using Content.Server.Administration.Managers;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Bank;
using Content.Server.Chemistry.Containers.EntitySystems;
using Content.Server.Cargo.Systems;
using Content.Server._Crescent.DynamicAcces;
using Content.Server.Decals;
using Content.Server.PointCannons;
using Content.Server.Power.EntitySystems;
using Content.Server.Popups;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Crescent.RepairStation;
using Content.Shared.Administration.Logs;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Server.GameObjects;
using Content.Shared.Damage;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.RepairStation;

/// <summary>
/// Drives the automated repair slip. It surveys ships clamped to the console's own grid against the
/// structural snapshot they carry, quotes the customer slightly above what the missing hull is worth,
/// then reinstates it part by part with RCD flashes so the ship visibly comes back together.
/// </summary>
public sealed partial class ShipRepairStationSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly DynamicCodeSystem _dynamicCodes = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly PointCannonSystem _pointCannons = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly ShipDrydockSnapshotSystem _drydock = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedHardpointSystem _hardpoint = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedShipRepairSystem _shipRepair = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SolutionContainerSystem _solution = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    /// <summary>
    /// How often a console with an open window is told about progress. The window counts the ETA
    /// down on its own in between.
    /// </summary>
    private static readonly TimeSpan UiUpdateInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on batches worked through in one Update, so a server catching up after a stall
    /// cannot rebuild a whole hull in a single frame.
    /// </summary>
    private const int MaxCatchUpTicks = 8;

    /// <summary>
    /// Which ship each console is pointed at. Selection is per console rather than per viewer, the
    /// same way the rest of the ship consoles behave.
    /// </summary>
    private readonly Dictionary<EntityUid, EntityUid> _selected = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShipRepairStationComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ShipRepairStationComponent, ShipRepairSelectMessage>(OnSelect);
        SubscribeLocalEvent<ShipRepairStationComponent, ShipRepairStartMessage>(OnStart);
        SubscribeLocalEvent<ShipRepairStationComponent, ShipRepairCancelMessage>(OnCancel);
        SubscribeLocalEvent<ShipRepairStationComponent, ComponentShutdown>(OnShutdown);

        InitializeAdmin();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ShipRepairStationComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var station = (uid, comp);

            if (comp.Target != null)
            {
                if (_power.IsPowered(uid))
                {
                    var ticks = 0;
                    while (comp.Target != null && now >= comp.NextTickTime && ticks++ < MaxCatchUpTicks)
                    {
                        comp.NextTickTime += comp.TickInterval;
                        RunTick(station);
                    }
                }
                else
                {
                    // Nothing is welded on a dead bus. The whole schedule slides with the stall, so the
                    // job picks up where it stopped instead of tearing through the backlog in one frame
                    // when the power comes back, and the bar and the ETA sit still while it waits.
                    var stall = TimeSpan.FromSeconds(frameTime);
                    comp.StartTime += stall;
                    comp.NextTickTime += stall;
                    comp.EndTime += stall;
                }
            }

            if (comp.AtmosTarget is { } refill && now >= comp.AtmosRefreshTime)
            {
                var seeds = comp.AtmosSeeds;
                comp.AtmosTarget = null;
                comp.AtmosSeeds = new HashSet<Vector2i>();
                RefreshAtmosphere(refill, seeds);
            }

            if (now < comp.NextUiUpdate)
                continue;

            comp.NextUiUpdate = now + UiUpdateInterval;

            if (_ui.IsUiOpen(uid, ShipRepairStationUiKey.Key))
                UpdateUi(station);
        }
    }

    private void OnShutdown(Entity<ShipRepairStationComponent> station, ref ComponentShutdown args)
    {
        // Whoever paid should not lose the unspent half of the job because the console was shot.
        AbortRepair(station, refund: true);
        _selected.Remove(station.Owner);
    }

    // ---------------------------------------------------------------------------------------------
    // UI
    // ---------------------------------------------------------------------------------------------

    private void OnUiOpened(Entity<ShipRepairStationComponent> station, ref BoundUIOpenedEvent args)
    {
        UpdateUi(station);
    }

    private void OnSelect(Entity<ShipRepairStationComponent> station, ref ShipRepairSelectMessage args)
    {
        if (!CanUse(station, args.Actor))
            return;

        var grid = GetEntity(args.Grid);
        if (IsServiceable(station, grid))
            _selected[station.Owner] = grid;

        PushUi(station);
    }

    private void UpdateUi(Entity<ShipRepairStationComponent> station)
    {
        var comp = station.Comp;
        var docked = GetServiceableGrids(station);

        var entries = new List<ShipRepairDockEntry>(docked.Count);
        foreach (var ship in docked)
        {
            entries.Add(new ShipRepairDockEntry
            {
                Grid = GetNetEntity(ship),
                Name = Name(ship),
                HasBlueprint = HasComp<ShipRepairDataComponent>(ship),
            });
        }

        var selected = GetSelection(station, docked);

        var state = new ShipRepairStationUiState
        {
            Docked = entries,
            Selected = selected == null ? null : GetNetEntity(selected.Value),
            Free = comp.Free,
            CanSnapshot = comp.AllowSnapshot,
        };

        if (comp.Target is { } target && !TerminatingOrDeleted(target))
        {
            state.Status = ShipRepairStatus.Repairing;
            state.RepairingName = Name(target);
            state.JobsTotal = comp.JobsTotal;
            state.JobsDone = comp.JobsDone;
            state.StartTime = comp.StartTime;
            state.EndTime = comp.EndTime;
        }
        else if (selected == null)
        {
            state.Status = ShipRepairStatus.NoShip;
        }
        else if (IsShipBusyElsewhere(station, selected.Value))
        {
            state.Status = ShipRepairStatus.Busy;
        }
        else
        {
            // A hull nobody ever filed a blueprint for - a station, a fleet ship that was mapped in
            // rather than bought - is refused outright by a crew slip. The override console works it
            // anyway, on the part of the survey that is judged by looking at the hull itself.
            var data = CompOrNull<ShipRepairDataComponent>(selected.Value);
            state.BlueprintMissing = data == null;

            if (data == null && !comp.RepairUnregistered)
            {
                state.Status = ShipRepairStatus.NoBlueprint;
            }
            else
            {
                var survey = Survey(station, selected.Value, data);
                state.MissingTiles = survey.MissingTiles;
                state.StrippedTiles = survey.StrippedTiles;
                state.MissingParts = survey.MissingParts;
                state.DamagedParts = survey.DamagedParts;
                state.Decals = survey.Decals;
                state.Spills = survey.Spills;
                state.Debris = survey.Debris;
                state.Restocks = survey.Restocks;
                state.Quote = survey.Quote;
                state.Status = survey.Jobs.Count == 0 ? ShipRepairStatus.Intact : ShipRepairStatus.Quoted;
            }
        }

        _ui.SetUiState(station.Owner, ShipRepairStationUiKey.Key, state);
    }

    /// <summary>
    /// Forces a state push outside the once-a-second cadence, for the moments the player expects an
    /// immediate answer to a button press.
    /// </summary>
    private void PushUi(Entity<ShipRepairStationComponent> station)
    {
        station.Comp.NextUiUpdate = _timing.CurTime + UiUpdateInterval;
        UpdateUi(station);
    }

    // ---------------------------------------------------------------------------------------------
    // Docking
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Hulls this console may work on: whatever is clamped directly to the console's own grid, and on
    /// an override slip the grid it is standing on as well. A ship docked to another ship that is in
    /// turn docked here is deliberately out of reach - the slip only works on what it is holding.
    /// </summary>
    private List<EntityUid> GetServiceableGrids(Entity<ShipRepairStationComponent> station)
    {
        var result = new List<EntityUid>();

        if (Transform(station.Owner).GridUid is not { } ourGrid)
            return result;

        // The override slip works the deck it is standing on too, so a station is repaired by putting a
        // console down on it rather than by finding something big enough to dock to it.
        if (station.Comp.ServiceOwnGrid)
            result.Add(ourGrid);

        foreach (var dock in _docking.GetDocks(ourGrid))
        {
            if (dock.Comp.DockedWith is not { } other || TerminatingOrDeleted(other))
                continue;

            if (Transform(other).GridUid is not { } otherGrid || otherGrid == ourGrid)
                continue;

            if (!result.Contains(otherGrid))
                result.Add(otherGrid);
        }

        return result;
    }

    private bool IsServiceable(Entity<ShipRepairStationComponent> station, EntityUid grid)
    {
        return GetServiceableGrids(station).Contains(grid);
    }

    /// <summary>
    /// The ship the console is pointed at, falling back to the first hull within its reach.
    /// </summary>
    private EntityUid? GetSelection(Entity<ShipRepairStationComponent> station, List<EntityUid> docked)
    {
        if (_selected.TryGetValue(station.Owner, out var picked) && docked.Contains(picked))
            return picked;

        _selected.Remove(station.Owner);
        return docked.Count > 0 ? docked[0] : null;
    }

    /// <summary>
    /// Keeps two consoles from fighting over the same hull.
    /// </summary>
    private bool IsShipBusyElsewhere(Entity<ShipRepairStationComponent> station, EntityUid grid)
    {
        var query = EntityQueryEnumerator<ShipRepairStationComponent>();
        while (query.MoveNext(out var uid, out var other))
        {
            if (uid != station.Owner && other.Target == grid)
                return true;
        }

        return false;
    }
}
