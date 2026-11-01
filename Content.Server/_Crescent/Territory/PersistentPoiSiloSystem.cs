using System.Linq;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Shared._Crescent.Territory;
using Content.Shared.Administration.Logs;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiSiloSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly ConstructionSystem _construction = default!;
    [Dependency] private readonly PersistentPoiBuildSystem _build = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly StackSystem _stack = default!;

    private static readonly EntProtoId BuildAction = "ActionPersistentPoiBuild";

    private const float Reach = 15f;

    private readonly List<PendingBuild> _pending = new();
    private HashSet<ProtoId<ConstructionPrototype>>? _recipes;
    private HashSet<ProtoId<StackPrototype>>? _accepted;

    // set while ChangeNode swaps a frame for its next entity
    private EntityUid? _changing;
    private EntityUid? _changedTo;

    private sealed class PendingBuild
    {
        public EntityUid User;
        public EntityUid Grid;
        public ConstructionPrototype Recipe = default!;
        public EntityCoordinates Location;
        public Direction Direction;
        public Dictionary<ProtoId<StackPrototype>, int> Costs = new();
        public TimeSpan ReadyAt;
        public EntityUid Effect;
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiMaterialSiloComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<PersistentPoiMaterialSiloComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<PersistentPoiMaterialSiloComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
        SubscribeLocalEvent<ConstructionChangeEntityEvent>(OnChangeEntity);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ =>
        {
            _recipes = null;
            _accepted = null;
        });
        SubscribeNetworkEvent<PersistentPoiSiloBuildRequestEvent>(OnBuildRequest);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _pending.Clear());
    }

    #region Storage

    private HashSet<ProtoId<StackPrototype>> Accepted
    {
        get
        {
            if (_accepted != null)
                return _accepted;

            _accepted = new HashSet<ProtoId<StackPrototype>>();
            foreach (var id in Recipes)
            {
                if (!_proto.TryIndex(id, out var recipe))
                    continue;

                foreach (var material in PersistentPoiSilo.GetCosts(_proto, recipe, out _, out _).Keys)
                {
                    _accepted.Add(material);
                }
            }

            return _accepted;
        }
    }

    private HashSet<ProtoId<ConstructionPrototype>> Recipes
    {
        get
        {
            if (_recipes != null)
                return _recipes;

            _recipes = new HashSet<ProtoId<ConstructionPrototype>>();
            if (_proto.TryIndex(BuildAction, out var action) &&
                action.TryGetComponent<PersistentPoiBuildMenuComponent>(out var menu, _factory))
            {
                foreach (var category in menu.Categories)
                {
                    _recipes.UnionWith(category.Recipes);
                }
            }

            return _recipes;
        }
    }

    private bool MayUse(EntityUid user, EntityUid silo)
    {
        return Transform(silo).GridUid is { } grid &&
               TryComp<PersistentPoiGridComponent>(grid, out var territory) &&
               PersistentTerritoryFactions.Holds(EntityManager, user, territory);
    }

    private void OnInteractUsing(Entity<PersistentPoiMaterialSiloComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        var storage = CompOrNull<StorageComponent>(args.Used);
        if (storage == null && !HasComp<StackComponent>(args.Used))
            return;

        args.Handled = true;

        if (!MayUse(args.User, ent))
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-silo-not-holder"), ent, args.User, PopupType.SmallCaution);
            return;
        }

        var stored = TryStore(ent, args.Used);
        if (storage != null)
        {
            foreach (var item in storage.Container.ContainedEntities.ToArray())
            {
                stored += TryStore(ent, item);
            }
        }

        _popup.PopupEntity(Loc.GetString(stored > 0 ? "persistent-poi-silo-stored" : "persistent-poi-silo-refused",
            ("count", stored)), ent, args.User);
    }

    private int TryStore(Entity<PersistentPoiMaterialSiloComponent> silo, EntityUid item)
    {
        if (!TryComp<StackComponent>(item, out var stack) ||
            stack.Count <= 0 ||
            EntityManager.IsQueuedForDeletion(item) ||
            !Accepted.Contains(stack.StackTypeId))
        {
            return 0;
        }

        // two interactions in one tick would otherwise both bank the stack before it is gone
        var count = stack.Count;
        silo.Comp.Stored[stack.StackTypeId] = silo.Comp.Stored.GetValueOrDefault(stack.StackTypeId) + count;
        Dirty(silo);
        _stack.SetCount(item, 0, stack);
        QueueDel(item);
        return count;
    }

    private void OnExamined(Entity<PersistentPoiMaterialSiloComponent> ent, ref ExaminedEvent args)
    {
        using (args.PushGroup(nameof(PersistentPoiMaterialSiloComponent)))
        {
            args.PushMarkup(Loc.GetString("persistent-poi-silo-examine-turning"));

            if (ent.Comp.Stored.Count == 0)
            {
                args.PushMarkup(Loc.GetString("persistent-poi-silo-examine-empty"));
                return;
            }

            foreach (var (material, count) in ent.Comp.Stored)
            {
                if (count > 0 && _proto.TryIndex(material, out var stack))
                    args.PushMarkup(Loc.GetString("persistent-poi-silo-examine-line", ("material", Loc.GetString(stack.Name)), ("count", count)));
            }
        }
    }

    private void OnGetVerbs(Entity<PersistentPoiMaterialSiloComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || !MayUse(args.User, ent))
            return;

        var user = args.User;
        foreach (var (material, count) in ent.Comp.Stored)
        {
            if (count <= 0 || !_proto.TryIndex(material, out var stack))
                continue;

            var id = material;
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("persistent-poi-silo-take", ("material", Loc.GetString(stack.Name))),
                Category = VerbCategory.Eject,
                Act = () => Take(ent, user, id),
            });
        }
    }

    private void Take(Entity<PersistentPoiMaterialSiloComponent> ent, EntityUid user, ProtoId<StackPrototype> material)
    {
        if (!MayUse(user, ent) || !_proto.TryIndex(material, out var stack))
            return;

        var stored = ent.Comp.Stored.GetValueOrDefault(material);
        var amount = Math.Min(stored, stack.MaxCount ?? _stack.GetMaxCount(stack.Spawn));
        if (amount <= 0)
            return;

        if (stored - amount > 0)
            ent.Comp.Stored[material] = stored - amount;
        else
            ent.Comp.Stored.Remove(material);
        Dirty(ent);

        var spawned = _stack.Spawn(amount, stack, Transform(ent).Coordinates);
        _hands.PickupOrDrop(user, spawned);
    }

    public void Spill(Entity<PersistentPoiMaterialSiloComponent> ent)
    {
        var coordinates = Transform(ent).Coordinates;
        foreach (var (material, amount) in ent.Comp.Stored)
        {
            if (amount > 0 && _proto.TryIndex(material, out var stack))
                _stack.SpawnMultiple(stack.Spawn, amount, coordinates);
        }

        ent.Comp.Stored.Clear();
        Dirty(ent);
    }

    /// <summary>
    /// Banks materials into the territory's silo; false when it has none.
    /// </summary>
    public bool TryDeposit(EntityUid grid, Dictionary<ProtoId<StackPrototype>, int> materials)
    {
        if (!PersistentPoiSilo.TryGetSilo(EntityManager, grid, out var silo))
            return false;

        foreach (var (material, amount) in materials)
        {
            if (amount > 0)
                silo.Comp.Stored[material] = silo.Comp.Stored.GetValueOrDefault(material) + amount;
        }

        Dirty(silo);
        return true;
    }

    private void Withdraw(PersistentPoiMaterialSiloComponent silo, Dictionary<ProtoId<StackPrototype>, int> costs)
    {
        foreach (var (material, amount) in costs)
        {
            var left = silo.Stored.GetValueOrDefault(material) - amount;
            if (left > 0)
                silo.Stored[material] = left;
            else
                silo.Stored.Remove(material);
        }
    }

    private void Refund(PendingBuild build)
    {
        if (PersistentPoiSilo.TryGetSilo(EntityManager, build.Grid, out var silo))
        {
            foreach (var (material, amount) in build.Costs)
            {
                silo.Comp.Stored[material] = silo.Comp.Stored.GetValueOrDefault(material) + amount;
            }

            Dirty(silo);
            return;
        }

        if (!build.Location.IsValid(EntityManager))
            return;

        foreach (var (material, amount) in build.Costs)
        {
            if (_proto.TryIndex(material, out var stack))
                _stack.SpawnMultiple(stack.Spawn, amount, build.Location);
        }
    }

    #endregion

    #region Building

    private void OnBuildRequest(PersistentPoiSiloBuildRequestEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is { } user)
            TryBuild(user, ev.Recipe, GetCoordinates(ev.Location), ev.Direction);
    }

    /// <summary>
    /// Builds a build-menu recipe straight from the territory's silo, after an RCD-style build-up.
    /// </summary>
    public bool TryBuild(EntityUid user, ProtoId<ConstructionPrototype> recipeId, EntityCoordinates location, Direction direction)
    {
        if (string.IsNullOrEmpty(recipeId.Id) ||
            !_build.CanBuild(user) ||
            !Recipes.Contains(recipeId) ||
            !_proto.TryIndex(recipeId, out var recipe) ||
            recipe.Type != ConstructionType.Structure)
        {
            return false;
        }

        var userXform = Transform(user);
        if (!location.IsValid(EntityManager) ||
            _transform.GetGrid(location) is not { } grid ||
            grid != userXform.GridUid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            !_transform.InRange(location, PersistentPoiMapping.GetReachOrigin(EntityManager, user, Reach, out var reach), reach))
        {
            return false;
        }

        var status = _levels.GetRecipeStatus((grid, territory), recipe, PersistentTerritoryFactions.GetTeam(EntityManager, user));
        if (status.Block != PersistentPoiBuildBlock.None)
            return false;

        var costs = PersistentPoiSilo.GetCosts(_proto, recipe, out var materialsOnly, out var doAfter);
        if (!materialsOnly || costs.Count == 0)
        {
            _popup.PopupCoordinates(Loc.GetString("persistent-poi-silo-hand-only"), location, user);
            return false;
        }

        if (!PersistentPoiSilo.TryGetSilo(EntityManager, grid, out var silo) || !PersistentPoiSilo.Covers(silo.Comp, costs))
        {
            _popup.PopupCoordinates(Loc.GetString("persistent-poi-silo-short"), location, user);
            return false;
        }

        foreach (var pending in _pending)
        {
            if (pending.Recipe == recipe && pending.Grid == grid && pending.Location.Equals(location))
                return false;
        }

        if (!CheckConditions(recipe, user, location, direction, true))
            return false;

        Withdraw(silo.Comp, costs);
        Dirty(silo);

        var seconds = Math.Clamp((int) MathF.Round(doAfter / 2f), 1, 4);
        _pending.Add(new PendingBuild
        {
            User = user,
            Grid = grid,
            Recipe = recipe,
            Location = location,
            Direction = direction,
            Costs = costs,
            ReadyAt = _timing.CurTime + TimeSpan.FromSeconds(seconds),
            Effect = Spawn($"EffectRCDConstruct{seconds}", location),
        });

        return true;
    }

    private bool CheckConditions(ConstructionPrototype recipe, EntityUid user, EntityCoordinates location, Direction direction, bool popup)
    {
        foreach (var condition in recipe.Conditions)
        {
            if (condition.Condition(user, location, direction))
                continue;

            if (popup && condition.GenerateGuideEntry()?.Localization is { } message)
                _popup.PopupCoordinates(Loc.GetString(message), location, user);

            return false;
        }

        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pending.Count == 0)
            return;

        var now = _timing.CurTime;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var build = _pending[i];
            if (now < build.ReadyAt)
                continue;

            _pending.RemoveAt(i);
            Finish(build);
        }
    }

    private void Finish(PendingBuild build)
    {
        if (!TerminatingOrDeleted(build.Effect))
            QueueDel(build.Effect);

        // the user may have died or left since; the tile must still take it either way
        if (TerminatingOrDeleted(build.Grid) ||
            !build.Location.IsValid(EntityManager) ||
            Exists(build.User) && !CheckConditions(build.Recipe, build.User, build.Location, build.Direction, false))
        {
            Refund(build);
            return;
        }

        var user = Exists(build.User) ? build.User : (EntityUid?) null;
        if (Assemble(build.Recipe, user, build.Location, build.Direction) is not { } built)
        {
            Refund(build);
            return;
        }

        if (user != null)
        {
            _adminLog.Add(LogType.Construction, LogImpact.Low,
                $"{ToPrettyString(user.Value):player} built {ToPrettyString(built)} from the material silo of territory {ToPrettyString(build.Grid)}");
        }
    }

    /// <summary>
    /// Walks the recipe's graph from start to finish in one go, running the same actions the hand steps would.
    /// </summary>
    private EntityUid? Assemble(ConstructionPrototype recipe, EntityUid? user, EntityCoordinates location, Direction direction)
    {
        if (!_proto.TryIndex(recipe.Graph, out var graph) ||
            !graph.Nodes.TryGetValue(recipe.StartNode, out var node) ||
            graph.Path(recipe.StartNode, recipe.TargetNode) is not { Length: > 0 } path)
        {
            return null;
        }

        EntityUid? uid = null;
        foreach (var next in path)
        {
            if (node.GetEdge(next.Name) is not { } edge)
                return uid;

            // the first edge raises the frame and then completes on it, as hand construction does
            if (uid == null)
            {
                if (next.Entity.GetId(null, user, new(EntityManager)) is not { } proto)
                    return null;

                var angle = recipe.CanRotate ? direction.ToAngle() : Angle.Zero;
                uid = SpawnAttachedTo(proto, location, rotation: angle);
                if (TryComp<ConstructionComponent>(uid, out var construction))
                    _construction.SetPathfindingTarget(uid.Value, recipe.TargetNode, construction);
            }

            foreach (var step in edge.Steps)
            {
                _construction.PerformActions(uid.Value, user, step.Completed);
            }

            _construction.PerformActions(uid.Value, user, edge.Completed);
            if (TerminatingOrDeleted(uid.Value))
                return null;

            if (next != path[0])
            {
                _changing = uid;
                _changedTo = null;
                _construction.ChangeNode(uid.Value, user, next.Name);
                _changing = null;
                uid = _changedTo ?? uid;

                if (TerminatingOrDeleted(uid.Value))
                    return null;
            }

            node = next;
        }

        return uid;
    }

    private void OnChangeEntity(ConstructionChangeEntityEvent ev)
    {
        if (_changing == ev.Old)
            _changedTo = ev.New;
    }

    #endregion
}
