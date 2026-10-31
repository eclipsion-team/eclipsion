using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Shared._Crescent.Territory;
using Content.Shared.Database;
using Content.Server.Administration.Logs;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Robust.Server.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiProgressionSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    [Dependency] private readonly TerritoryProfileSystem _profiles = default!;

    private ResPath SavePath => _profiles.PathFor("territory_progress.json");
    private const int SaveVersion = 1;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private static readonly PersistentPoiWorksCategory[] WorksCategories = Enum.GetValues<PersistentPoiWorksCategory>();

    private readonly Dictionary<string, TerritoryProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EntityUid, Dictionary<PersistentPoiWorksCategory, int>> _counts = new();
    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        Load();

        SubscribeLocalEvent<TerritoryProfileChangedEvent>((ref TerritoryProfileChangedEvent _) => Load());
        SubscribeLocalEvent<PersistentRegionRegisteredEvent>(OnRegionRegistered);
        SubscribeLocalEvent<PersistentRegionNeutralizedEvent>(OnRegionNeutralized);
        SubscribeLocalEvent<PersistentRegionOwnerChangedEvent>(OnOwnerChanged);
        SubscribeLocalEvent<PersistentPoiAdministrationConsoleComponent, InteractUsingEvent>(OnDeposit);

        Subs.BuiEvents<PersistentPoiAdministrationConsoleComponent>(PersistentPoiAdministrationUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnAdministrationOpened);
            subs.Event<PersistentPoiUpgradeMessage>(OnUpgrade);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshInterval;

        // one pass for every territory and category
        _counts.Clear();
        var grids = EntityQueryEnumerator<PersistentPoiGridComponent>();
        while (grids.MoveNext(out var uid, out _))
        {
            _counts[uid] = new Dictionary<PersistentPoiWorksCategory, int>();
        }

        _levels.CountAllWorksLive(_counts);
        foreach (var (uid, counts) in _counts)
        {
            var territory = Comp<PersistentPoiGridComponent>(uid);
            ApplyCounts(uid, territory, counts);
            SyncLevel(uid, territory);
        }

        var consoles = EntityQueryEnumerator<PersistentPoiAdministrationConsoleComponent>();
        while (consoles.MoveNext(out var uid, out _))
        {
            if (_ui.IsUiOpen(uid, PersistentPoiAdministrationUiKey.Key))
                UpdateAdministrationConsole(uid);
        }
    }

    #region Registry

    public int GetLevel(string regionId)
    {
        return _progress.TryGetValue(regionId.Trim(), out var progress) ? progress.Level : 1;
    }

    public void SetLevel(string regionId, int level)
    {
        var progress = GetOrCreate(regionId);
        progress.Level = Math.Clamp(level, 1, _levels.MaxLevel);
        progress.Deposits.Clear();
        Save();

        foreach (var grid in GetLiveGrids(regionId))
        {
            grid.Comp.Level = progress.Level;
            Dirty(grid);
            RaiseDevelopmentChanged(grid);
        }
    }

    public bool SetCapability(string regionId, string? capability)
    {
        if (capability != null && !_proto.HasIndex<PersistentPoiCapabilityPrototype>(capability))
            return false;

        var progress = GetOrCreate(regionId);
        progress.Capability = capability;
        Save();

        foreach (var grid in GetLiveGrids(regionId))
        {
            grid.Comp.Capability = capability == null
                ? null
                : (ProtoId<PersistentPoiCapabilityPrototype>?) new ProtoId<PersistentPoiCapabilityPrototype>(capability);
            Dirty(grid);
            RaiseDevelopmentChanged(grid);
        }

        return true;
    }

    public bool ResetProgress(string regionId)
    {
        regionId = regionId.Trim();
        var known = _progress.Remove(regionId);
        Save();

        var grids = GetLiveGrids(regionId);
        foreach (var grid in grids)
        {
            grid.Comp.Level = 1;
            grid.Comp.Capability = null;
            Dirty(grid);
            RaiseDevelopmentChanged(grid);
        }

        return known || grids.Count > 0;
    }

    public int ResetAllProgress()
    {
        var regions = _progress.Keys.ToList();
        foreach (var regionId in regions)
        {
            ResetProgress(regionId);
        }

        var live = EntityQueryEnumerator<PersistentPoiGridComponent>();
        while (live.MoveNext(out var uid, out var territory))
        {
            if (territory.Level == 1 && territory.Capability == null)
                continue;

            territory.Level = 1;
            territory.Capability = null;
            Dirty(uid, territory);
            RaiseDevelopmentChanged(uid);
        }

        return regions.Count;
    }

    public string? GetCapability(string regionId)
    {
        return _progress.TryGetValue(regionId.Trim(), out var progress) ? progress.Capability : null;
    }

    private void OnRegionRegistered(ref PersistentRegionRegisteredEvent args)
    {
        if (!TryComp<PersistentPoiGridComponent>(args.Grid, out var territory))
            return;

        var progress = _progress.GetValueOrDefault(args.RegionId);
        territory.Level = Math.Clamp(progress?.Level ?? 1, 1, _levels.MaxLevel);
        territory.Capability = progress?.Capability is { } capability &&
                               _proto.HasIndex<PersistentPoiCapabilityPrototype>(capability)
            ? new ProtoId<PersistentPoiCapabilityPrototype>(capability)
            : (ProtoId<PersistentPoiCapabilityPrototype>?) null;

        RefreshCounts(args.Grid, territory);
        Dirty(args.Grid, territory);
    }

    private void OnRegionNeutralized(ref PersistentRegionNeutralizedEvent args)
    {
        if (!TryComp<PersistentCaptureRegionComponent>(args.Region, out var region) ||
            region.LevelsLostOnNeutralize <= 0)
        {
            return;
        }

        var level = GetLevel(args.RegionId);
        var reduced = Math.Max(1, level - region.LevelsLostOnNeutralize);
        if (reduced != level)
            SetLevel(args.RegionId, reduced);
    }

    private void OnOwnerChanged(ref PersistentRegionOwnerChangedEvent args)
    {
        var consoles = EntityQueryEnumerator<PersistentPoiAdministrationConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == args.Grid)
                _ui.CloseUi(uid, PersistentPoiAdministrationUiKey.Key);
        }
    }

    private TerritoryProgress GetOrCreate(string regionId)
    {
        regionId = regionId.Trim();
        if (!_progress.TryGetValue(regionId, out var progress))
        {
            progress = new TerritoryProgress();
            _progress[regionId] = progress;
        }

        return progress;
    }

    private List<Entity<PersistentPoiGridComponent>> GetLiveGrids(string regionId)
    {
        regionId = regionId.Trim();
        var grids = new List<Entity<PersistentPoiGridComponent>>();
        var query = EntityQueryEnumerator<PersistentPoiGridComponent>();
        while (query.MoveNext(out var uid, out var territory))
        {
            if (string.Equals(territory.RegionId, regionId, StringComparison.OrdinalIgnoreCase))
                grids.Add((uid, territory));
        }

        return grids;
    }

    // The save is the truth. A grid that missed its registration sat at level 1, and raising it from there
    // wrote level 2 over a saved 3.
    private void SyncLevel(EntityUid grid, PersistentPoiGridComponent territory)
    {
        if (!_progress.TryGetValue(territory.RegionId, out var progress))
            return;

        var level = Math.Clamp(progress.Level, 1, _levels.MaxLevel);
        if (territory.Level == level)
            return;

        Log.Warning($"Territory '{territory.RegionId}' showed level {territory.Level} against a saved {level}; corrected.");
        territory.Level = level;
        Dirty(grid, territory);
        RaiseDevelopmentChanged(grid);
    }

    private void RaiseDevelopmentChanged(EntityUid grid)
    {
        var ev = new PersistentPoiDevelopmentChangedEvent(grid);
        RaiseLocalEvent(ref ev);
    }

    private void RefreshCounts(EntityUid grid, PersistentPoiGridComponent territory)
    {
        var counts = new Dictionary<EntityUid, Dictionary<PersistentPoiWorksCategory, int>> { [grid] = new() };
        _levels.CountAllWorksLive(counts);
        ApplyCounts(grid, territory, counts[grid]);
    }

    private void ApplyCounts(EntityUid grid, PersistentPoiGridComponent territory,
        Dictionary<PersistentPoiWorksCategory, int> counts)
    {
        var changed = false;
        foreach (var category in WorksCategories)
        {
            var count = counts.GetValueOrDefault(category);
            if (territory.WorksCounts.GetValueOrDefault(category) == count &&
                territory.WorksCounts.ContainsKey(category))
            {
                continue;
            }

            territory.WorksCounts[category] = count;
            changed = true;
        }

        if (changed)
            Dirty(grid, territory);
    }

    #endregion

    #region Administration console

    private void OnAdministrationOpened(Entity<PersistentPoiAdministrationConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateAdministrationConsole(ent);
    }

    private void OnDeposit(Entity<PersistentPoiAdministrationConsoleComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        var storage = CompOrNull<StorageComponent>(args.Used);
        if (storage == null && !HasComp<StackComponent>(args.Used))
            return;

        args.Handled = true;

        if (!TryGetTerritory(ent, out var grid, out var territory))
            return;

        if (!IsHolder(args.User, territory))
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-admin-not-holder"), ent, args.User, PopupType.SmallCaution);
            return;
        }

        SyncLevel(grid, territory);
        if (_levels.GetNextLevel(territory.Level) is not { } next)
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-admin-max-level"), ent, args.User);
            return;
        }

        var progress = GetOrCreate(territory.RegionId);
        var taken = new Dictionary<string, int>();

        if (storage != null)
        {
            foreach (var item in storage.Container.ContainedEntities.ToArray())
            {
                if (TryComp<StackComponent>(item, out var stack))
                    TakeFromStack(item, stack, progress, next, taken);
            }

            if (taken.Count == 0)
            {
                _popup.PopupEntity(Loc.GetString("persistent-poi-admin-nothing-needed-in", ("container", args.Used)),
                    ent,
                    args.User,
                    PopupType.SmallCaution);
                return;
            }
        }
        else
        {
            var stack = Comp<StackComponent>(args.Used);
            if (!next.UpgradeCost.ContainsKey(stack.StackTypeId))
            {
                _popup.PopupEntity(Loc.GetString("persistent-poi-admin-not-needed"), ent, args.User, PopupType.SmallCaution);
                return;
            }

            if (!TakeFromStack(args.Used, stack, progress, next, taken))
            {
                _popup.PopupEntity(Loc.GetString("persistent-poi-admin-already-enough"), ent, args.User);
                return;
            }
        }

        Save();

        var entries = taken.Select(pair => Loc.GetString("persistent-poi-admin-deposited",
            ("amount", pair.Value),
            ("material", GetMaterialName(pair.Key)),
            ("total", progress.Deposits.GetValueOrDefault(pair.Key)),
            ("required", next.UpgradeCost.GetValueOrDefault(pair.Key))));

        _popup.PopupEntity(string.Join("\n", entries), ent, args.User);
        UpdateAdministrationConsole(ent);
    }

    private bool TakeFromStack(
        EntityUid uid,
        StackComponent stack,
        TerritoryProgress progress,
        PersistentPoiLevelPrototype next,
        Dictionary<string, int> taken)
    {
        var material = stack.StackTypeId;
        if (!next.UpgradeCost.TryGetValue(material, out var required))
            return false;

        var deposited = progress.Deposits.GetValueOrDefault(material);
        var take = Math.Min(stack.Count, required - deposited);
        if (take <= 0 || !_stack.Use(uid, take, stack))
            return false;

        progress.Deposits[material] = deposited + take;
        taken[material] = taken.GetValueOrDefault(material) + take;
        return true;
    }

    private void OnUpgrade(Entity<PersistentPoiAdministrationConsoleComponent> ent, ref PersistentPoiUpgradeMessage args)
    {
        if (!TryGetTerritory(ent, out var grid, out var territory) ||
            !IsHolder(args.Actor, territory) ||
            _levels.GetNextLevel(territory.Level) == null)
        {
            return;
        }

        if (!RaiseLevel(grid, args.Actor))
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-admin-missing-materials"), ent, args.Actor, PopupType.SmallCaution);
            return;
        }

        _popup.PopupEntity(Loc.GetString("persistent-poi-admin-upgraded", ("level", territory.Level)), ent, args.Actor, PopupType.Medium);
        UpdateAdministrationConsole(ent);
    }

    public bool RaiseLevel(EntityUid grid, EntityUid actor)
    {
        if (!TryUpgrade(grid) || !TryComp<PersistentPoiGridComponent>(grid, out var territory))
            return false;

        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(actor):player} raised territory '{territory.RegionId}' to level {territory.Level}");

        return true;
    }

    public bool TryUpgrade(EntityUid grid)
    {
        if (!TryComp<PersistentPoiGridComponent>(grid, out var territory))
            return false;

        SyncLevel(grid, territory);
        if (_levels.GetNextLevel(territory.Level) is not { } next)
            return false;

        var progress = GetOrCreate(territory.RegionId);
        foreach (var (material, required) in next.UpgradeCost)
        {
            if (progress.Deposits.GetValueOrDefault(material) < required)
                return false;
        }

        SetLevel(territory.RegionId, next.Level);
        return true;
    }

    public int GetDeposited(string regionId, string material)
    {
        return _progress.TryGetValue(regionId.Trim(), out var progress)
            ? progress.Deposits.GetValueOrDefault(material)
            : 0;
    }

    private void UpdateAdministrationConsole(EntityUid console)
    {
        var grid = TryGetTerritory(console, out var territoryGrid, out _) ? territoryGrid : (EntityUid?) null;
        _ui.SetUiState(console, PersistentPoiAdministrationUiKey.Key, BuildAdministrationState(grid));
    }

    public PersistentPoiAdministrationState BuildAdministrationState(EntityUid? territoryGrid)
    {
        var state = new PersistentPoiAdministrationState
        {
            MaxLevel = _levels.MaxLevel,
        };

        if (territoryGrid is { } grid && TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            state.Owner = territory.Owner;
            state.RegionName = PersistentTerritoryFactions.StripOwnerPrefix(Name(grid));
            state.Level = territory.Level;
            state.Capability = territory.Capability is { } capability &&
                               _proto.TryIndex(capability, out var capabilityProto)
                ? Loc.GetString(capabilityProto.Name)
                : null;

            var current = _levels.GetLevel(territory.Level);
            var next = _levels.GetNextLevel(territory.Level);

            foreach (var category in Enum.GetValues<PersistentPoiWorksCategory>())
            {
                int? limit = current != null && current.Limits.TryGetValue(category, out var currentLimit) ? currentLimit : null;
                int? nextLimit = next != null && next.Limits.TryGetValue(category, out var upcomingLimit) ? upcomingLimit : null;

                state.Limits.Add(new PersistentPoiLimitState
                {
                    Category = category,
                    Used = territory.WorksCounts.GetValueOrDefault(category),
                    Limit = limit,
                    NextLimit = nextLimit,
                });
            }

            if (next != null)
            {
                var deposits = _progress.GetValueOrDefault(territory.RegionId)?.Deposits;
                var ready = true;

                foreach (var (material, required) in next.UpgradeCost)
                {
                    var deposited = deposits?.GetValueOrDefault(material) ?? 0;
                    ready &= deposited >= required;

                    state.NextLevelCost.Add(new PersistentPoiCostState
                    {
                        Material = GetMaterialName(material),
                        Deposited = deposited,
                        Required = required,
                    });
                }

                state.CanUpgrade = ready && territory.Owner != null;
            }
        }

        return state;
    }

    private bool TryGetTerritory(EntityUid console, out EntityUid grid, [NotNullWhen(true)] out PersistentPoiGridComponent? territory)
    {
        territory = null;
        grid = default;

        if (Transform(console).GridUid is not { } consoleGrid ||
            !TryComp(consoleGrid, out territory))
        {
            return false;
        }

        grid = consoleGrid;
        return true;
    }

    private bool IsHolder(EntityUid user, PersistentPoiGridComponent territory)
    {
        return territory.Owner != null &&
               string.Equals(PersistentTerritoryFactions.GetTeam(EntityManager, user), territory.Owner, StringComparison.Ordinal);
    }

    private string GetMaterialName(ProtoId<StackPrototype> material)
    {
        if (!_proto.TryIndex(material, out var stack))
            return material.Id;

        return Loc.TryGetString(stack.Name, out var localized) ? localized : stack.Name;
    }

    #endregion

    #region Persistence

    private void Load()
    {
        _progress.Clear();

        try
        {
            if (!_resources.UserData.Exists(SavePath) || !_resources.UserData.TryReadAllText(SavePath, out var json))
                return;

            var data = JsonSerializer.Deserialize<TerritoryProgressSaveData>(json);
            if (data is null || data.Version != SaveVersion)
                return;

            foreach (var (regionId, progress) in data.Territories)
            {
                if (!string.IsNullOrWhiteSpace(regionId))
                    _progress[regionId.Trim()] = progress;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load territory progress: {e}");
        }
    }

    private void Save()
    {
        try
        {
            var data = new TerritoryProgressSaveData { Version = SaveVersion };
            foreach (var (regionId, progress) in _progress)
            {
                data.Territories[regionId] = progress;
            }

            var path = SavePath;
            _resources.UserData.CreateDir(path.Directory);
            _resources.UserData.WriteAllText(path, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save territory progress: {e}");
        }
    }

    #endregion
}

public sealed class TerritoryProgress
{
    public int Level { get; set; } = 1;

    public string? Capability { get; set; }

    public Dictionary<string, int> Deposits { get; set; } = new();
}

internal sealed class TerritoryProgressSaveData
{
    public int Version { get; set; }

    public Dictionary<string, TerritoryProgress> Territories { get; set; } = new();
}
