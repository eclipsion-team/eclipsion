using Content.Shared._Crescent.Economy;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Cargo.Systems;
using Content.Server.GameTicking;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Stack;
using Content.Shared._Crescent.Territory;
using Content.Shared.Audio;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiProductionSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly TerritoryPaymentSystem _payment = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PersistentPoiProgressionSystem _progression = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private const float TickInterval = 1f;

    private const float FullPowerRatio = 0.99f;

    private const float NoPowerRatio = 0.01f;

    private float _accumulator;

    private readonly HashSet<EntityUid> _onlineGrids = new();

    private readonly HashSet<EntityUid> _withinLimit = new();

    private readonly Dictionary<EntityUid, List<EntityUid>> _worksByGrid = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiProductionMachineComponent, MapInitEvent>(OnMachineMapInit);
        SubscribeLocalEvent<PersistentPoiProductionMachineComponent, ExaminedEvent>(OnMachineExamined);
        SubscribeLocalEvent<PersistentRegionOwnerChangedEvent>(OnOwnerChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => _batchValues.Clear());

        Subs.BuiEvents<PersistentPoiProductionConsoleComponent>(PersistentPoiProductionUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnConsoleOpened);
            subs.Event<PersistentPoiProductionToggleMessage>(OnToggle);
            subs.Event<PersistentPoiProductionSelectRecipeMessage>(OnSelectRecipe);
            subs.Event<PersistentPoiProductionDispenseMessage>(OnDispense);
            subs.Event<PersistentPoiProductionSetCapabilityMessage>(OnSetCapability);
            subs.Event<PersistentPoiProductionDecommissionMessage>(OnDecommission);
            subs.Event<PersistentPoiProductionRushMessage>(OnRush);
            subs.Event<PersistentPoiProductionOvertimeMessage>(OnOvertime);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < TickInterval)
            return;

        var elapsed = _accumulator;
        _accumulator = 0f;

        _onlineGrids.Clear();
        var consoles = EntityQueryEnumerator<PersistentPoiProductionConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid is { } grid && this.IsPowered(uid, EntityManager))
                _onlineGrids.Add(grid);
        }

        RefreshLimits();

        var machines = EntityQueryEnumerator<PersistentPoiProductionMachineComponent, TransformComponent>();
        while (machines.MoveNext(out var uid, out var machine, out var xform))
        {
            var blocker = GetBlocker(uid, machine, xform, out var recipe, out var level);
            var working = blocker == null && recipe != null;
            var power = DrawPower(uid, machine, working, out var starting);

            machine.PowerRatio = power;
            machine.Status = blocker ?? GetPowerStatus(power, starting);
            _ambient.SetAmbience(uid, working && power > NoPowerRatio);

            if (!working || power <= NoPowerRatio)
                continue;

            var rate = power >= FullPowerRatio ? 1f : power;
            machine.Progress += elapsed * (level?.ProductionSpeed ?? 1f) * GetOvertimeSpeed(xform.GridUid) * rate;
            if (machine.Progress < GetDuration(recipe!))
                continue;

            machine.Progress = 0f;
            machine.Stored++;
        }

        UpdateOpenConsoles();
    }

    private float DrawPower(EntityUid uid, PersistentPoiProductionMachineComponent machine, bool working, out bool starting)
    {
        starting = false;

        if (machine.PowerDraw <= 0f)
            return working ? 1f : 0f;

        if (!TryComp<PowerConsumerComponent>(uid, out var consumer))
            return working && this.IsPowered(uid, EntityManager) ? 1f : 0f;

        var wasDrawing = consumer.DrawRate > 0f;
        var draw = working ? machine.PowerDraw : 0f;
        if (!MathHelper.CloseTo(consumer.DrawRate, draw))
            consumer.DrawRate = draw;

        if (!working)
            return 0f;

        if (!wasDrawing)
        {
            starting = true;
            return 0f;
        }

        return Math.Clamp(consumer.ReceivedPower / draw, 0f, 1f);
    }

    private static PersistentPoiProductionStatus GetPowerStatus(float power, bool starting)
    {
        if (starting)
            return PersistentPoiProductionStatus.Starting;

        if (power >= FullPowerRatio)
            return PersistentPoiProductionStatus.Running;

        return power > NoPowerRatio
            ? PersistentPoiProductionStatus.LowPower
            : PersistentPoiProductionStatus.Unpowered;
    }

    private void RefreshLimits()
    {
        _withinLimit.Clear();
        _worksByGrid.Clear();

        var machines = EntityQueryEnumerator<PersistentPoiProductionMachineComponent, TransformComponent>();
        while (machines.MoveNext(out var uid, out var machine, out var xform))
        {
            if (xform.GridUid is not { } grid ||
                !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
                territory.Capability != machine.Capability)
            {
                continue;
            }

            if (!_worksByGrid.TryGetValue(grid, out var list))
            {
                list = new List<EntityUid>();
                _worksByGrid[grid] = list;
            }

            list.Add(uid);
        }

        foreach (var (grid, list) in _worksByGrid)
        {
            if (list.Count == 0 || !TryComp<PersistentPoiGridComponent>(grid, out var territory))
                continue;

            list.Sort();
            var limit = _levels.GetLimit(territory, PersistentPoiWorksCategory.Factory) ?? list.Count;
            for (var i = 0; i < Math.Min(limit, list.Count); i++)
            {
                _withinLimit.Add(list[i]);
            }
        }
    }

    private PersistentPoiProductionStatus? GetBlocker(
        EntityUid uid,
        PersistentPoiProductionMachineComponent machine,
        TransformComponent xform,
        out PersistentPoiProductionRecipePrototype? recipe,
        out PersistentPoiLevelPrototype? level)
    {
        recipe = null;
        level = null;

        if (machine.SelectedRecipe is not { } recipeId || !_proto.TryIndex(recipeId, out recipe))
            return PersistentPoiProductionStatus.NoRecipe;

        if (!machine.Enabled)
            return PersistentPoiProductionStatus.Disabled;

        if (xform.GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner == null)
        {
            return PersistentPoiProductionStatus.Unclaimed;
        }

        level = _levels.GetLevel(territory.Level);

        if (territory.Capability != machine.Capability)
            return PersistentPoiProductionStatus.WrongCapability;

        if (!_withinLimit.Contains(uid))
            return PersistentPoiProductionStatus.OverLimit;

        if (!_onlineGrids.Contains(grid))
            return PersistentPoiProductionStatus.NoConsole;

        if (machine.Stored >= GetMaxStored(machine, level))
            return PersistentPoiProductionStatus.Full;

        return null;
    }

    private static float GetDuration(PersistentPoiProductionRecipePrototype recipe)
    {
        return MathF.Max(1f, recipe.Duration);
    }

    private static int GetMaxStored(PersistentPoiProductionMachineComponent machine, PersistentPoiLevelPrototype? level)
    {
        return Math.Max(1, machine.MaxStored + (level?.ExtraStorage ?? 0));
    }

    private int GetMaxStored(EntityUid uid, PersistentPoiProductionMachineComponent machine)
    {
        var level = Transform(uid).GridUid is { } grid && TryComp<PersistentPoiGridComponent>(grid, out var territory)
            ? _levels.GetLevel(territory.Level)
            : null;

        return GetMaxStored(machine, level);
    }

    private void OnMachineMapInit(Entity<PersistentPoiProductionMachineComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.MaxStored = Math.Max(1, ent.Comp.MaxStored);

        if (ent.Comp.SelectedRecipe == null && ent.Comp.Recipes.Count > 0)
            ent.Comp.SelectedRecipe = ent.Comp.Recipes[0];
    }

    private void OnMachineExamined(Entity<PersistentPoiProductionMachineComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        if (_proto.TryIndex(ent.Comp.Capability, out var capability))
        {
            args.PushMarkup(Loc.GetString("persistent-poi-production-examine-capability",
                ("capability", Loc.GetString(capability.Name))));
        }

        args.PushMarkup(Loc.GetString("persistent-poi-production-examine-status",
            ("status", Loc.GetString(ent.Comp.Status.GetLocId(), ("percent", GetPowerPercent(ent.Comp))))));
        args.PushMarkup(Loc.GetString("persistent-poi-production-examine-stored",
            ("stored", ent.Comp.Stored),
            ("max", GetMaxStored(ent, ent.Comp))));

        if (ent.Comp.PowerDraw > 0f)
        {
            args.PushMarkup(Loc.GetString("persistent-poi-production-examine-power",
                ("kw", MathF.Round(ent.Comp.PowerDraw / 1000f, 1))));
        }
    }

    private static int GetPowerPercent(PersistentPoiProductionMachineComponent machine)
    {
        return (int) MathF.Round(machine.PowerRatio * 100f);
    }

    private void OnOwnerChanged(ref PersistentRegionOwnerChangedEvent args)
    {
        // overtime is paid for by the old holder
        if (TryComp<PersistentPoiGridComponent>(args.Grid, out var territory))
        {
            territory.OvertimeUntil = TimeSpan.Zero;
            territory.OvertimeSpeed = 1f;
        }

        var consoles = EntityQueryEnumerator<PersistentPoiProductionConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == args.Grid)
                _ui.CloseUi(uid, PersistentPoiProductionUiKey.Key);
        }
    }

    #region Console

    private void OnConsoleOpened(Entity<PersistentPoiProductionConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateConsole(ent);
    }

    private void OnToggle(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionToggleMessage args)
    {
        if (!TryGetMachine(ent, args.Machine, args.Actor, out var machine))
            return;

        ToggleWorks(machine);
        UpdateConsole(ent);
    }

    private void OnSelectRecipe(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionSelectRecipeMessage args)
    {
        var recipe = new ProtoId<PersistentPoiProductionRecipePrototype>(args.Recipe);
        if (!TryGetMachine(ent, args.Machine, args.Actor, out var machine) ||
            !machine.Comp.Recipes.Contains(recipe) ||
            machine.Comp.SelectedRecipe == recipe)
        {
            return;
        }

        Dispense(machine);

        TrySetRecipe(machine, args.Recipe);
        UpdateConsole(ent);
    }

    public void ToggleWorks(Entity<PersistentPoiProductionMachineComponent> machine)
    {
        machine.Comp.Enabled = !machine.Comp.Enabled;
    }

    public bool TrySetRecipe(Entity<PersistentPoiProductionMachineComponent> machine, string recipeId)
    {
        var recipe = new ProtoId<PersistentPoiProductionRecipePrototype>(recipeId);
        if (machine.Comp.Stored > 0 ||
            !machine.Comp.Recipes.Contains(recipe) ||
            machine.Comp.SelectedRecipe == recipe)
        {
            return false;
        }

        machine.Comp.SelectedRecipe = recipe;
        machine.Comp.Progress = 0f;
        return true;
    }

    public bool IsConsoleOnline(EntityUid grid)
    {
        return _onlineGrids.Contains(grid);
    }

    private void OnDispense(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionDispenseMessage args)
    {
        if (!TryGetMachine(ent, args.Machine, args.Actor, out var machine))
            return;

        if (RoundOver)
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-production-round-over"), ent, args.Actor, PopupType.SmallCaution);
            return;
        }

        if (Dispense(machine) == 0)
            _popup.PopupEntity(Loc.GetString("persistent-poi-production-nothing-stored"), ent, args.Actor, PopupType.SmallCaution);

        UpdateConsole(ent);
    }

    private void OnSetCapability(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionSetCapabilityMessage args)
    {
        if (!TryGetHeldTerritory(ent, args.Actor, out _, out var territory))
            return;

        if (TrySetCapability(territory, args.Capability, args.Actor))
            UpdateConsole(ent);
    }

    public bool TrySetCapability(PersistentPoiGridComponent territory, string capability, EntityUid actor)
    {
        if (!_proto.HasIndex<PersistentPoiCapabilityPrototype>(capability) ||
            territory.Capability?.Id == capability ||
            !_progression.SetCapability(territory.RegionId, capability))
        {
            return false;
        }

        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(actor):player} retooled territory '{territory.RegionId}' for {capability}");
        return true;
    }

    private void OnDecommission(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionDecommissionMessage args)
    {
        if (!TryGetMachine(ent, args.Machine, args.Actor, out var machine))
            return;

        if (RoundOver)
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-production-round-over"), ent, args.Actor, PopupType.SmallCaution);
            return;
        }

        Dispense(machine);

        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} decommissioned territory works {ToPrettyString(machine)}");

        _popup.PopupEntity(Loc.GetString("persistent-poi-production-decommissioned", ("works", Name(machine))), ent, args.Actor);
        QueueDel(machine);
        UpdateConsole(ent);
    }

    private bool TryGetHeldTerritory(
        EntityUid console,
        EntityUid actor,
        out EntityUid grid,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PersistentPoiGridComponent? territory)
    {
        territory = null;
        grid = default;

        if (Transform(console).GridUid is not { } consoleGrid ||
            !TryComp(consoleGrid, out territory) ||
            territory.Owner == null ||
            !string.Equals(PersistentTerritoryFactions.GetTeam(EntityManager, actor), territory.Owner, StringComparison.Ordinal))
        {
            territory = null;
            return false;
        }

        grid = consoleGrid;
        return true;
    }

    private bool TryGetMachine(
        EntityUid console,
        NetEntity netMachine,
        EntityUid actor,
        out Entity<PersistentPoiProductionMachineComponent> machine)
    {
        machine = default;

        if (!TryGetEntity(netMachine, out var uid) ||
            !TryComp<PersistentPoiProductionMachineComponent>(uid, out var comp) ||
            !TryGetHeldTerritory(console, actor, out var grid, out _) ||
            Transform(uid.Value).GridUid != grid)
        {
            return false;
        }

        machine = (uid.Value, comp);
        return true;
    }

    private void UpdateOpenConsoles()
    {
        var consoles = EntityQueryEnumerator<PersistentPoiProductionConsoleComponent>();
        while (consoles.MoveNext(out var uid, out var console))
        {
            if (_ui.IsUiOpen(uid, PersistentPoiProductionUiKey.Key))
                UpdateConsole((uid, console));
        }
    }

    private void UpdateConsole(Entity<PersistentPoiProductionConsoleComponent> ent)
    {
        var viewer = _ui.GetActors(ent.Owner, PersistentPoiProductionUiKey.Key).FirstOrDefault();
        _ui.SetUiState(ent.Owner, PersistentPoiProductionUiKey.Key,
            BuildConsoleState(Transform(ent).GridUid, viewer.IsValid() ? viewer : null));
    }

    public PersistentPoiProductionConsoleState BuildConsoleState(EntityUid? territoryGrid, EntityUid? viewer = null)
    {
        var state = new PersistentPoiProductionConsoleState();

        foreach (var capability in _proto.EnumeratePrototypes<PersistentPoiCapabilityPrototype>()
                     .OrderBy(c => c.Priority)
                     .ThenBy(c => c.ID, StringComparer.Ordinal))
        {
            state.Capabilities.Add(new PersistentPoiCapabilityOption
            {
                Id = capability.ID,
                Name = Loc.GetString(capability.Name),
                Description = Loc.GetString(capability.Description),
            });
        }

        if (territoryGrid is { } grid && TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            state.Owner = territory.Owner;
            state.Level = territory.Level;
            state.Capability = territory.Capability?.Id;
            state.Factories = territory.WorksCounts.GetValueOrDefault(PersistentPoiWorksCategory.Factory);
            state.FactoryLimit = _levels.GetLimit(territory, PersistentPoiWorksCategory.Factory);

            var level = _levels.GetLevel(territory.Level);
            var pricing = GetPricing(grid);
            var machines = EntityQueryEnumerator<PersistentPoiProductionMachineComponent, TransformComponent>();
            while (machines.MoveNext(out var uid, out var machine, out var xform))
            {
                if (xform.GridUid != grid)
                    continue;

                var row = GetMachineState(uid, machine, level);
                row.RushPrice = GetRushPrice(uid, machine, pricing);
                state.Machines.Add(row);
            }

            state.OvertimeSeconds = (float) GetOvertimeLeft(territory).TotalSeconds;
            state.OvertimeSpeed = pricing.OvertimeSpeed;
            state.OvertimeMinutes = (int) Math.Round(pricing.OvertimeDuration.TotalMinutes);
            state.OvertimeMaxed = IsOvertimeMaxed(territory, pricing);
            state.OvertimePrice = GetOvertimePrice(grid, territory, pricing);

            if (viewer is { } player && territory.Owner != null)
                state.Payment = _payment.GetState(player, territory.Owner, pricing.TreasuryShare);
        }

        state.Machines = state.Machines
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Machine.Id)
            .ToList();

        return state;
    }

    private PersistentPoiProductionMachineState GetMachineState(
        EntityUid uid,
        PersistentPoiProductionMachineComponent machine,
        PersistentPoiLevelPrototype? level)
    {
        var state = new PersistentPoiProductionMachineState
        {
            Machine = GetNetEntity(uid),
            Name = Name(uid),
            Enabled = machine.Enabled,
            Status = machine.Status,
            Power = machine.PowerRatio,
            SelectedRecipe = machine.SelectedRecipe?.Id,
            Stored = machine.Stored,
            MaxStored = GetMaxStored(machine, level),
        };

        foreach (var recipeId in machine.Recipes)
        {
            if (!_proto.TryIndex(recipeId, out var recipe))
                continue;

            state.RecipeIds.Add(recipeId.Id);
            state.RecipeNames.Add(Loc.GetString(recipe.Name));
        }

        if (machine.SelectedRecipe is { } selectedId && _proto.TryIndex(selectedId, out var selected))
        {
            state.Progress = Math.Clamp(machine.Progress / GetDuration(selected), 0f, 1f);
            state.Output = DescribeOutputs(selected);
        }

        return state;
    }

    private string DescribeOutputs(PersistentPoiProductionRecipePrototype recipe)
    {
        var parts = new List<string>();
        foreach (var (proto, count) in recipe.Outputs)
        {
            var name = _proto.TryIndex(proto, out var entity) ? entity.Name : proto.Id;
            parts.Add(Loc.GetString("persistent-poi-production-output-entry", ("count", count), ("item", name)));
        }

        return string.Join(", ", parts);
    }

    #endregion

    public int Dispense(Entity<PersistentPoiProductionMachineComponent> machine)
    {
        if (RoundOver ||
            machine.Comp.Stored <= 0 ||
            machine.Comp.SelectedRecipe is not { } recipeId ||
            !_proto.TryIndex(recipeId, out var recipe))
        {
            return 0;
        }

        var batches = machine.Comp.Stored;
        machine.Comp.Stored = 0;

        foreach (var (proto, perBatch) in recipe.Outputs)
        {
            var remaining = perBatch * batches;
            while (remaining > 0)
            {
                var item = SpawnNextToOrDrop(proto, machine);
                if (TryComp<StackComponent>(item, out var stack))
                {
                    var count = Math.Clamp(remaining, 1, Math.Max(1, _stack.GetMaxCount(item)));
                    _stack.SetCount(item, count, stack);
                    remaining -= count;
                }
                else
                {
                    remaining--;
                }
            }
        }

        return batches;
    }

    #region Paid orders

    private readonly Dictionary<string, double> _batchValues = new();

    private static readonly PersistentPoiProductionConsoleComponent DefaultPricing = new();

    public PersistentPoiProductionConsoleComponent GetPricing(EntityUid grid)
    {
        var consoles = EntityQueryEnumerator<PersistentPoiProductionConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out _, out var console, out var xform))
        {
            if (xform.GridUid == grid)
                return console;
        }

        return DefaultPricing;
    }

    private double GetBatchValue(PersistentPoiProductionRecipePrototype recipe)
    {
        if (_batchValues.TryGetValue(recipe.ID, out var value))
            return value;

        value = 0;
        foreach (var (proto, count) in recipe.Outputs)
        {
            if (_proto.TryIndex(proto, out var entity))
                value += _pricing.GetEstimatedPrice(entity) * count;
        }

        _batchValues[recipe.ID] = value;
        return value;
    }

    public int GetRushPrice(EntityUid uid, PersistentPoiProductionMachineComponent machine, PersistentPoiProductionConsoleComponent pricing)
    {
        if (GetBlocker(uid, machine, Transform(uid), out var recipe, out _) != null || recipe == null || !IsProducing(machine))
            return 0;

        var duration = GetDuration(recipe);
        var left = Math.Clamp(1f - machine.Progress / duration, 0f, 1f);
        if (left <= 0f)
            return 0;

        var value = GetBatchValue(recipe);
        var price = value > 0
            ? value * left * pricing.RushMarkup
            : duration * left / 60.0 * pricing.RushPricePerMinute;

        return Math.Max(pricing.MinimumRushPrice, (int) Math.Ceiling(price));
    }

    public int GetOvertimePrice(EntityUid grid, PersistentPoiGridComponent territory, PersistentPoiProductionConsoleComponent pricing)
    {
        if (territory.Owner == null || pricing.OvertimeSpeed <= 1f || IsOvertimeMaxed(territory, pricing))
            return 0;

        var levelSpeed = _levels.GetLevel(territory.Level)?.ProductionSpeed ?? 1f;
        var extraSeconds = pricing.OvertimeDuration.TotalSeconds * (pricing.OvertimeSpeed - 1f) * levelSpeed;
        var value = 0d;
        var works = 0;

        var machines = EntityQueryEnumerator<PersistentPoiProductionMachineComponent, TransformComponent>();
        while (machines.MoveNext(out var uid, out var machine, out var xform))
        {
            if (xform.GridUid != grid ||
                GetBlocker(uid, machine, xform, out var recipe, out _) != null ||
                recipe == null ||
                !IsProducing(machine))
            {
                continue;
            }

            works++;
            var batchValue = GetBatchValue(recipe);
            value += batchValue > 0
                ? batchValue * extraSeconds / GetDuration(recipe)
                : extraSeconds / 60.0 * pricing.RushPricePerMinute;
        }

        return works == 0 ? 0 : Math.Max(pricing.MinimumOvertimePrice, (int) Math.Ceiling(value * pricing.OvertimeMarkup));
    }

    private static bool IsProducing(PersistentPoiProductionMachineComponent machine)
    {
        return machine.Status is PersistentPoiProductionStatus.Running
            or PersistentPoiProductionStatus.LowPower
            or PersistentPoiProductionStatus.Starting;
    }

    private bool RoundOver => _ticker.RunLevel == GameRunLevel.PostRound;

    private bool IsOvertimeMaxed(PersistentPoiGridComponent territory, PersistentPoiProductionConsoleComponent pricing)
    {
        return GetOvertimeLeft(territory) + pricing.OvertimeDuration > pricing.MaxOvertime;
    }

    public TimeSpan GetOvertimeLeft(PersistentPoiGridComponent territory)
    {
        var left = territory.OvertimeUntil - _timing.CurTime;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private float GetOvertimeSpeed(EntityUid? grid)
    {
        return grid is { } uid &&
               TryComp<PersistentPoiGridComponent>(uid, out var territory) &&
               territory.OvertimeUntil > _timing.CurTime
            ? MathF.Max(1f, territory.OvertimeSpeed)
            : 1f;
    }

    public bool TryRush(
        Entity<PersistentPoiProductionMachineComponent> machine,
        EntityUid actor,
        PaymentSource source,
        out int price,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        price = 0;
        if (RoundOver)
        {
            error = "persistent-poi-production-round-over";
            return false;
        }

        if (Transform(machine).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner is not { } owner)
        {
            error = "persistent-poi-rush-unavailable";
            return false;
        }

        var pricing = GetPricing(grid);
        price = GetRushPrice(machine, machine.Comp, pricing);
        if (price <= 0)
        {
            error = "persistent-poi-rush-unavailable";
            return false;
        }

        if (!_payment.TryCharge(actor, owner, source, price, pricing.TreasuryShare,
                $"rushing a batch at {ToPrettyString(machine)} on territory '{territory.RegionId}'", out error))
        {
            return false;
        }

        machine.Comp.Progress = 0f;
        machine.Comp.Stored++;
        return true;
    }

    public bool TryBuyOvertime(
        EntityUid grid,
        PersistentPoiGridComponent territory,
        EntityUid actor,
        PaymentSource source,
        out int price,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        price = 0;
        if (RoundOver)
        {
            error = "persistent-poi-production-round-over";
            return false;
        }

        var pricing = GetPricing(grid);

        if (territory.Owner is not { } owner)
        {
            error = "persistent-poi-overtime-unavailable";
            return false;
        }

        if (IsOvertimeMaxed(territory, pricing))
        {
            error = "persistent-poi-overtime-maxed";
            return false;
        }

        price = GetOvertimePrice(grid, territory, pricing);
        if (price <= 0)
        {
            error = "persistent-poi-overtime-unavailable";
            return false;
        }

        if (!_payment.TryCharge(actor, owner, source, price, pricing.TreasuryShare,
                $"{pricing.OvertimeDuration.TotalMinutes:0} minutes of overtime on territory '{territory.RegionId}'", out error))
        {
            return false;
        }

        var now = _timing.CurTime;
        territory.OvertimeUntil = (territory.OvertimeUntil > now ? territory.OvertimeUntil : now) + pricing.OvertimeDuration;
        territory.OvertimeSpeed = pricing.OvertimeSpeed;
        return true;
    }

    private void OnRush(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionRushMessage args)
    {
        if (!TryGetMachine(ent, args.Machine, args.Actor, out var machine))
            return;

        if (!TryRush(machine, args.Actor, args.Source, out var price, out var error))
        {
            _popup.PopupEntity(Loc.GetString(error), ent, args.Actor, PopupType.SmallCaution);
            UpdateConsole(ent);
            return;
        }

        _popup.PopupEntity(Loc.GetString("persistent-poi-rush-done", ("price", price), ("source", SourceName(args.Source))),
            ent, args.Actor);
        UpdateConsole(ent);
    }

    private void OnOvertime(Entity<PersistentPoiProductionConsoleComponent> ent, ref PersistentPoiProductionOvertimeMessage args)
    {
        if (!TryGetHeldTerritory(ent, args.Actor, out var grid, out var territory))
            return;

        if (!TryBuyOvertime(grid, territory, args.Actor, args.Source, out var price, out var error))
        {
            _popup.PopupEntity(Loc.GetString(error), ent, args.Actor, PopupType.SmallCaution);
            UpdateConsole(ent);
            return;
        }

        _popup.PopupEntity(Loc.GetString("persistent-poi-overtime-done", ("price", price), ("source", SourceName(args.Source))),
            ent, args.Actor);
        UpdateConsole(ent);
    }

    public string SourceName(PaymentSource source)
    {
        return Loc.GetString(source == PaymentSource.Faction
            ? "territory-payment-source-faction"
            : "territory-payment-source-personal");
    }

    #endregion
}
