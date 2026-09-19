using System.Numerics;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.Economy;
using Content.Server._Crescent.ShipAI;
using Content.Server._Mono.NPC.HTN;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Explosion.EntitySystems;
using Content.Server.NPC.HTN;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.DroneControl;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mind.Components;
using Content.Shared.Popups;
using Content.Shared.Shipyard.Components;
using Content.Shared.Shipyard.Prototypes;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using StationTradeMarketSystem = Content.Server.Crescent.Dispenser.StationTradeMarketSystem;

namespace Content.Server._Crescent.DroneControl;

/// <summary>
///     Drives <see cref="AutoDroneComponent"/> drones for a <see cref="DroneCarrierComponent"/> console:
///     claims drones docked to (or linked to) the carrier, undocks them, holds them in a selectable
///     formation, and directs them to orbit and focus-fire diplomatic enemies. Manual console orders
///     temporarily override the autopilot (routed here from <see cref="DroneControlSystem"/>).
/// </summary>
public sealed class AutoDroneSystem : EntitySystem
{
    [Dependency] private readonly DeviceListSystem _deviceList = default!;
    [Dependency] private readonly DiplomacySystem _diplomacy = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly EconomyPriceSystem _economyPrice = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedShuttleSystem _shuttle = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShipSteeringSystem _steering = default!;
    [Dependency] private readonly ShipTargetingSystem _targeting = default!;
    [Dependency] private readonly ShipWeaponArcSystem _arc = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StationTradeMarketSystem _market = default!;

    private EntityQuery<ApcPowerReceiverComponent> _powerQuery;
    private EntityQuery<AutoDroneComponent> _autoQuery;
    private EntityQuery<DroneControlComponent> _droneServerQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<ShipTargetingComponent> _targetingQuery;
    private EntityQuery<ShipSteererComponent> _steererQuery;

    private const float UpdateInterval = 0.25f;
    private const int SlowEveryNTicks = 4; // ~1s between deployment scans, enemy scans and hull readouts
    private const float FriendlyHoldFireRange = 300f; // hold fire if a friendly is this close in front
    private const float PendingSpawnTtl = 20f; // seconds a produced-but-unclaimed drone counts against the cap

    /// <summary>
    ///     Accuracy of the drones' target leading. Matches the value the hand-written HTN drone task uses, so
    ///     an autopiloted drone is no deadlier than any other AI ship - the component default is a perfect 1.0.
    /// </summary>
    private const float DroneLeadingAccuracy = 0.4f;

    private float _accumulator;
    private int _slowTick;

    // scratch collections, reused between ticks
    private readonly HashSet<Entity<DockingComponent>> _docks = new();
    private readonly HashSet<Entity<AutoDroneComponent>> _dockedDrones = new();
    private readonly HashSet<EntityUid> _enemies = new();
    private readonly HashSet<EntityUid> _enemyDrones = new(); // enemy grids that carry a drone server
    private readonly List<KeyValuePair<int, EntityUid>> _slotScratch = new();
    private readonly List<Entity<AutoDroneComponent>> _scuttleScratch = new();
    private List<Entity<MapGridComponent>> _gridScratch = new();

    /// <summary>
    ///     Wrecks left by a scuttling charge, due for removal once the blast has had time to tear them apart.
    ///     Held here rather than on the drone because the explosion usually destroys the control server.
    /// </summary>
    private readonly List<(TimeSpan At, EntityUid Grid)> _scuttleCleanup = new();

    public override void Initialize()
    {
        base.Initialize();

        _powerQuery = GetEntityQuery<ApcPowerReceiverComponent>();
        _autoQuery = GetEntityQuery<AutoDroneComponent>();
        _droneServerQuery = GetEntityQuery<DroneControlComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _targetingQuery = GetEntityQuery<ShipTargetingComponent>();
        _steererQuery = GetEntityQuery<ShipSteererComponent>();

        // System fields outlive a round but entity uids don't, so drop any pending wreck cleanup on restart.
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _scuttleCleanup.Clear());

        SubscribeLocalEvent<AutoDroneComponent, ComponentShutdown>(OnDroneShutdown);
        SubscribeLocalEvent<DroneCarrierComponent, ComponentShutdown>(OnCarrierShutdown);
        SubscribeLocalEvent<DroneCarrierComponent, GetVerbsEvent<AlternativeVerb>>(OnCarrierGetVerbs);

        SubscribeLocalEvent<DroneCarrierComponent, DroneConsoleDeployMessage>(OnUiDeploy);
        SubscribeLocalEvent<DroneCarrierComponent, DroneConsoleSetStanceMessage>(OnUiSetStance);
        SubscribeLocalEvent<DroneCarrierComponent, DroneConsoleSetTargetingMessage>(OnUiSetTargeting);
        SubscribeLocalEvent<DroneCarrierComponent, DroneConsoleSetFormationMessage>(OnUiSetFormation);
        SubscribeLocalEvent<DroneCarrierComponent, DroneConsoleSpawnMessage>(OnUiSpawn);
        SubscribeLocalEvent<DroneCarrierComponent, DroneConsoleSelfDestructMessage>(OnUiSelfDestruct);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < UpdateInterval)
            return;
        _accumulator = 0f;

        var now = _timing.CurTime;
        // Claiming walks docks/station grids and the enemy scan sweeps a multi-kilometre radius, so run both
        // less often than the steering update.
        var slowTick = _slowTick++ % SlowEveryNTicks == 0;

        // Runs over every drone, not just commanded ones: an orphaned drone still has to be able to scuttle
        // itself after its carrier console is gone.
        UpdateSelfDestruct(now);

        var query = EntityQueryEnumerator<DroneCarrierComponent>();
        while (query.MoveNext(out var consoleUid, out var carrier))
        {
            var carrierGrid = Transform(consoleUid).GridUid;
            if (carrierGrid == null)
                continue;

            // An unpowered console commands nothing. Deployment, targeting and steering all stop together, so
            // knocking the console out is a real counter instead of only stopping new launches.
            if (!IsConsolePowered(consoleUid))
            {
                IdleDrones((consoleUid, carrier));
                continue;
            }

            if (slowTick)
            {
                TryDeploy((consoleUid, carrier), carrierGrid.Value);
                UpdateHullIntegrity(carrier);
            }

            // Find diplomatic enemies near the carrier (leash), pick a focus, then drive each drone.
            // The stance decides whether and how far the drones look for targets.
            if (carrier.Stance == DroneStance.Follow)
            {
                _enemies.Clear();
                carrier.FocusTarget = null;
            }
            else if (slowTick)
            {
                var faction = GetGridFaction(carrierGrid.Value);
                var range = carrier.Stance == DroneStance.Defend ? carrier.DefendRange : carrier.EngagementRange;
                ScanEnemies(carrierGrid.Value, faction, range, carrier.Targeting);
                SelectFocus(carrier, carrierGrid.Value);
            }

            DriveDrones((consoleUid, carrier), now);
        }
    }

    /// <summary>
    ///     A console with no power receiver counts as powered (unpowered otherwise), matching how the rest of
    ///     the computer prototypes behave.
    /// </summary>
    private bool IsConsolePowered(EntityUid console)
    {
        return !_powerQuery.TryComp(console, out var receiver) || _power.IsPowered(console, receiver);
    }

    /// <summary>
    ///     A grid's diplomatic faction, or null if it has none we recognise. See
    ///     <see cref="DiplomacySystem.GetGridFaction"/> for what "none we recognise" covers.
    /// </summary>
    private string? GetGridFaction(EntityUid grid)
    {
        return _diplomacy.GetGridFaction(grid);
    }

    #region deployment

    private void TryDeploy(Entity<DroneCarrierComponent> carrier, EntityUid carrierGrid)
    {
        if (carrier.Comp.ProducedCount >= EffectiveMaxDrones(carrier.Comp))
            return;

        // Formation is anchored to the console's own grid.
        if (!_gridQuery.TryComp(carrierGrid, out var carrierGridComp))
            return;

        // 1) Drones docked directly to the carrier ship.
        ScanGridForDockedDrones(carrier, carrierGrid, carrierGridComp, carrierGrid);
        if (carrier.Comp.ProducedCount >= EffectiveMaxDrones(carrier.Comp))
            return;

        // 2) Drones docked to any grid of the console's owning station: a shipyard docks purchased drones to
        //    the station's largest grid, which is often not the same grid the drone console sits on.
        if (_station.GetOwningStation(carrier.Owner) is { } station && TryComp<StationDataComponent>(station, out var data))
        {
            foreach (var stationGrid in data.Grids)
            {
                if (stationGrid == carrierGrid)
                    continue;

                ScanGridForDockedDrones(carrier, carrierGrid, carrierGridComp, stationGrid);
                if (carrier.Comp.ProducedCount >= EffectiveMaxDrones(carrier.Comp))
                    return;
            }
        }

        // 3) Drones already linked to this console's device list (e.g. wired up with a network configurator),
        //    wherever they currently are.
        if (HasComp<DeviceListComponent>(carrier.Owner))
        {
            foreach (var (_, device) in _deviceList.GetDeviceList(carrier.Owner))
            {
                if (!_autoQuery.TryComp(device, out var ad) || ad.CarrierConsole != null)
                    continue;

                DeployDrone(carrier, carrierGrid, carrierGridComp, (device, ad));
                if (carrier.Comp.ProducedCount >= EffectiveMaxDrones(carrier.Comp))
                    return;
            }
        }

        // NOTE: there is deliberately NO proximity/faction "claim any nearby drone" scan. In multiplayer two
        // same-faction carriers sitting near each other would each poach the other's freshly produced drones.
        // Produced drones are instead bound to their exact producing console the instant they are made
        // (see OnUiSpawn -> TryBindDroneGrid), so no scan can ever steal them.
    }

    /// <summary>
    ///     Binds an unclaimed drone sitting on <paramref name="droneGrid"/> to this specific carrier. Used the
    ///     moment a drone is produced so no other console's scan can claim it. Returns true if it bound one.
    /// </summary>
    private bool TryBindDroneGrid(Entity<DroneCarrierComponent> carrier, EntityUid carrierGrid, MapGridComponent carrierGridComp, EntityUid droneGrid)
    {
        if (!_gridQuery.TryComp(droneGrid, out var dgrid))
            return false;

        _dockedDrones.Clear();
        _lookup.GetLocalEntitiesIntersecting(droneGrid, dgrid.LocalAABB, _dockedDrones);
        foreach (var drone in _dockedDrones)
        {
            if (drone.Comp.CarrierConsole != null)
                continue;

            var before = carrier.Comp.ProducedCount;
            DeployDrone(carrier, carrierGrid, carrierGridComp, drone, produced: true);
            return carrier.Comp.ProducedCount > before;
        }

        return false;
    }

    /// <summary>
    ///     Finds undeployed <see cref="AutoDroneComponent"/> drones docked to <paramref name="scanGrid"/> and
    ///     deploys them into the given carrier's formation (anchored to <paramref name="carrierGrid"/>).
    /// </summary>
    private void ScanGridForDockedDrones(Entity<DroneCarrierComponent> carrier, EntityUid carrierGrid, MapGridComponent carrierGridComp, EntityUid scanGrid)
    {
        if (!_gridQuery.TryComp(scanGrid, out var grid))
            return;

        _docks.Clear();
        _lookup.GetLocalEntitiesIntersecting(scanGrid, grid.LocalAABB, _docks);

        foreach (var dock in _docks)
        {
            if (dock.Comp.DockedWith == null)
                continue;

            var partnerGrid = Transform(dock.Comp.DockedWith.Value).GridUid;
            if (partnerGrid == null || partnerGrid == scanGrid)
                continue;

            if (!_gridQuery.TryComp(partnerGrid, out var partnerGridComp))
                continue;

            // A partner grid is a valid drone only if it carries an AutoDrone control server. Anything else
            // (the station the carrier is parked at, a player ship, ...) simply has none and is skipped.
            _dockedDrones.Clear();
            _lookup.GetLocalEntitiesIntersecting(partnerGrid.Value, partnerGridComp.LocalAABB, _dockedDrones);
            foreach (var drone in _dockedDrones)
            {
                if (drone.Comp.CarrierConsole != null)
                    continue; // already deployed elsewhere

                // A drone we produced ourselves may have failed to bind on the spawn tick and be waiting here.
                var produced = ConsumePendingSpawn(carrier.Comp, partnerGrid.Value);

                DeployDrone(carrier, carrierGrid, carrierGridComp, drone, produced);

                if (carrier.Comp.ProducedCount >= EffectiveMaxDrones(carrier.Comp))
                    return;
            }
        }
    }

    /// <summary>
    ///     Consumes the in-flight production record for <paramref name="droneGrid"/> if we are the console
    ///     that produced it. Matching on the grid (rather than popping the oldest entry) keeps a drone that
    ///     merely docked here by hand from eating a production slot it never used.
    /// </summary>
    private bool ConsumePendingSpawn(DroneCarrierComponent carrier, EntityUid droneGrid)
    {
        for (var i = 0; i < carrier.PendingSpawns.Count; i++)
        {
            if (carrier.PendingSpawns[i].Grid != droneGrid)
                continue;

            carrier.PendingSpawns.RemoveAt(i);
            return true;
        }

        return false;
    }

    private void DeployDrone(Entity<DroneCarrierComponent> carrier, EntityUid carrierGrid, MapGridComponent grid, Entity<AutoDroneComponent> drone, bool produced = false)
    {
        // A drone its carrier already wrote off stays a derelict; it can't be docked back in to dodge a restock.
        if (drone.Comp.WrittenOff)
            return;

        var droneGrid = Transform(drone.Owner).GridUid;

        var carrierFaction = GetGridFaction(carrierGrid);
        var droneFaction = droneGrid != null ? GetGridFaction(droneGrid.Value) : null;

        // Only ever claim our own faction's drones, or unaligned ones (which is what a freshly produced hull
        // looks like before we stamp our IFF on it). Claiming anyone else's - even a neutral or allied
        // faction's - would let a carrier walk off with another player's ship just because it docked here.
        if (droneFaction != null && droneFaction != carrierFaction)
            return;

        var slot = GetFreeSlot(carrier.Comp);
        if (slot < 0)
            return;

        carrier.Comp.Slots[slot] = drone.Owner;
        carrier.Comp.ProducedCount++; // lifetime count, never decremented (hard production cap)

        drone.Comp.CarrierConsole = carrier.Owner;
        drone.Comp.Slot = slot;
        drone.Comp.SlotCoordinates = ComputeSlot(carrierGrid, grid, carrier.Comp, slot);
        drone.Comp.Mode = AutoDroneMode.Follow;
        drone.Comp.Produced = produced;
        drone.Comp.Launched = false;
        drone.Comp.Undocked = false;
        drone.Comp.SelfDestructAt = null;

        // Baseline for the console's hull readout. Taken at deploy so battle damage shows up as a shortfall.
        drone.Comp.InitialTileCount = droneGrid != null ? CountTiles(droneGrid.Value) : 0;
        drone.Comp.HullIntegrity = 1f;

        // Adopt the carrier's faction + colour so diplomacy/radar treat the drone consistently (instead of the
        // default gold a bare grid shows).
        if (droneGrid != null)
        {
            if (carrierFaction != null)
                _shuttle.SetIFFFaction(droneGrid.Value, carrierFaction);
            _shuttle.SetIFFColor(droneGrid.Value, carrier.Comp.IffColor);

            // Only a drone we built ourselves loses its deed. Stripping it from a hull somebody else owns
            // would destroy their claim on a ship we are merely borrowing.
            if (produced)
                RemComp<ShuttleDeedComponent>(droneGrid.Value);
        }

        // A leftover HTN plan drives the very same steering/targeting components this system does, so the two
        // would fight over the drone every tick. Tear it down before we take the controls.
        if (TryComp<HTNComponent>(drone.Owner, out var htn))
        {
            _htn.ShutdownPlan(htn);
            if (TryComp<DroneControlComponent>(drone.Owner, out var droneControl))
            {
                htn.Blackboard.Remove<string>(droneControl.OrderKey);
                htn.Blackboard.Remove<EntityCoordinates>(droneControl.TargetKey);
            }
        }

        // Link the drone to the carrier console so it shows on the console UI and can receive manual override
        // orders after it has undocked. Mirrors the console's manual autolink.
        if (TryComp<DroneControlComponent>(drone.Owner, out var control))
            control.Autolinked = true;
        if (HasComp<DeviceListComponent>(carrier.Owner))
            _deviceList.UpdateDeviceList(carrier.Owner, new List<EntityUid> { drone.Owner }, true);

        // Cast off from the carrier (no-op if it wasn't docked).
        TryUndock(drone);
    }

    /// <summary>
    ///     Casts a drone off whatever it is docked to, once. Undocking sweeps the whole drone grid for docking
    ///     ports, so it is latched rather than re-run every tick - and re-running it would also make it
    ///     impossible to ever dock to a deployed drone to repair or recover it.
    /// </summary>
    private void TryUndock(Entity<AutoDroneComponent> drone)
    {
        if (drone.Comp.Undocked)
            return;

        if (Transform(drone.Owner).GridUid is not { } droneGrid)
            return;

        _docking.UndockDocks(droneGrid);
        drone.Comp.Undocked = true;
    }

    private int GetFreeSlot(DroneCarrierComponent carrier)
    {
        var max = EffectiveMaxDrones(carrier);
        for (var i = 0; i < max; i++)
        {
            if (!carrier.Slots.ContainsKey(i))
                return i;
        }

        return -1;
    }

    private static int EffectiveMaxDrones(DroneCarrierComponent carrier) => Math.Max(0, carrier.MaxDrones);

    private int CountTiles(EntityUid gridUid)
    {
        if (!_gridQuery.TryComp(gridUid, out var grid))
            return 0;

        var count = 0;
        var enumerator = _map.GetAllTilesEnumerator(gridUid, grid);
        while (enumerator.MoveNext(out _))
            count++;

        return count;
    }

    /// <summary>
    ///     Refreshes each commanded drone's hull readout. Walks every tile of the drone grids, so it runs on
    ///     the slow tick only.
    /// </summary>
    private void UpdateHullIntegrity(DroneCarrierComponent carrier)
    {
        foreach (var droneUid in carrier.Slots.Values)
        {
            if (!_autoQuery.TryComp(droneUid, out var drone) || drone.InitialTileCount <= 0)
                continue;

            if (Transform(droneUid).GridUid is not { } gridUid)
                continue;

            drone.HullIntegrity = Math.Clamp(CountTiles(gridUid) / (float) drone.InitialTileCount, 0f, 1f);
        }
    }

    /// <summary>
    ///     Drops a disabled drone from the carrier for good. Its slot is freed but the hangar is not refilled,
    ///     so the loss shows on the console until a repair station restocks it.
    /// </summary>
    private void WriteOffDrone(Entity<DroneCarrierComponent> carrier, int slot, Entity<AutoDroneComponent> drone)
    {
        var name = Transform(drone.Owner).GridUid is { } grid ? Name(grid) : Name(drone.Owner);

        UndeploySlot(carrier, slot);
        drone.Comp.WrittenOff = true;
        drone.Comp.UnpoweredSince = null;

        _popup.PopupEntity(Loc.GetString("drone-carrier-drone-lost", ("drone", name)), carrier.Owner, PopupType.MediumCaution);
    }

    #endregion

    #region hangar

    /// <summary>
    ///     Drones this carrier produced that are no longer under its command.
    /// </summary>
    public int GetLostDrones(DroneCarrierComponent carrier)
    {
        return Math.Clamp(carrier.ProducedCount - carrier.Slots.Count, 0, EffectiveMaxDrones(carrier));
    }

    /// <summary>
    ///     Drones still in the hangar, i.e. how many more can be produced right now.
    /// </summary>
    public int GetHangarCount(DroneCarrierComponent carrier)
    {
        return Math.Max(0, EffectiveMaxDrones(carrier) - carrier.ProducedCount - carrier.PendingSpawns.Count);
    }

    /// <summary>
    ///     Repair station fee for restocking this carrier's lost drones: <see cref="DroneCarrierComponent.RestockCostMin"/>
    ///     for one, rising linearly to <see cref="DroneCarrierComponent.RestockCostMax"/> for the whole squadron.
    /// </summary>
    public int GetRestockCost(DroneCarrierComponent carrier)
    {
        var lost = GetLostDrones(carrier);
        if (lost <= 0)
            return 0;

        var max = EffectiveMaxDrones(carrier);
        if (max <= 1)
            return carrier.RestockCostMin;

        var t = (lost - 1) / (float) (max - 1);
        return (int) MathF.Round(carrier.RestockCostMin + (carrier.RestockCostMax - carrier.RestockCostMin) * t);
    }

    /// <summary>
    ///     Lost drones and restock fee summed over every carrier console aboard <paramref name="grid"/>.
    ///     <paramref name="console"/> is the first console that needs restocking.
    /// </summary>
    public (int Lost, int Cost) GetHangarRestock(EntityUid grid, out EntityUid? console)
    {
        console = null;
        var lost = 0;
        var cost = 0;

        var query = EntityQueryEnumerator<DroneCarrierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var carrier, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            var carrierLost = GetLostDrones(carrier);
            if (carrierLost <= 0)
                continue;

            console ??= uid;
            lost += carrierLost;
            cost += GetRestockCost(carrier);
        }

        return (lost, cost);
    }

    /// <summary>
    ///     Refills the hangar of every carrier console aboard <paramref name="grid"/>, so lost drones can be
    ///     produced again. Returns how many drones were restocked.
    /// </summary>
    public int RestockHangars(EntityUid grid)
    {
        var restocked = 0;

        var query = EntityQueryEnumerator<DroneCarrierComponent, TransformComponent>();
        while (query.MoveNext(out var carrier, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            var lost = GetLostDrones(carrier);
            carrier.ProducedCount -= lost;
            restocked += lost;
        }

        return restocked;
    }

    #endregion

    #region formation

    /// <summary>
    ///     Distance from the carrier grid's origin out to the furthest corner of its hull. Drones measure
    ///     their separation from that same origin, so the launch clearance and the formation slots have to be
    ///     expressed against it too - measuring one from the origin and the other from the hull's rear edge
    ///     lets the two disagree, and the drone then oscillates between flying out and forming up.
    /// </summary>
    private static float GetHullRadius(MapGridComponent grid)
    {
        var aabb = grid.LocalAABB;
        var x = MathF.Max(MathF.Abs(aabb.Left), MathF.Abs(aabb.Right));
        var y = MathF.Max(MathF.Abs(aabb.Bottom), MathF.Abs(aabb.Top));
        return new Vector2(x, y).Length();
    }

    /// <summary>
    ///     Formation slot as a carrier-grid-relative coordinate, anchored behind the carrier's hull so drones
    ///     never try to sit inside it. Grid-relative, so it rotates and moves with the carrier.
    /// </summary>
    private EntityCoordinates ComputeSlot(EntityUid carrierGrid, MapGridComponent grid, DroneCarrierComponent carrier, int slot)
    {
        var aabb = grid.LocalAABB;
        var offset = GetFormationOffset(carrier, slot);
        // X centered on the hull, Y measured backward from the rear edge (shuttles face grid-north / +Y).
        var local = new Vector2(aabb.Center.X + offset.X, aabb.Bottom - offset.Y);

        // Push any slot that would land inside the launch clearance ring back out past it. On a wide, shallow
        // carrier the rear-edge offset alone can leave the front row inside that ring, and a drone can then
        // never satisfy both behaviours at once.
        var minDistance = GetHullRadius(grid) + carrier.LaunchClearance;
        var distance = local.Length();
        if (distance > 0.01f && distance < minDistance)
            local *= minDistance / distance;

        return new EntityCoordinates(carrierGrid, local);
    }

    /// <summary>
    ///     Slot offset as (lateral X, distance behind the hull). Both are positive magnitudes except X's sign.
    /// </summary>
    private static Vector2 GetFormationOffset(DroneCarrierComponent carrier, int slot)
    {
        var s = carrier.FormationSpacing;
        var d = carrier.FormationDepth;
        var n = Math.Max(1, EffectiveMaxDrones(carrier));

        switch (carrier.Formation)
        {
            case DroneFormation.Arrow:
            {
                var row = slot / 2;
                var side = slot % 2 == 0 ? -1f : 1f;
                return new Vector2(side * (row + 1) * s, (row + 1) * d);
            }
            case DroneFormation.LineAbreast:
                return new Vector2((slot - (n - 1) / 2f) * s, d);
            case DroneFormation.Column:
                return new Vector2(0f, (slot + 1) * d);
            case DroneFormation.Echelon:
                return new Vector2((slot + 1) * s, (slot + 1) * d);
            case DroneFormation.Diamond:
            {
                var cd = 2f * d;
                return slot switch
                {
                    0 => new Vector2(0f, cd - d),
                    1 => new Vector2(0f, cd + d),
                    2 => new Vector2(-s, cd),
                    3 => new Vector2(s, cd),
                    _ => new Vector2((slot - (n - 1) / 2f) * s, cd),
                };
            }
            default:
                return new Vector2(0f, d);
        }
    }

    private void RecomputeFormation(EntityUid console, DroneCarrierComponent carrier)
    {
        var carrierGrid = Transform(console).GridUid;
        if (carrierGrid == null || !_gridQuery.TryComp(carrierGrid, out var grid))
            return;

        foreach (var (slot, drone) in carrier.Slots)
        {
            if (_autoQuery.TryComp(drone, out var comp))
                comp.SlotCoordinates = ComputeSlot(carrierGrid.Value, grid, carrier, slot);
        }
    }

    private void OnCarrierGetVerbs(Entity<DroneCarrierComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        // Explicit deploy so players don't depend on the automatic dock scan, and to diagnose setup.
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("drone-carrier-deploy"),
            Priority = 10,
            Act = () => ForceDeploy(ent)
        });

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("drone-carrier-cycle-formation",
                ("formation", Loc.GetString($"drone-formation-{ent.Comp.Formation.ToString().ToLowerInvariant()}"))),
            Priority = 9,
            Act = () => CycleFormation(ent)
        });

        // One verb per stance so the player picks directly; the active stance is greyed out.
        var stanceCategory = new VerbCategory(Loc.GetString("drone-stance-category"));
        foreach (var stance in Enum.GetValues<DroneStance>())
        {
            var isCurrent = ent.Comp.Stance == stance;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString($"drone-stance-{stance.ToString().ToLowerInvariant()}"),
                Category = stanceCategory,
                Priority = 8,
                Disabled = isCurrent,
                Act = () => SetStance(ent, stance)
            });
        }
    }

    private void OnUiDeploy(Entity<DroneCarrierComponent> ent, ref DroneConsoleDeployMessage args)
    {
        if (!IsConsolePowered(ent.Owner))
            return;

        ForceDeploy(ent);
    }

    private void OnUiSetStance(Entity<DroneCarrierComponent> ent, ref DroneConsoleSetStanceMessage args)
    {
        if (!IsConsolePowered(ent.Owner))
            return;

        SetStance(ent, args.Stance);
    }

    private void OnUiSetTargeting(Entity<DroneCarrierComponent> ent, ref DroneConsoleSetTargetingMessage args)
    {
        if (!IsConsolePowered(ent.Owner))
            return;

        ent.Comp.Targeting = args.Targeting;
        _popup.PopupEntity(Loc.GetString("drone-targeting-set",
            ("targeting", Loc.GetString($"drone-targeting-{args.Targeting.ToString().ToLowerInvariant()}"))),
            ent.Owner, PopupType.Medium);
    }

    private void OnUiSetFormation(Entity<DroneCarrierComponent> ent, ref DroneConsoleSetFormationMessage args)
    {
        if (!IsConsolePowered(ent.Owner))
            return;

        ent.Comp.Formation = args.Formation;
        RecomputeFormation(ent.Owner, ent.Comp);
    }

    private void OnUiSelfDestruct(Entity<DroneCarrierComponent> ent, ref DroneConsoleSelfDestructMessage args)
    {
        if (!IsConsolePowered(ent.Owner))
            return;

        var now = _timing.CurTime;
        var affected = 0;

        foreach (var droneUid in ent.Comp.Slots.Values)
        {
            if (!args.SelectedDrones.Contains(GetNetEntity(droneUid)) || !_autoQuery.TryComp(droneUid, out var drone))
                continue;

            if (args.Cancel)
            {
                if (drone.SelfDestructAt == null)
                    continue;

                drone.SelfDestructAt = null;
                drone.Mode = AutoDroneMode.Follow;
            }
            else
            {
                if (drone.SelfDestructAt != null)
                    continue; // already counting down, don't restart the clock

                drone.SelfDestructAt = now + drone.SelfDestructDelay;
            }

            affected++;
        }

        if (affected == 0)
            return;

        _popup.PopupEntity(
            Loc.GetString(args.Cancel ? "drone-self-destruct-cancelled" : "drone-self-destruct-armed", ("count", affected)),
            ent.Owner,
            args.Cancel ? PopupType.Medium : PopupType.LargeCaution);
    }

    private void OnUiSpawn(Entity<DroneCarrierComponent> ent, ref DroneConsoleSpawnMessage args)
    {
        if (!IsConsolePowered(ent.Owner))
            return;

        if (!ent.Comp.SpawnableDrones.Contains(args.VesselId))
            return;

        // Drop stale in-flight spawns so a drone that never docked doesn't block production forever.
        var now = _timing.CurTime;
        ent.Comp.PendingSpawns.RemoveAll(p => (now - p.Time).TotalSeconds > PendingSpawnTtl);

        // Produced + still-arriving must stay under the limit; only a repair station restock refills it.
        if (GetHangarCount(ent.Comp) <= 0)
        {
            _popup.PopupEntity(Loc.GetString("drone-carrier-hangar-empty"), ent.Owner, PopupType.MediumCaution);
            return;
        }

        if (_station.GetOwningStation(ent.Owner) is not { } station)
        {
            _popup.PopupEntity(Loc.GetString("drone-carrier-deploy-nogrid"), ent.Owner, PopupType.Medium);
            return;
        }

        if (!_proto.TryIndex<VesselPrototype>(args.VesselId, out var vessel))
        {
            _popup.PopupEntity(Loc.GetString("drone-carrier-unknown-vessel", ("vessel", args.VesselId)), ent.Owner, PopupType.MediumCaution);
            return;
        }

        // Bill the faction that owns the carrier, exactly as a faction shipyard would. Checked before the
        // spawn so a failed spawn never costs anyone anything.
        var price = GetDronePrice(ent.Owner, ent.Comp, vessel);
        if (price > 0 && _market.GetTreasury(station) < price)
        {
            _popup.PopupEntity(Loc.GetString("drone-carrier-treasury-insufficient", ("cost", price)), ent.Owner, PopupType.MediumCaution);
            return;
        }

        // The shipyard spawns the drone and docks it to the station; the deploy scan then claims it into a
        // formation slot (which is where ProducedCount is incremented).
        if (!_shipyard.TryPurchaseShuttle(station, vessel.Path.ToString(), out var shuttle, out _))
        {
            _popup.PopupEntity(Loc.GetString("drone-carrier-spawn-failed"), ent.Owner, PopupType.MediumCaution);
            return;
        }

        // Protect the hull immediately, including while FTL-docking or waiting for a deployment slot.
        EnsureComp<DroneSpawnProtectionComponent>(shuttle.Owner).ExpiresAt =
            _timing.CurTime + ent.Comp.SpawnCollisionProtectionDuration;

        if (price > 0)
            _market.TryWithdrawTreasury(station, price);

        // TryPurchaseShuttle leaves the grid as an anonymous "grid"; give it a proper name so it shows on radar.
        _metaData.SetEntityName(shuttle.Owner, $"{vessel.Name} {_random.Next(100, 1000)}");

        // Bind it to THIS console immediately (same server tick, before any other console's scan runs) so a
        // nearby same-faction carrier can never claim it. Faction/color/undock are handled by DeployDrone.
        var carrierGrid = Transform(ent.Owner).GridUid;
        var bound = carrierGrid != null
            && _gridQuery.TryComp(carrierGrid.Value, out var carrierGridComp)
            && TryBindDroneGrid(ent, carrierGrid.Value, carrierGridComp, shuttle.Owner);

        // Fallback: a dock scan will claim it. Recording the grid keeps that claim attributable to us, so it
        // is treated as our own production rather than as a borrowed hull.
        if (!bound)
            ent.Comp.PendingSpawns.Add((now, shuttle.Owner));

        _popup.PopupEntity(
            price > 0
                ? Loc.GetString("drone-carrier-spawned-cost", ("cost", price))
                : Loc.GetString("drone-carrier-spawned"),
            ent.Owner,
            PopupType.Medium);
    }

    /// <summary>
    ///     What producing <paramref name="vessel"/> bills the treasury, or 0 when production is free: either
    ///     billing is switched off on the console, or the carrier belongs to no faction whose vault we could
    ///     charge. An unaligned carrier builds for free rather than being unable to build at all.
    /// </summary>
    public int GetDronePrice(EntityUid console, DroneCarrierComponent carrier, VesselPrototype vessel)
    {
        if (!carrier.ChargeTreasury)
            return 0;

        if (_station.GetOwningStation(console) is not { } station || _market.GetStationFaction(station) == null)
            return 0;

        return Math.Max(0, (int) (_economyPrice.GetVesselPrice(vessel) * carrier.PriceMultiplier));
    }

    private void ForceDeploy(Entity<DroneCarrierComponent> ent)
    {
        var carrierGrid = Transform(ent.Owner).GridUid;
        if (carrierGrid == null)
        {
            _popup.PopupEntity(Loc.GetString("drone-carrier-deploy-nogrid"), ent.Owner, PopupType.Medium);
            return;
        }

        var before = ent.Comp.Slots.Count;
        TryDeploy(ent, carrierGrid.Value);
        var deployed = ent.Comp.Slots.Count - before;

        _popup.PopupEntity(Loc.GetString("drone-carrier-deployed", ("count", deployed)), ent.Owner, PopupType.Medium);
    }

    private void SetStance(Entity<DroneCarrierComponent> ent, DroneStance stance)
    {
        ent.Comp.Stance = stance;

        if (stance == DroneStance.Follow)
            ent.Comp.FocusTarget = null;

        _popup.PopupEntity(Loc.GetString("drone-stance-set",
            ("stance", Loc.GetString($"drone-stance-{stance.ToString().ToLowerInvariant()}"))),
            ent.Owner, PopupType.Medium);
    }

    private void CycleFormation(Entity<DroneCarrierComponent> ent)
    {
        var values = Enum.GetValues<DroneFormation>();
        var next = (Array.IndexOf(values, ent.Comp.Formation) + 1) % values.Length;
        ent.Comp.Formation = values[next];

        RecomputeFormation(ent.Owner, ent.Comp);

        _popup.PopupEntity(Loc.GetString("drone-carrier-formation-set",
            ("formation", Loc.GetString($"drone-formation-{ent.Comp.Formation.ToString().ToLowerInvariant()}"))),
            ent.Owner, PopupType.Medium);
    }

    #endregion

    #region behavior

    private void DriveDrones(Entity<DroneCarrierComponent> carrier, TimeSpan now)
    {
        var focus = carrier.Comp.FocusTarget;
        var hasFocus = focus != null && !TerminatingOrDeleted(focus.Value);

        // Copy since undeploy mutates the dictionary.
        _slotScratch.Clear();
        _slotScratch.AddRange(carrier.Comp.Slots);

        foreach (var (slot, droneUid) in _slotScratch)
        {
            if (TerminatingOrDeleted(droneUid) || !_autoQuery.TryComp(droneUid, out var drone) || Transform(droneUid).GridUid == null)
            {
                UndeploySlot(carrier, slot);
                continue;
            }

            // A drone counting down to scuttle stops taking orders; it just coasts until it goes off.
            if (drone.SelfDestructAt != null)
            {
                StopDrone(droneUid);
                drone.Mode = AutoDroneMode.SelfDestructing;
                continue;
            }

            // Hull shot away: the drone is no use to anyone, write it off.
            if (drone.HullIntegrity < carrier.Comp.DisabledHullIntegrity)
            {
                WriteOffDrone(carrier, slot, (droneUid, drone));
                continue;
            }

            // Unpowered drones drift, and are written off if they stay dark.
            if (_powerQuery.TryComp(droneUid, out var receiver) && !_power.IsPowered(droneUid, receiver))
            {
                drone.UnpoweredSince ??= now;
                if (now - drone.UnpoweredSince.Value >= carrier.Comp.DisabledTimeout)
                {
                    WriteOffDrone(carrier, slot, (droneUid, drone));
                    continue;
                }

                StopDrone(droneUid);
                drone.Mode = AutoDroneMode.Idle;
                continue;
            }

            drone.UnpoweredSince = null;

            // A produced drone can finish its FTL hop still docked to the station; cast it off once.
            TryUndock((droneUid, drone));

            // A recent manual console order takes priority.
            if (now < drone.ManualOverrideUntil && drone.ManualCommand != null)
            {
                DriveManual(droneUid, drone, carrier.Comp);
                continue;
            }

            if (hasFocus)
                DriveAttack(droneUid, drone, focus!.Value, carrier.Comp);
            else
                DriveFollow(droneUid, drone, carrier.Comp);
        }
    }

    /// <summary>
    ///     Cuts every drone loose from a carrier that can no longer command them (an unpowered console).
    /// </summary>
    private void IdleDrones(Entity<DroneCarrierComponent> carrier)
    {
        carrier.Comp.FocusTarget = null;

        foreach (var droneUid in carrier.Comp.Slots.Values)
        {
            if (TerminatingOrDeleted(droneUid) || !_autoQuery.TryComp(droneUid, out var drone))
                continue;

            // Still let an armed scuttle run - that is handled centrally and must not depend on the console.
            if (drone.SelfDestructAt != null)
                continue;

            // The power-loss clock only runs while the carrier is watching.
            drone.UnpoweredSince = null;
            StopDrone(droneUid);
            drone.Mode = AutoDroneMode.Idle;
        }
    }

    private void DriveFollow(EntityUid drone, AutoDroneComponent comp, DroneCarrierComponent carrier)
    {
        var carrierGrid = comp.SlotCoordinates.EntityId;

        // Straight after deployment we're still hugging the carrier, so fly out to open space first instead of
        // grinding along the hull. The target is empty space, so the carrier IS avoided.
        // This is latched: once clear, a drone never drops back into launching, so it can't ping-pong between
        // flying out and forming up when its slot happens to sit near the clearance boundary.
        if (!comp.Launched && !TerminatingOrDeleted(carrierGrid) && _gridQuery.TryComp(carrierGrid, out var carrierGridComp))
        {
            var carrierMap = _transform.GetMapCoordinates(carrierGrid);
            var droneMap = _transform.GetMapCoordinates(drone);

            if (carrierMap.MapId != MapId.Nullspace && carrierMap.MapId == droneMap.MapId)
            {
                var toDrone = droneMap.Position - carrierMap.Position;
                var dist = toDrone.Length();
                var clearRadius = GetHullRadius(carrierGridComp) + carrier.LaunchClearance;

                if (dist < clearRadius && Transform(carrierGrid).MapUid is { } mapUid)
                {
                    var awayDir = dist > 0.1f ? toDrone / dist : new Vector2(0f, -1f);
                    var awayPos = carrierMap.Position + awayDir * (clearRadius + carrier.LaunchClearance);

                    var launch = _steering.Steer(drone, new EntityCoordinates(mapUid, awayPos));
                    if (launch != null)
                    {
                        launch.Mode = ShipSteeringMode.GoToRange;
                        launch.Range = 5f;
                        launch.RangeTolerance = null;
                        launch.InRangeMaxSpeed = null; // clear the hull quickly
                        launch.MaxRotateRate = null;
                        launch.LeadingEnabled = false;
                        launch.AlwaysFaceTarget = false;
                        launch.AvoidCollisions = true;
                        launch.AvoidTargetGrid = false; // target is empty space
                        launch.TargetRotation = 0f;     // reset from attack
                    }

                    StopFiring(drone);
                    comp.Mode = AutoDroneMode.Launching;
                    return;
                }

                comp.Launched = true;
            }
        }

        // Clear of the hull: hold the formation slot, matching the carrier's velocity and routing around it.
        var steer = _steering.Steer(drone, comp.SlotCoordinates);
        if (steer == null)
            return;

        steer.Mode = ShipSteeringMode.GoToRange;
        steer.Range = carrier.FormationRange;
        steer.RangeTolerance = null;
        steer.InRangeMaxSpeed = 0.1f; // relative to the carrier, so drones lock velocity with it
        steer.MaxRotateRate = 0.02f; // must settle rotation before counting as arrived, so it stops spinning
        steer.NoFinish = false;       // allowed to settle into the slot (reset from attack)
        steer.LeadingEnabled = true; // match the carrier's velocity so we hold station in sync
        steer.AlwaysFaceTarget = false;
        steer.AvoidCollisions = true;
        steer.AvoidProjectiles = false;
        steer.AvoidTargetGrid = true; // route around the carrier instead of ramming through it
        steer.TargetRotation = 0f;    // reset from attack, or a broadside drone sits sideways in formation

        StopFiring(drone);
        comp.Mode = AutoDroneMode.Follow;
    }

    private void DriveAttack(EntityUid drone, AutoDroneComponent comp, EntityUid enemyGrid, DroneCarrierComponent carrier)
    {
        var enemyCoords = new EntityCoordinates(enemyGrid, Vector2.Zero);

        var steer = _steering.Steer(drone, enemyCoords);
        if (steer != null)
        {
            // Circle the target rather than parking in front of it: a drone holding a fixed standoff is a
            // stationary gun platform and trivial to hit back. Separation comes from the per-slot standoff
            // radius and from alternating which way each drone circles - not from varying the lead angle,
            // which is what actually drives the orbit and would stall a drone if it were ever zero.
            steer.Mode = comp.Slot % 2 == 0 ? ShipSteeringMode.Orbit : ShipSteeringMode.OrbitCW;
            steer.OrbitOffset = carrier.OrbitLeadAngle;
            steer.Range = carrier.OrbitRange + comp.Slot * carrier.OrbitSpread;
            steer.RangeTolerance = carrier.OrbitTolerance;
            steer.InRangeMaxSpeed = null; // orbit as fast as we can
            steer.MaxRotateRate = null;
            steer.NoFinish = true;          // never park - keep steering so projectile evasion runs
            steer.LeadingEnabled = true;
            steer.AlwaysFaceTarget = true;
            steer.AvoidCollisions = true;
            steer.AvoidProjectiles = true;  // dodge incoming shipgun rounds
            steer.AvoidTargetGrid = false;

            // Hold whatever bearing actually lets this hull shoot. A drone with forward-firing guns gets 0
            // here and orbits nose-on exactly as before; only a broadside-armed one starts flying sideways.
            if (Transform(drone).GridUid is { } droneGrid)
                steer.TargetRotation = _arc.GetFiringOffset(droneGrid);
        }

        comp.Mode = AutoDroneMode.Attack;

        // Hold fire if a friendly ship is in the line of fire toward the target, so we never clip an ally.
        var faction = GetGridFaction(Transform(drone).GridUid ?? drone);
        if (FriendlyInLineOfFire(drone, enemyGrid, faction))
            StopFiring(drone);
        else
            Fire(drone, enemyCoords);
    }

    /// <summary>
    ///     Points the drone's guns at a target, matching the leading accuracy the hand-written HTN drone task
    ///     uses so autopiloted drones aren't strictly better shots than every other AI ship.
    /// </summary>
    private void Fire(EntityUid drone, EntityCoordinates target)
    {
        var targeting = _targeting.Target(drone, target);
        if (targeting != null)
            targeting.LeadingAccuracy = DroneLeadingAccuracy;
    }

    /// <summary>
    ///     True if a grid belonging to a faction we are not at war with sits within
    ///     <see cref="FriendlyHoldFireRange"/> of the drone and roughly between it and the target - i.e. in
    ///     the line of fire. Only grids with a real faction count: debris, asteroids and other unaligned hulls
    ///     have no IFF faction, and treating those as friendlies would mute the squadron in any wreck field.
    /// </summary>
    private bool FriendlyInLineOfFire(EntityUid drone, EntityUid enemyGrid, string? faction)
    {
        var droneGrid = Transform(drone).GridUid;
        if (droneGrid == null)
            return false;

        var dronePos = _transform.GetMapCoordinates(droneGrid.Value);
        if (dronePos.MapId == MapId.Nullspace)
            return false;

        var toEnemy = _transform.GetMapCoordinates(enemyGrid).Position - dronePos.Position;
        if (toEnemy.LengthSquared() < 0.01f)
            return false;
        toEnemy = toEnemy.Normalized();

        var bounds = Box2.CenteredAround(dronePos.Position, new Vector2(FriendlyHoldFireRange * 2f, FriendlyHoldFireRange * 2f));
        _gridScratch.Clear();
        _map.FindGridsIntersecting(dronePos.MapId, bounds, ref _gridScratch, approx: true, includeMap: false);

        foreach (var grid in _gridScratch)
        {
            if (grid.Owner == droneGrid.Value || grid.Owner == enemyGrid)
                continue;

            // Unaligned hulls (debris, asteroids, wrecks) are not anyone's friendly - shoot through them.
            if (GetGridFaction(grid.Owner) is not { } gridFaction)
                continue;

            var toGrid = _transform.GetMapCoordinates(grid.Owner).Position - dronePos.Position;
            var distSq = toGrid.LengthSquared();
            if (distSq > FriendlyHoldFireRange * FriendlyHoldFireRange || distSq < 0.01f)
                continue;

            // In front, within ~30 degrees of the firing line?
            if (Vector2.Dot(toGrid.Normalized(), toEnemy) < 0.86f)
                continue;

            if (faction == null || _diplomacy.GetRelations(faction, gridFaction) != Relations.War)
                return true; // a friendly/neutral grid is in the line of fire
        }

        return false;
    }

    private void DriveManual(EntityUid drone, AutoDroneComponent comp, DroneCarrierComponent carrier)
    {
        var target = comp.ManualTarget;

        if (comp.ManualCommand == DroneConsoleConstants.CommandTarget)
        {
            var steer = _steering.Steer(drone, target);
            if (steer != null)
            {
                // Hold at standoff facing the target, don't orbit/spin.
                steer.Mode = ShipSteeringMode.GoToRange;
                steer.Range = carrier.OrbitRange;
                steer.RangeTolerance = carrier.OrbitTolerance;
                steer.InRangeMaxSpeed = 0.1f;
                steer.MaxRotateRate = 0.02f;
                steer.NoFinish = false;
                steer.LeadingEnabled = true;
                steer.AlwaysFaceTarget = true;
                steer.AvoidCollisions = true;
                steer.AvoidTargetGrid = false;

                // A manual fire order is still an attack, so bring the guns to bear the same way.
                if (Transform(drone).GridUid is { } manualGrid)
                    steer.TargetRotation = _arc.GetFiringOffset(manualGrid);
            }

            Fire(drone, target);
        }
        else // move
        {
            var steer = _steering.Steer(drone, target);
            if (steer != null)
            {
                steer.Mode = ShipSteeringMode.GoToRange;
                steer.Range = 15f;
                steer.RangeTolerance = null;
                steer.InRangeMaxSpeed = 0.1f;
                steer.MaxRotateRate = 0.02f;
                steer.NoFinish = false;
                steer.LeadingEnabled = false;
                steer.AlwaysFaceTarget = true;
                steer.AvoidCollisions = true;
                steer.AvoidTargetGrid = false;
                steer.TargetRotation = 0f; // a move order is not a firing pass
            }

            StopFiring(drone);
        }

        comp.Mode = AutoDroneMode.Manual;
    }

    #endregion

    #region targeting

    private void ScanEnemies(EntityUid carrierGrid, string? faction, float range, DroneTargeting targeting)
    {
        _enemies.Clear();
        _enemyDrones.Clear();

        var carrierPos = _transform.GetMapCoordinates(carrierGrid);
        if (carrierPos.MapId == MapId.Nullspace)
            return;

        foreach (var (target, targetComp) in _lookup.GetEntitiesInRange<ShipNpcTargetComponent>(carrierPos, range))
        {
            var targetGrid = Transform(target).GridUid;
            if (targetComp.NeedGrid && targetGrid == null)
                continue;
            if (targetGrid == null || targetGrid == carrierGrid)
                continue;
            // A ship that has lost power counts as neutralized - stop wasting fire on it.
            if (targetComp.NeedPower && _powerQuery.TryComp(target, out var receiver) && !_power.IsPowered(target, receiver))
                continue;

            // Never open fire on a hull with no diplomatic faction. That covers derelicts, unaligned civilian
            // ships, stations we happen to be parked at, and - critically - our own freshly produced drones,
            // which are unaligned for the moment between spawning and being stamped with our IFF.
            if (GetGridFaction(targetGrid.Value) is not { } targetFaction)
                continue;

            var relation = faction == null ? Relations.Neutral : _diplomacy.GetRelations(faction, targetFaction);
            // Enemies mode: only War. All mode: every faction except our own and our allies.
            var hostile = targeting == DroneTargeting.All ? relation != Relations.Ally : relation == Relations.War;
            if (!hostile)
                continue;

            _enemies.Add(targetGrid.Value);

            // An enemy whose targetable point is a drone control server is an enemy drone; we prioritise
            // killing those first (destroying the server disables the drone).
            if (_droneServerQuery.HasComp(target))
                _enemyDrones.Add(targetGrid.Value);
        }
    }

    /// <summary>
    ///     Picks the shared focus target with focus-fire + priority: enemy drones are hunted first (kill their
    ///     server), and only once none remain do the drones turn on other ships. The current target is kept
    ///     while it is still valid at the current priority tier.
    /// </summary>
    private void SelectFocus(DroneCarrierComponent carrier, EntityUid carrierGrid)
    {
        // Enemy drones present: focus one of them (destroy the servers first).
        if (_enemyDrones.Count > 0)
        {
            if (carrier.FocusTarget is { } droneFocus && !TerminatingOrDeleted(droneFocus) && _enemyDrones.Contains(droneFocus))
                return;

            carrier.FocusTarget = Nearest(_enemyDrones, carrierGrid);
            return;
        }

        // No enemy drones left: focus any enemy ship.
        if (_enemies.Count == 0)
        {
            carrier.FocusTarget = null;
            return;
        }

        if (carrier.FocusTarget is { } current && !TerminatingOrDeleted(current) && _enemies.Contains(current))
            return;

        carrier.FocusTarget = Nearest(_enemies, carrierGrid);
    }

    private EntityUid? Nearest(HashSet<EntityUid> grids, EntityUid fromGrid)
    {
        var fromPos = _transform.GetMapCoordinates(fromGrid).Position;

        EntityUid? best = null;
        var bestDist = float.MaxValue;
        foreach (var grid in grids)
        {
            if (TerminatingOrDeleted(grid))
                continue;

            var dist = (_transform.GetMapCoordinates(grid).Position - fromPos).LengthSquared();
            if (dist < bestDist)
            {
                bestDist = dist;
                best = grid;
            }
        }

        return best;
    }

    #endregion

    #region self destruct

    /// <summary>
    ///     Runs every armed scuttle countdown, and clears up the wrecks afterwards. Deliberately independent
    ///     of the carrier loop so an orphaned drone - whose console is gone, which is exactly when the
    ///     automatic countdown gets armed - still goes off.
    /// </summary>
    private void UpdateSelfDestruct(TimeSpan now)
    {
        _scuttleScratch.Clear();

        var query = EntityQueryEnumerator<AutoDroneComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.SelfDestructAt is { } at && now >= at)
                _scuttleScratch.Add((uid, comp));
        }

        // Deferred: scuttling touches grids, which mutates the entity set we just walked.
        foreach (var drone in _scuttleScratch)
        {
            Scuttle(drone, now);
        }

        for (var i = _scuttleCleanup.Count - 1; i >= 0; i--)
        {
            var (at, grid) = _scuttleCleanup[i];
            if (now < at)
                continue;

            _scuttleCleanup.RemoveAt(i);

            // Never delete a hull somebody boarded in the meantime - leave them the wreck.
            if (Exists(grid) && !TerminatingOrDeleted(grid) && !HasOccupants(grid))
                QueueDel(grid);
        }
    }

    /// <summary>
    ///     Blows the drone up, then clears the wreck a moment later so a squadron whose carrier died doesn't
    ///     leave a field of unrecoverable derelicts behind. The removal is tracked here rather than on the
    ///     drone because the blast usually destroys the control server itself.
    /// </summary>
    private void Scuttle(Entity<AutoDroneComponent> drone, TimeSpan now)
    {
        drone.Comp.SelfDestructAt = null;

        if (TerminatingOrDeleted(drone.Owner))
            return;

        // Release the slot first: the carrier shouldn't keep steering a hull that's about to stop existing.
        if (drone.Comp.CarrierConsole is { } console
            && TryComp<DroneCarrierComponent>(console, out var carrier)
            && drone.Comp.Slot >= 0
            && carrier.Slots.TryGetValue(drone.Comp.Slot, out var occupant)
            && occupant == drone.Owner)
        {
            carrier.Slots.Remove(drone.Comp.Slot);
        }

        StopDrone(drone.Owner);
        drone.Comp.CarrierConsole = null;
        drone.Comp.Slot = -1;
        drone.Comp.Mode = AutoDroneMode.Idle;

        var gridUid = Transform(drone.Owner).GridUid;

        // Resolves the epicentre immediately and queues a map-anchored blast, so it still goes off even
        // though the hull under it is on its way out.
        _explosion.QueueExplosion(
            drone.Owner,
            drone.Comp.SelfDestructExplosion,
            drone.Comp.SelfDestructIntensity,
            drone.Comp.SelfDestructSlope,
            drone.Comp.SelfDestructMaxTileIntensity);

        if (gridUid != null)
            _scuttleCleanup.Add((now + drone.Comp.ScuttleCleanupDelay, gridUid.Value));
    }

    /// <summary>
    ///     True if anything with a mind is aboard the grid. Used to make sure scuttling never deletes a hull
    ///     out from under a player who boarded it.
    /// </summary>
    private bool HasOccupants(EntityUid gridUid)
    {
        var query = EntityQueryEnumerator<MindContainerComponent, TransformComponent>();
        while (query.MoveNext(out _, out var mind, out var xform))
        {
            if (xform.GridUid == gridUid && mind.HasMind)
                return true;
        }

        return false;
    }

    #endregion

    #region cleanup

    private void OnDroneShutdown(Entity<AutoDroneComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.CarrierConsole is not { } console || !TryComp<DroneCarrierComponent>(console, out var carrier))
            return;

        if (ent.Comp.Slot >= 0 && carrier.Slots.TryGetValue(ent.Comp.Slot, out var occupant) && occupant == ent.Owner)
            carrier.Slots.Remove(ent.Comp.Slot);
    }

    private void OnCarrierShutdown(Entity<DroneCarrierComponent> ent, ref ComponentShutdown args)
    {
        var now = _timing.CurTime;

        foreach (var drone in ent.Comp.Slots.Values)
        {
            if (!_autoQuery.TryComp(drone, out var comp))
                continue;

            comp.CarrierConsole = null;
            comp.Slot = -1;
            comp.Mode = AutoDroneMode.Idle;

            // Nothing can command these any more, and they can't dock or be recovered, so arm a scuttle rather
            // than leaving a squadron of derelicts drifting on the map for the rest of the round.
            if (ent.Comp.SelfDestructOnOrphan && comp.SelfDestructAt == null && !TerminatingOrDeleted(drone))
                comp.SelfDestructAt = now + comp.OrphanSelfDestructDelay;

            if (!TerminatingOrDeleted(drone))
                StopDrone(drone);
        }

        ent.Comp.Slots.Clear();
    }

    private void UndeploySlot(Entity<DroneCarrierComponent> carrier, int slot)
    {
        if (!carrier.Comp.Slots.Remove(slot, out var drone))
            return;

        if (!_autoQuery.TryComp(drone, out var comp))
            return;

        comp.CarrierConsole = null;
        comp.Slot = -1;
        comp.Mode = AutoDroneMode.Idle;

        if (!TerminatingOrDeleted(drone))
            StopDrone(drone);
    }

    private void StopDrone(EntityUid drone)
    {
        if (_steererQuery.HasComp(drone))
            _steering.Stop(drone);
        StopFiring(drone);
    }

    private void StopFiring(EntityUid drone)
    {
        if (_targetingQuery.HasComp(drone))
            _targeting.Stop(drone);
    }

    #endregion
}
