using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Construction;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Lathe.Components;
using Content.Server.Materials;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Stack;
using Content.Shared.Atmos;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Construction;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared.Database;
using Content.Shared.Emag.Components;
using Content.Shared.Examine;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Power;
using Content.Shared.ReagentSpeed;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using JetBrains.Annotations;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;

namespace Content.Server.Lathe
{
    [UsedImplicitly]
    public sealed class LatheSystem : SharedLatheSystem
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPrototypeManager _proto = default!;
        [Dependency] private readonly IComponentFactory _componentFactory = default!;
        [Dependency] private readonly IAdminLogManager _adminLogger = default!;
        [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly ContainerSystem _container = default!;
        [Dependency] private readonly UserInterfaceSystem _uiSys = default!;
        [Dependency] private readonly MaterialStorageSystem _materialStorage = default!;
        [Dependency] private readonly PopupSystem _popup = default!;
        [Dependency] private readonly PuddleSystem _puddle = default!;
        [Dependency] private readonly ReagentSpeedSystem _reagentSpeed = default!;
        [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;
        [Dependency] private readonly StackSystem _stack = default!;
        [Dependency] private readonly TransformSystem _transform = default!;
        [Dependency] private readonly ChatSystem _chatSystem = default!; // Goobstation - New recipes message

        /// <summary>
        /// Per-tick cache
        /// </summary>
        private readonly List<GasMixture> _environments = new();

        /// <summary>
        /// Lathes that wanted a UI update but got rate limited, flushed in <see cref="Update"/>.
        /// </summary>
        private readonly HashSet<EntityUid> _pendingUiUpdates = new();

        /// <summary>
        /// Reusable snapshot buffer for <see cref="FlushPendingUiUpdates"/>.
        /// </summary>
        private readonly List<EntityUid> _uiUpdateBuffer = new();

        private static readonly TimeSpan UiUpdateInterval = TimeSpan.FromSeconds(0.5);

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<LatheComponent, GetMaterialWhitelistEvent>(OnGetWhitelist);
            SubscribeLocalEvent<LatheComponent, MapInitEvent>(OnMapInit);
            SubscribeLocalEvent<LatheComponent, PowerChangedEvent>(OnPowerChanged);
            SubscribeLocalEvent<LatheComponent, TechnologyDatabaseModifiedEvent>(OnDatabaseModified);
            SubscribeLocalEvent<LatheComponent, ResearchRegistrationChangedEvent>(OnResearchRegistrationChanged);
            SubscribeLocalEvent<LatheComponent, RefreshPartsEvent>(OnPartsRefresh);
            SubscribeLocalEvent<LatheComponent, UpgradeExamineEvent>(OnUpgradeExamine);
            SubscribeLocalEvent<LatheComponent, MachineDeconstructedEvent>(OnDeconstructed);

            SubscribeLocalEvent<LatheComponent, LatheQueueRecipeMessage>(OnLatheQueueRecipeMessage);
            SubscribeLocalEvent<LatheComponent, LatheSyncRequestMessage>(OnLatheSyncRequestMessage);

            SubscribeLocalEvent<LatheComponent, BeforeActivatableUIOpenEvent>((u, c, _) => UpdateUserInterfaceState(u, c, true));
            SubscribeLocalEvent<LatheComponent, MaterialAmountChangedEvent>(OnMaterialAmountChanged);
            SubscribeLocalEvent<TechnologyDatabaseComponent, LatheGetRecipesEvent>(OnGetRecipes);
            SubscribeLocalEvent<EmagLatheRecipesComponent, LatheGetRecipesEvent>(GetEmagLatheRecipes);
            SubscribeLocalEvent<LatheHeatProducingComponent, LatheStartPrintingEvent>(OnHeatStartPrinting);
        }
        public override void Update(float frameTime)
        {
            var query = EntityQueryEnumerator<LatheProducingComponent, LatheComponent>();
            while (query.MoveNext(out var uid, out var comp, out var lathe))
            {
                if (lathe.CurrentRecipe == null)
                    continue;

                if (_timing.CurTime - comp.StartTime >= comp.ProductionLength)
                    FinishProducing(uid, lathe);
            }

            var heatQuery = EntityQueryEnumerator<LatheHeatProducingComponent, LatheProducingComponent, TransformComponent>();
            while (heatQuery.MoveNext(out var uid, out var heatComp, out _, out var xform))
            {
                if (_timing.CurTime < heatComp.NextSecond)
                    continue;
                heatComp.NextSecond += TimeSpan.FromSeconds(1);

                var position = _transform.GetGridTilePositionOrDefault((uid, xform));
                _environments.Clear();

                if (_atmosphere.GetTileMixture(xform.GridUid, xform.MapUid, position, true) is { } tileMix)
                    _environments.Add(tileMix);

                if (xform.GridUid != null)
                {
                    var enumerator = _atmosphere.GetAdjacentTileMixtures(xform.GridUid.Value, position, false, true);
                    while (enumerator.MoveNext(out var mix))
                    {
                        _environments.Add(mix);
                    }
                }

                if (_environments.Count > 0)
                {
                    var heatPerTile = heatComp.EnergyPerSecond / _environments.Count;
                    foreach (var env in _environments)
                    {
                        _atmosphere.AddHeat(env, heatPerTile);
                    }
                }
            }

            FlushPendingUiUpdates();
        }

        private void FlushPendingUiUpdates()
        {
            if (_pendingUiUpdates.Count == 0)
                return;

            // Snapshot into a reusable buffer rather than a fresh array: pushing an update removes the lathe
            // from the set as it goes, and this runs every tick for as long as anything is printing.
            _uiUpdateBuffer.Clear();
            _uiUpdateBuffer.AddRange(_pendingUiUpdates);

            foreach (var uid in _uiUpdateBuffer)
            {
                // Dropped either because the lathe is gone or because the last viewer closed the UI while the
                // update was still queued. Without this the entry would sit here and eventually push a full
                // recipe catalogue at nobody.
                if (!TryComp<LatheComponent>(uid, out var lathe) || !_uiSys.IsUiOpen(uid, LatheUiKey.Key))
                {
                    _pendingUiUpdates.Remove(uid);
                    continue;
                }

                if (_timing.CurTime < lathe.NextUiUpdate)
                    continue;

                UpdateUserInterfaceState(uid, lathe, true);
            }
        }

        private void OnGetWhitelist(EntityUid uid, LatheComponent component, ref GetMaterialWhitelistEvent args)
        {
            if (args.Storage != uid)
                return;
            var materialWhitelist = new List<ProtoId<MaterialPrototype>>();
            var recipes = GetAvailableRecipes(uid, component, true);
            foreach (var id in recipes)
            {
                if (!_proto.TryIndex(id, out var proto))
                    continue;
                foreach (var (mat, _) in proto.Materials)
                {
                    if (!materialWhitelist.Contains(mat))
                    {
                        materialWhitelist.Add(mat);
                    }
                }
            }

            var combined = args.Whitelist.Union(materialWhitelist).ToList();
            args.Whitelist = combined;
        }

        [PublicAPI]
        public bool TryGetAvailableRecipes(EntityUid uid, [NotNullWhen(true)] out List<ProtoId<LatheRecipePrototype>>? recipes, [NotNullWhen(true)] LatheComponent? component = null, bool getUnavailable = false)
        {
            recipes = null;
            if (!Resolve(uid, ref component))
                return false;
            recipes = GetAvailableRecipes(uid, component, getUnavailable);
            return true;
        }

        /// <summary>
        /// The recipes this lathe can make. The returned list is the caller's own copy; the cached original
        /// backs every later call, so handing it out would let one caller's edit change what everyone else sees.
        /// Internal hot paths use <see cref="EnsureCachedRecipes"/> and skip the copy.
        /// </summary>
        public List<ProtoId<LatheRecipePrototype>> GetAvailableRecipes(EntityUid uid, LatheComponent component, bool getUnavailable = false)
        {
            // The "everything, available or not" query is only used for whitelist building, so it isn't cached.
            if (!getUnavailable)
                return new List<ProtoId<LatheRecipePrototype>>(EnsureCachedRecipes(uid, component));

            var ev = new LatheGetRecipesEvent(uid, true)
            {
                Recipes = new List<ProtoId<LatheRecipePrototype>>(component.StaticRecipes)
            };
            RaiseLocalEvent(uid, ev);

            ev.Recipes.RemoveAll(component.ExcludedRecipes.Contains);

            return ev.Recipes;
        }

        /// <summary>
        /// The live cached recipe list, rebuilt only when something invalidated it. Never hand this to a caller
        /// that might modify it; see <see cref="GetAvailableRecipes"/>.
        /// </summary>
        private List<ProtoId<LatheRecipePrototype>> EnsureCachedRecipes(EntityUid uid, LatheComponent component)
        {
            if (component.CachedRecipes is { } cached)
                return cached;

            var ev = new LatheGetRecipesEvent(uid, false)
            {
                Recipes = new List<ProtoId<LatheRecipePrototype>>(component.StaticRecipes)
            };
            RaiseLocalEvent(uid, ev);

            ev.Recipes.RemoveAll(component.ExcludedRecipes.Contains);

            component.CachedRecipes = ev.Recipes;
            component.CachedRecipeLookup = new HashSet<ProtoId<LatheRecipePrototype>>(ev.Recipes);

            return ev.Recipes;
        }

        public static List<ProtoId<LatheRecipePrototype>> GetAllBaseRecipes(LatheComponent component)
        {
            return component.StaticRecipes
                .Union(component.DynamicRecipes)
                .Except(component.ExcludedRecipes)
                .ToList();
        }

        public bool TryAddToQueue(EntityUid uid, LatheRecipePrototype recipe, LatheComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return false;

            if (!CanProduce(uid, recipe, 1, component))
                return false;

            foreach (var (mat, amount) in recipe.Materials)
            {
                var adjustedAmount = recipe.ApplyMaterialDiscount
                    ? (int) (-amount * component.MaterialUseMultiplier)
                    : -amount;

                _materialStorage.TryChangeMaterialAmount(uid, mat, adjustedAmount);
            }
            component.Queue.Add(recipe);

            return true;
        }

        public bool TryStartProducing(EntityUid uid, LatheComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return false;
            if (component.CurrentRecipe != null || component.Queue.Count <= 0 || !this.IsPowered(uid, EntityManager))
                return false;

            var recipe = component.Queue.First();
            component.Queue.RemoveAt(0);

            var time = _reagentSpeed.ApplySpeed(uid, recipe.CompleteTime) * component.TimeMultiplier;

            var wasProducing = HasComp<LatheProducingComponent>(uid);
            var lathe = EnsureComp<LatheProducingComponent>(uid);
            lathe.StartTime = _timing.CurTime;
            lathe.ProductionLength = time;
            component.CurrentRecipe = recipe;

            var ev = new LatheStartPrintingEvent(recipe);
            RaiseLocalEvent(uid, ref ev);

            // A batch lathe is doing one job as far as the player is concerned, so it gets one start-up
            // sound rather than one per item; a few hundred overlapping copies is both noise and traffic.
            if (!component.BatchOutput || !wasProducing)
                _audio.PlayPvs(component.ProducingSound, uid);

            UpdateRunningAppearance(uid, true);
            UpdateUserInterfaceState(uid, component);

            if (time == TimeSpan.Zero)
            {
                FinishProducing(uid, component, lathe);
            }
            return true;
        }

        public void FinishProducing(EntityUid uid, LatheComponent? comp = null, LatheProducingComponent? prodComp = null)
        {
            if (!Resolve(uid, ref comp, ref prodComp, false))
                return;

            if (comp.CurrentRecipe != null)
            {
                if (comp.CurrentRecipe.Result is { } resultProto)
                {
                    if (comp.BatchOutput)
                    {
                        // Tallied now, handed over once the queue runs dry. See FlushPendingOutput.
                        comp.PendingOutput[resultProto] = comp.PendingOutput.GetValueOrDefault(resultProto) + 1;
                    }
                    else
                    {
                        var result = Spawn(resultProto, Transform(uid).Coordinates);
                        _stack.TryMergeToContacts(result);
                    }
                }

                if (comp.CurrentRecipe.ResultReagents is { } resultReagents &&
                    comp.ReagentOutputSlotId is { } slotId)
                {
                    var toAdd = new Solution(
                        resultReagents.Select(p => new ReagentQuantity(p.Key.Id, p.Value, null)));

                    if (_container.TryGetContainer(uid, slotId, out var container) &&
                        container.ContainedEntities.Count == 1)
                    {
                        var held = container.ContainedEntities[0];
                        if (_solution.TryGetFitsInDispenser(held, out var solution, out _) ||
                            _solution.TryGetRefillableSolution(held, out solution, out _))
                        {
                            var added = _solution.AddSolution(solution.Value, toAdd.Clone());
                            toAdd.SplitSolution(added);
                        }
                    }

                    if (toAdd.Volume > 0)
                        DispenseReagentOverflow(uid, comp, toAdd);
                }
            }

            var completedRecipe = comp.CurrentRecipe;
            comp.CurrentRecipe = null;
            prodComp.StartTime = _timing.CurTime;

            if (completedRecipe != null)
                OnProductionComplete(uid, completedRecipe);

            if (!TryStartProducing(uid, comp))
            {
                // Nothing left to make (or no power to make it with), so the run is over and whatever it
                // built gets handed over.
                FlushPendingOutput(uid, comp);
                RemCompDeferred(uid, prodComp);
                UpdateUserInterfaceState(uid, comp);
                UpdateRunningAppearance(uid, false);
            }
        }

        private void DispenseReagentOverflow(EntityUid uid, LatheComponent comp, Solution toAdd)
        {
            if (comp.ReagentOutputFallback is { } fallback)
            {
                var coords = Transform(uid).Coordinates;
                while (toAdd.Volume > 0)
                {
                    var bottle = Spawn(fallback, coords);
                    // Free glass from biomass otherwise.
                    RemComp<PhysicalCompositionComponent>(bottle);

                    if (!_solution.TryGetRefillableSolution(bottle, out var soln, out _) ||
                        soln.Value.Comp.Solution.AvailableVolume <= 0)
                    {
                        Del(bottle);
                        break;
                    }

                    toAdd.SplitSolution(_solution.AddSolution(soln.Value, toAdd.Clone()));
                }

                if (toAdd.Volume <= 0)
                    return;
            }

            _popup.PopupEntity(Loc.GetString("lathe-reagent-dispense-no-container", ("name", uid)), uid);
            _puddle.TrySpillAt(uid, toAdd, out _);
        }

        /// <summary>
        /// Drops everything a batch run built, merged into as few stacks as the stack size allows. A hundred
        /// queued sheets leave as a handful of full stacks rather than a hundred one-sheet entities that then
        /// have to find each other.
        /// </summary>
        public void FlushPendingOutput(EntityUid uid, LatheComponent? component = null)
        {
            if (!Resolve(uid, ref component, false) || component.PendingOutput.Count == 0)
                return;

            var coords = Transform(uid).Coordinates;

            foreach (var (resultProto, count) in component.PendingOutput)
            {
                if (!_proto.TryIndex<EntityPrototype>(resultProto, out var proto))
                    continue;

                // A recipe's result prototype can itself be a stack of more than one, so the run's total is
                // counted in stack units, not in entities.
                if (proto.TryGetComponent<StackComponent>(out var stackProto, _componentFactory))
                {
                    foreach (var spawned in _stack.SpawnMultiple(resultProto, stackProto.Count * count, coords))
                    {
                        _stack.TryMergeToContacts(spawned);
                    }
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        Spawn(resultProto, coords);
                    }
                }
            }

            component.PendingOutput.Clear();
        }

        public void OnProductionComplete(EntityUid uid, LatheRecipePrototype recipe)
        {
            var ev = new LatheProduceCompleteEvent(uid, recipe);
            RaiseLocalEvent(uid, ev);
        }

        /// <summary>
        /// Pushes a fresh UI state. Each state carries the lathe's whole recipe list, which is well over a
        /// thousand entries on the bigger lathes, so bulk work (queueing, finishing an item) only asks for an
        /// update and gets coalesced here rather than re-sending the catalogue per item.
        /// </summary>
        /// <param name="immediate">Bypass the rate limit. Used when a player is waiting on the result, e.g. opening the UI.</param>
        public void UpdateUserInterfaceState(EntityUid uid, LatheComponent? component = null, bool immediate = false)
        {
            if (!Resolve(uid, ref component))
                return;

            // BeforeActivatableUIOpenEvent fires before the UI counts as open, so the immediate path has to
            // skip both of these checks or the state would never be seeded for the player opening it.
            if (!immediate)
            {
                // Nobody's looking, so there's nothing to update. The UI refreshes itself on open.
                if (!_uiSys.IsUiOpen(uid, LatheUiKey.Key))
                {
                    _pendingUiUpdates.Remove(uid);
                    return;
                }

                if (_timing.CurTime < component.NextUiUpdate)
                {
                    _pendingUiUpdates.Add(uid);
                    return;
                }
            }

            _pendingUiUpdates.Remove(uid);
            component.NextUiUpdate = _timing.CurTime + UiUpdateInterval;

            var producing = component.CurrentRecipe ?? component.Queue.FirstOrDefault();

            // Safe to alias the cache here: invalidation replaces the list rather than clearing it in place, so
            // a state still waiting to be serialized keeps the roster it was built with.
            var state = new LatheUpdateState(EnsureCachedRecipes(uid, component), BuildQueueSummary(component.Queue), producing);
            _uiSys.SetUiState(uid, LatheUiKey.Key, state);
        }

        /// <summary>
        /// Collapses the queue into runs of the same recipe. Sending it job by job means sending a whole
        /// recipe prototype per job, so a 500 item order would put a hundred kilobytes on the wire every time
        /// the interface refreshed.
        /// </summary>
        private static List<LatheQueueEntry> BuildQueueSummary(List<LatheRecipePrototype> queue)
        {
            var summary = new List<LatheQueueEntry>();

            foreach (var recipe in queue)
            {
                if (summary.Count > 0 && summary[^1].Recipe.Id == recipe.ID)
                {
                    var last = summary[^1];
                    last.Count++;
                    summary[^1] = last;
                    continue;
                }

                summary.Add(new LatheQueueEntry(recipe.ID, 1));
            }

            return summary;
        }

        private void OnGetRecipes(EntityUid uid, TechnologyDatabaseComponent component, LatheGetRecipesEvent args)
        {
            if (uid != args.Lathe || !TryComp<LatheComponent>(uid, out var latheComponent))
                return;

            foreach (var recipe in latheComponent.DynamicRecipes)
            {
                if (!(args.getUnavailable || component.UnlockedRecipes.Contains(recipe)) || args.Recipes.Contains(recipe))
                    continue;
                args.Recipes.Add(recipe);
            }
        }

        private void GetEmagLatheRecipes(EntityUid uid, EmagLatheRecipesComponent component, LatheGetRecipesEvent args)
        {
            if (uid != args.Lathe || !TryComp<TechnologyDatabaseComponent>(uid, out var technologyDatabase))
                return;
            if (!args.getUnavailable && !HasComp<EmaggedComponent>(uid))
                return;
            foreach (var recipe in component.EmagDynamicRecipes)
            {
                if (!(args.getUnavailable || technologyDatabase.UnlockedRecipes.Contains(recipe)) || args.Recipes.Contains(recipe))
                    continue;
                args.Recipes.Add(recipe);
            }
            foreach (var recipe in component.EmagStaticRecipes)
            {
                args.Recipes.Add(recipe);
            }
        }

        private void OnHeatStartPrinting(EntityUid uid, LatheHeatProducingComponent component, LatheStartPrintingEvent args)
        {
            component.NextSecond = _timing.CurTime;
        }

        private void OnMaterialAmountChanged(EntityUid uid, LatheComponent component, ref MaterialAmountChangedEvent args)
        {
            UpdateUserInterfaceState(uid, component);
        }

        /// <summary>
        /// Initialize the UI and appearance.
        /// Appearance requires initialization or the layers break
        /// </summary>
        private void OnMapInit(EntityUid uid, LatheComponent component, MapInitEvent args)
        {
            _appearance.SetData(uid, LatheVisuals.IsInserting, false);
            _appearance.SetData(uid, LatheVisuals.IsRunning, false);

            _materialStorage.UpdateMaterialWhitelist(uid);
        }

        /// <summary>
        /// Sets the machine sprite to either play the running animation
        /// or stop.
        /// </summary>
        private void UpdateRunningAppearance(EntityUid uid, bool isRunning)
        {
            _appearance.SetData(uid, LatheVisuals.IsRunning, isRunning);
        }

        private void OnPowerChanged(EntityUid uid, LatheComponent component, ref PowerChangedEvent args)
        {
            if (!args.Powered)
            {
                RemComp<LatheProducingComponent>(uid);
                UpdateRunningAppearance(uid, false);
                // A dead machine shouldn't sit on a half-finished batch: the queue survives the outage, but
                // what it already built comes out now rather than being held until it can finish.
                FlushPendingOutput(uid, component);
            }
            else if (component.CurrentRecipe != null)
            {
                EnsureComp<LatheProducingComponent>(uid);
                TryStartProducing(uid, component);
            }
        }

        private void OnDatabaseModified(EntityUid uid, LatheComponent component, ref TechnologyDatabaseModifiedEvent args)
        {
            InvalidateRecipeCache(uid, component);
            UpdateUserInterfaceState(uid, component);

            // Goobstation - Lathe message on recipes update - Start
            if (args.UnlockedRecipes == null || args.UnlockedRecipes.Count == 0)
                return;

            var recipesCount = args.UnlockedRecipes.Count(recipe => component.DynamicRecipes.Contains(recipe));
            if (recipesCount > 0)
                _chatSystem.TrySendInGameICMessage(uid, Loc.GetString("lathe-technology-recipes-update-message", ("count", recipesCount)), InGameICChatType.Speak, hideChat: true);
            // Goobstation - Lathe message on recipes update - End
        }

        private void OnResearchRegistrationChanged(EntityUid uid, LatheComponent component, ref ResearchRegistrationChangedEvent args)
        {
            InvalidateRecipeCache(uid, component);
            UpdateUserInterfaceState(uid, component);
        }

        private void OnPartsRefresh(EntityUid uid, LatheComponent component, RefreshPartsEvent args)
        {
            // The prototype's own multipliers are the baseline that part ratings scale off of, otherwise
            // stock parts would reset variants like the industrial ore processor back to 1x.
            component.BaseTimeMultiplier ??= component.TimeMultiplier;
            component.BaseMaterialUseMultiplier ??= component.MaterialUseMultiplier;

            var printTimeRating = args.GetRating(component.MachinePartPrintSpeed);
            var materialUseRating = args.GetRating(component.MachinePartMaterialUse);

            component.TimeMultiplier = component.BaseTimeMultiplier.Value *
                                       MathF.Pow(component.PartRatingPrintTimeMultiplier, printTimeRating - 1);
            component.MaterialUseMultiplier = component.BaseMaterialUseMultiplier.Value *
                                              MathF.Pow(component.PartRatingMaterialUseMultiplier, materialUseRating - 1);
            Dirty(uid, component);
        }

        private void OnDeconstructed(EntityUid uid, LatheComponent component, MachineDeconstructedEvent args)
        {
            FlushPendingOutput(uid, component);
        }

        private void OnUpgradeExamine(EntityUid uid, LatheComponent component, UpgradeExamineEvent args)
        {
            args.AddPercentageUpgrade("lathe-component-upgrade-speed", 1 / component.TimeMultiplier);
            args.AddPercentageUpgrade("lathe-component-upgrade-material-use", component.MaterialUseMultiplier);
        }

        protected override bool HasRecipe(EntityUid uid, LatheRecipePrototype recipe, LatheComponent component)
        {
            EnsureCachedRecipes(uid, component);
            return component.CachedRecipeLookup?.Contains(recipe.ID) == true;
        }

        #region UI Messages

        private void OnLatheQueueRecipeMessage(EntityUid uid, LatheComponent component, LatheQueueRecipeMessage args)
        {
            if (_proto.TryIndex(args.ID, out LatheRecipePrototype? recipe))
            {
                var count = 0;
                var clampedQuantity = Math.Min(args.Quantity, component.MaxQueuePerRequest);
                for (var i = 0; i < clampedQuantity; i++)
                {
                    if (TryAddToQueue(uid, recipe, component))
                        count++;
                    else
                        break;
                }
                if (count > 0)
                {
                    _adminLogger.Add(LogType.Action,
                        LogImpact.Low,
                        $"{ToPrettyString(args.Actor):player} queued {count} {GetRecipeName(recipe)} at {ToPrettyString(uid):lathe}");
                }
            }
            TryStartProducing(uid, component);
            // The player just clicked, so answer immediately rather than making them wait out the rate limit.
            UpdateUserInterfaceState(uid, component, true);
        }

        private void OnLatheSyncRequestMessage(EntityUid uid, LatheComponent component, LatheSyncRequestMessage args)
        {
            UpdateUserInterfaceState(uid, component, true);
        }
        #endregion
    }
}
