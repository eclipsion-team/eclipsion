using System.Diagnostics.CodeAnalysis;
using Content.Server.Administration.Logs;
using Content.Server.Popups;
using Content.Shared.Access.Systems;
using Content.Shared.Database;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.Territory;
using Content.Shared.CaptureFlag;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryConsoleSystem : EntitySystem
{
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly PersistentCaptureRegionSystem _regions = default!;
    [Dependency] private readonly PersistentPoiProductionSystem _production = default!;
    [Dependency] private readonly PersistentPoiProgressionSystem _progression = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly TerritoryDefenceSystem _defence = default!;
    [Dependency] private readonly TerritoryRemoteFireSystem _remoteFire = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float RefreshInterval = 1f;

    private static readonly TimeSpan RenameCooldown = TimeSpan.FromSeconds(30);

    private float _accumulator;

    private readonly Dictionary<EntityUid, (int Works, int Running, int Stored)> _worksByGrid = new();

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<TerritoryConsoleComponent>(TerritoryConsoleUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<TerritoryConsoleSelectMessage>(OnSelect);
            subs.Event<PersistentPoiProductionToggleMessage>(OnToggle);
            subs.Event<PersistentPoiProductionSelectRecipeMessage>(OnSelectRecipe);
            subs.Event<PersistentPoiProductionSetCapabilityMessage>(OnSetCapability);
            subs.Event<TerritoryConsoleDefenceMessage>(OnDefence);
            subs.Event<TerritoryConsoleRenameMessage>(OnRename);
            subs.Event<TerritoryConsoleAbandonMessage>(OnAbandon);
            subs.Event<TerritoryConsoleRemoteFireMessage>(OnRemoteFire);
            subs.Event<PersistentPoiProductionRushMessage>(OnRush);
            subs.Event<PersistentPoiProductionOvertimeMessage>(OnOvertime);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < RefreshInterval)
            return;

        _accumulator = 0f;

        var consoles = EntityQueryEnumerator<TerritoryConsoleComponent>();
        while (consoles.MoveNext(out var uid, out var console))
        {
            if (_ui.IsUiOpen(uid, TerritoryConsoleUiKey.Key))
                UpdateUi((uid, console));
        }
    }

    private void OnOpened(Entity<TerritoryConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (ent.Comp.Selected == null)
        {
            foreach (var status in _regions.GetRegions())
            {
                if (status.Loaded && IsFaction(ent, status.Owner))
                {
                    ent.Comp.Selected = status.RegionId;
                    break;
                }
            }
        }

        UpdateUi(ent);
    }

    private void OnSelect(Entity<TerritoryConsoleComponent> ent, ref TerritoryConsoleSelectMessage args)
    {
        var regionId = args.RegionId;
        if (!_regions.GetRegions().Any(status => string.Equals(status.RegionId, regionId, StringComparison.OrdinalIgnoreCase)))
            return;

        ent.Comp.Selected = regionId;
        UpdateUi(ent);
    }

    private void OnToggle(Entity<TerritoryConsoleComponent> ent, ref PersistentPoiProductionToggleMessage args)
    {
        if (!TryGetProductionOrder(ent, args.Actor, out var grid) ||
            !TryGetMachine(grid, args.Machine, out var machine))
        {
            return;
        }

        _production.ToggleWorks(machine);
        UpdateUi(ent);
    }

    private void OnSelectRecipe(Entity<TerritoryConsoleComponent> ent, ref PersistentPoiProductionSelectRecipeMessage args)
    {
        if (!TryGetProductionOrder(ent, args.Actor, out var grid) ||
            !TryGetMachine(grid, args.Machine, out var machine))
        {
            return;
        }

        if (machine.Comp.Stored > 0)
        {
            Deny(ent, args.Actor, "territory-console-recipe-locked");
            return;
        }

        _production.TrySetRecipe(machine, args.Recipe);
        UpdateUi(ent);
    }

    private void OnSetCapability(Entity<TerritoryConsoleComponent> ent, ref PersistentPoiProductionSetCapabilityMessage args)
    {
        if (!TryGetProductionOrder(ent, args.Actor, out var grid) ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            return;
        }

        if (_production.TrySetCapability(territory, args.Capability, args.Actor))
            UpdateUi(ent);
    }

    private void OnDefence(Entity<TerritoryConsoleComponent> ent, ref TerritoryConsoleDefenceMessage args)
    {
        if (!TryCommand(ent, args.Actor) || !TryGetHeldGrid(ent, out var grid, out var territory))
            return;

        _defence.SetOrder(grid, territory, args.Order, args.Enabled);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} set {args.Order} to {args.Enabled} on territory '{territory.RegionId}' from {ToPrettyString(ent):console}");
        UpdateUi(ent);
    }

    private void OnRename(Entity<TerritoryConsoleComponent> ent, ref TerritoryConsoleRenameMessage args)
    {
        if (ent.Comp.Selected is not { } regionId || !HoldsSelected(ent) || !TryCommand(ent, args.Actor))
            return;

        var now = _timing.CurTime;
        if (now < ent.Comp.NextRename)
        {
            Deny(ent, args.Actor, "territory-rename-cooldown");
            return;
        }

        if (!_regions.TrySetCustomName(regionId, args.Name, out var error))
        {
            Deny(ent, args.Actor, error);
            return;
        }

        ent.Comp.NextRename = now + RenameCooldown;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):player} renamed territory '{regionId}' to \"{args.Name}\" from {ToPrettyString(ent):console}");
        _popup.PopupEntity(Loc.GetString("territory-rename-done"), ent, args.Actor);
        UpdateUi(ent);
    }

    private void OnAbandon(Entity<TerritoryConsoleComponent> ent, ref TerritoryConsoleAbandonMessage args)
    {
        if (ent.Comp.Selected is not { } regionId || ent.Comp.Faction is not { } faction || !TryCommand(ent, args.Actor))
            return;

        if (!_regions.TryAbandon(regionId, faction))
            return;

        _adminLog.Add(LogType.Action, LogImpact.High,
            $"{ToPrettyString(args.Actor):player} abandoned territory '{regionId}' for {faction} from {ToPrettyString(ent):console}");
        UpdateUi(ent);
    }

    private void OnRemoteFire(Entity<TerritoryConsoleComponent> ent, ref TerritoryConsoleRemoteFireMessage args)
    {
        if (!TryCommand(ent, args.Actor) ||
            !TryGetHeldGrid(ent, out var grid, out var territory) ||
            ent.Comp.Faction is not { } faction)
        {
            return;
        }

        if (!_remoteFire.TryTakeControl(ent, faction, grid, args.Actor, out var error))
        {
            Deny(ent, args.Actor, error);
            return;
        }

        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):player} took remote fire control of territory '{territory.RegionId}' from {ToPrettyString(ent):console}");
    }

    private void OnRush(Entity<TerritoryConsoleComponent> ent, ref PersistentPoiProductionRushMessage args)
    {
        if (!TryGetProductionOrder(ent, args.Actor, out var grid) ||
            !TryGetMachine(grid, args.Machine, out var machine))
        {
            return;
        }

        if (!_production.TryRush(machine, args.Actor, args.Source, out var price, out var error))
        {
            Deny(ent, args.Actor, error);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-rush-done",
                ("price", price), ("source", _production.SourceName(args.Source))), ent, args.Actor);
        }

        UpdateUi(ent);
    }

    private void OnOvertime(Entity<TerritoryConsoleComponent> ent, ref PersistentPoiProductionOvertimeMessage args)
    {
        if (!TryGetProductionOrder(ent, args.Actor, out var grid) ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            return;
        }

        if (!_production.TryBuyOvertime(grid, territory, args.Actor, args.Source, out var price, out var error))
        {
            Deny(ent, args.Actor, error);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-overtime-done",
                ("price", price), ("source", _production.SourceName(args.Source))), ent, args.Actor);
        }

        UpdateUi(ent);
    }

    private bool HoldsSelected(Entity<TerritoryConsoleComponent> ent)
    {
        return ent.Comp.Selected is { } regionId &&
               _regions.GetRegions().Any(status =>
                   string.Equals(status.RegionId, regionId, StringComparison.OrdinalIgnoreCase) &&
                   IsFaction(ent, status.Owner));
    }

    private bool TryGetHeldGrid(
        Entity<TerritoryConsoleComponent> ent,
        out EntityUid grid,
        [NotNullWhen(true)] out PersistentPoiGridComponent? territory)
    {
        grid = default;
        territory = null;

        if (ent.Comp.Selected is not { } regionId ||
            !_regions.TryGetLiveRegion(regionId, out var region) ||
            Transform(region).GridUid is not { } regionGrid ||
            !TryComp(regionGrid, out territory) ||
            !IsFaction(ent, territory.Owner))
        {
            territory = null;
            return false;
        }

        grid = regionGrid;
        return true;
    }

    private bool TryGetProductionOrder(Entity<TerritoryConsoleComponent> ent, EntityUid actor, out EntityUid grid)
    {
        grid = default;
        if (!IsMember(ent, actor))
        {
            Deny(ent, actor, "territory-console-not-member");
            return false;
        }

        if (!TryGetHeldGrid(ent, out grid, out _))
            return false;

        if (_production.IsConsoleOnline(grid))
            return true;

        Deny(ent, actor, "territory-console-no-production-link");
        return false;
    }

    private bool TryGetMachine(EntityUid grid, NetEntity netMachine, out Entity<PersistentPoiProductionMachineComponent> machine)
    {
        machine = default;

        if (!TryGetEntity(netMachine, out var uid) ||
            !TryComp<PersistentPoiProductionMachineComponent>(uid, out var comp) ||
            Transform(uid.Value).GridUid != grid)
        {
            return false;
        }

        machine = (uid.Value, comp);
        return true;
    }

    private static bool IsFaction(Entity<TerritoryConsoleComponent> ent, string? owner)
    {
        return owner != null &&
               ent.Comp.Faction != null &&
               string.Equals(owner, ent.Comp.Faction, StringComparison.Ordinal);
    }

    private bool IsMember(Entity<TerritoryConsoleComponent> ent, EntityUid actor)
    {
        return IsFaction(ent, PersistentTerritoryFactions.GetTeam(EntityManager, actor));
    }

    private bool CanCommand(Entity<TerritoryConsoleComponent> ent, EntityUid actor)
    {
        if (!IsMember(ent, actor))
            return false;

        if (ent.Comp.CommandAccess.Count == 0)
            return true;

        var tags = _access.FindAccessTags(actor);
        return ent.Comp.CommandAccess.Any(set => set.IsSubsetOf(tags));
    }

    private bool TryCommand(Entity<TerritoryConsoleComponent> ent, EntityUid actor)
    {
        if (CanCommand(ent, actor))
            return true;

        Deny(ent, actor, IsMember(ent, actor) ? "territory-console-command-denied" : "territory-console-not-member");
        return false;
    }

    private void Deny(EntityUid console, EntityUid actor, string message)
    {
        _popup.PopupEntity(Loc.GetString(message), console, actor, PopupType.SmallCaution);
    }

    private void UpdateUi(Entity<TerritoryConsoleComponent> ent)
    {
        var viewer = _ui.GetActors(ent.Owner, TerritoryConsoleUiKey.Key).FirstOrDefault();
        var state = new TerritoryConsoleState
        {
            Faction = ent.Comp.Faction,
            CanHoldTerritory = PersistentTerritoryFactions.IsSupported(ent.Comp.Faction),
            Selected = ent.Comp.Selected,
            CanCommand = viewer.IsValid() && CanCommand(ent, viewer),
        };

        CountWorks();

        foreach (var status in _regions.GetRegions())
        {
            var summary = new TerritorySummary
            {
                RegionId = status.RegionId,
                Name = status.CustomName != null ? status.Name : PersistentTerritoryFactions.StripOwnerPrefix(status.Name),
                Owner = status.Owner,
                Level = _progression.GetLevel(status.RegionId),
                Loaded = status.Loaded,
            };

            if (status.Loaded && IsFaction(ent, status.Owner) && _regions.TryGetLiveRegion(status.RegionId, out var region))
            {
                if (TryComp<CaptureFlagComponent>(region, out var flag) && flag.Stage != CaptureFlagStage.Idle)
                {
                    summary.Stage = flag.Stage;
                    summary.Attacker = flag.ActiveTeam;
                    summary.Progress = (float) _regions.GetCaptureProgress(region.Comp);
                }

                if (Transform(region).GridUid is { } grid && _worksByGrid.TryGetValue(grid, out var works))
                {
                    summary.Works = works.Works;
                    summary.WorksRunning = works.Running;
                    summary.Stored = works.Stored;
                }
            }

            state.Territories.Add(summary);
        }

        state.Territories = state.Territories
            .OrderByDescending(t => IsFaction(ent, t.Owner))
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (TryGetHeldGrid(ent, out var heldGrid, out var heldTerritory))
        {
            state.Administration = _progression.BuildAdministrationState(heldGrid);
            state.Production = _production.BuildConsoleState(heldGrid, viewer.IsValid() ? viewer : null);
            state.ProductionLinked = _production.IsConsoleOnline(heldGrid);
            state.Defence = _defence.BuildState(heldGrid, heldTerritory);
            state.Defence.RemoteFireAvailable = ent.Comp.Faction is { } holder && _remoteFire.TryGetConsole(heldGrid, holder, out _, out _);
        }

        if (ent.Comp.Selected is { } selected && HoldsSelected(ent))
        {
            state.SelectedHeld = true;
            state.CustomName = _regions.GetCustomName(selected);
            state.NamePrefix = ent.Comp.Faction is { } faction ? FactionDisplay.Abbreviation(faction) : null;
        }

        _ui.SetUiState(ent.Owner, TerritoryConsoleUiKey.Key, state);
    }

    private void CountWorks()
    {
        _worksByGrid.Clear();

        var machines = EntityQueryEnumerator<PersistentPoiProductionMachineComponent, TransformComponent>();
        while (machines.MoveNext(out var machine, out var xform))
        {
            if (xform.GridUid is not { } grid)
                continue;

            var counts = _worksByGrid.GetValueOrDefault(grid);
            counts.Works++;
            counts.Stored += machine.Stored;
            if (machine.Status is PersistentPoiProductionStatus.Running or PersistentPoiProductionStatus.LowPower)
                counts.Running++;

            _worksByGrid[grid] = counts;
        }
    }
}
