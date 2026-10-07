using Content.Server.DeviceNetwork;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.NPC.HTN;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Crescent.DroneControl;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Systems;
using Content.Shared.Popups;
using Content.Shared.Shipyard.Prototypes;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using StationTradeMarketSystem = Content.Server.Crescent.Dispenser.StationTradeMarketSystem;

namespace Content.Server._Crescent.DroneControl;

public sealed class DroneControlSystem : EntitySystem
{
    [Dependency] private readonly AutoDroneSystem _autoDrone = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedShuttleSystem _shuttles = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StationTradeMarketSystem _market = default!;

    private EntityQuery<ApcPowerReceiverComponent> _powerQuery;

    /// <summary>
    ///     How often an open console's UI is refreshed. Building the state sweeps every docking port in the
    ///     world, so it must not run per tick.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(0.5);

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        // Manual autolink is intentionally disabled: a carrier only fields the drones it produces, so players
        // can't wire extra drones in with a multitool. Deployment/linking is handled by AutoDroneSystem.

        SubscribeLocalEvent<DroneControlConsoleComponent, DroneConsoleMoveMessage>(OnMoveMsg);
        SubscribeLocalEvent<DroneControlConsoleComponent, DroneConsoleTargetMessage>(OnTargetMsg);

        SubscribeLocalEvent<DroneControlComponent, DeviceNetworkPacketEvent>(OnPacketReceived);

        _powerQuery = GetEntityQuery<ApcPowerReceiverComponent>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshInterval;

        var query = EntityQueryEnumerator<DroneControlConsoleComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_ui.IsUiOpen(uid, DroneConsoleUiKey.Key))
                UpdateState(uid);
        }
    }

    private void OnMoveMsg(Entity<DroneControlConsoleComponent> ent, ref DroneConsoleMoveMessage args)
    {
        DoTargetedDroneOrder(ent, args.SelectedDrones, DroneOrderType.Move, GetCoordinates(args.TargetCoordinates));
    }

    private void OnTargetMsg(Entity<DroneControlConsoleComponent> ent, ref DroneConsoleTargetMessage args)
    {
        DoTargetedDroneOrder(ent, args.SelectedDrones, DroneOrderType.Target, GetCoordinates(args.TargetCoordinates));
    }

    private void OnPacketReceived(Entity<DroneControlComponent> ent, ref DeviceNetworkPacketEvent args)
    {
        if (!args.Data.TryGetValue(DeviceNetworkConstants.Command, out string? cmd)
            || !args.Data.TryGetValue(DroneConsoleConstants.TargetCoords, out EntityCoordinates coords)
        )
            return;

        // A drone claimed by a carrier is driven by AutoDroneSystem and takes its orders straight from that
        // console (see DoTargetedDroneOrder), so a stray broadcast on this frequency must not be able to
        // redirect somebody else's squadron. Only unclaimed drones answer the network.
        if (TryComp<AutoDroneComponent>(ent, out var autoDrone) && autoDrone.CarrierConsole != null)
            return;

        if (!TryComp<HTNComponent>(ent, out var htn))
            return;

        var blackboard = htn.Blackboard;

        if (!blackboard.TryGetValue<string>(ent.Comp.OrderKey, out var nowCmd, EntityManager) || !nowCmd.Equals(cmd))
            _htn.ShutdownPlan(htn);

        blackboard.SetValue(ent.Comp.OrderKey, cmd);
        blackboard.SetValue(ent.Comp.TargetKey, coords);
    }

    private void DoTargetedDroneOrder(Entity<DroneControlConsoleComponent> console, HashSet<NetEntity> selected, DroneOrderType order, EntityCoordinates coordinates)
    {
        // An unpowered console issues no orders, matching how its drones stop being driven at all.
        if (_powerQuery.TryComp(console, out var receiver) && !_power.IsPowered(console, receiver))
            return;

        if (!coordinates.TryDistance(EntityManager, Transform(console).Coordinates, out var distance))
            return;

        if (distance > (console.Comp.MaxOrderRadius ?? float.MaxValue))
        {
            _popup.PopupEntity(Loc.GetString("drone-control-out-of-range"), console, PopupType.Medium);
            return;
        }

        if (!TryComp<DroneCarrierComponent>(console, out var carrier))
            return;

        var command = order == DroneOrderType.Move ? DroneConsoleConstants.CommandMove : DroneConsoleConstants.CommandTarget;

        // Set the manual override directly on the selected claimed drones. This works on ANY clicked grid,
        // including a friendly one (in case that ship has been captured by the enemy).
        foreach (var drone in carrier.Slots.Values)
        {
            if (!selected.Contains(GetNetEntity(drone)) || !TryComp<AutoDroneComponent>(drone, out var ad))
                continue;

            // A drone counting down to scuttle isn't taking orders any more.
            if (ad.SelfDestructAt != null)
                continue;

            ad.ManualCommand = command;
            ad.ManualTarget = coordinates;
            ad.ManualOverrideUntil = _timing.CurTime + ad.ManualOverrideTimeout;
        }
    }

    private void UpdateState(EntityUid console)
    {
        var nav = _shuttleConsole.GetNavState(console, _shuttleConsole.GetAllDocks());
        var iffState = _shuttleConsole.GetIFFState(console, null);

        // The carrier's own slot roster is authoritative - it always matches the drones it commands.
        var drones = new List<DroneStatusEntry>();
        var isCarrier = TryComp<DroneCarrierComponent>(console, out var carrier);
        var now = _timing.CurTime;

        if (carrier != null)
        {
            foreach (var drone in carrier.Slots.Values)
            {
                if (TerminatingOrDeleted(drone) || !TryComp<AutoDroneComponent>(drone, out var auto))
                    continue;

                var xform = Transform(drone);
                if (xform.GridUid == null)
                    continue;

                float? selfDestructIn = auto.SelfDestructAt is { } at
                    ? MathF.Max(0f, (float) (at - now).TotalSeconds)
                    : null;

                drones.Add(new DroneStatusEntry
                {
                    Server = GetNetEntity(drone),
                    Grid = GetNetEntity(xform.GridUid.Value),
                    Name = _shuttles.GetIFFLabel(xform.GridUid.Value, self: false) ?? Name(xform.GridUid.Value),
                    Mode = auto.Mode,
                    HullIntegrity = auto.HullIntegrity,
                    Powered = !_powerQuery.TryComp(drone, out var droneReceiver) || _power.IsPowered(drone, droneReceiver),
                    SelfDestructIn = selfDestructIn,
                });
            }
        }

        _ui.SetUiState(console, DroneConsoleUiKey.Key, new DroneConsoleBoundUserInterfaceState(
            nav, iffState, drones,
            isCarrier,
            carrier?.Stance ?? DroneStance.Attack,
            carrier?.Targeting ?? DroneTargeting.Enemies,
            carrier?.Formation ?? DroneFormation.Arrow,
            carrier?.ProducedCount ?? 0,
            carrier != null ? _autoDrone.GetDeployedSpace(carrier) : 0,
            carrier?.MaxDrones ?? 0,
            carrier != null ? _autoDrone.GetLostDrones(carrier) : 0,
            carrier != null ? _autoDrone.GetHangarCount(carrier) : 0,
            BuildSpawnList(console, carrier),
            GetTreasury(console, carrier)));
    }

    /// <summary>
    ///     The vessels this console can produce, each with the price it would bill.
    /// </summary>
    private List<DroneSpawnEntry> BuildSpawnList(EntityUid console, DroneCarrierComponent? carrier)
    {
        var list = new List<DroneSpawnEntry>();
        if (carrier == null)
            return list;

        foreach (var vesselId in carrier.SpawnableDrones)
        {
            if (!_proto.TryIndex<VesselPrototype>(vesselId, out var vessel))
                continue;

            list.Add(new DroneSpawnEntry
            {
                VesselId = vesselId,
                Name = vessel.Name,
                Price = _autoDrone.GetDronePrice(console, carrier, vessel),
                HangarCost = _autoDrone.GetHangarCost(carrier, vesselId),
            });
        }

        return list;
    }

    /// <summary>
    ///     Funds available to this console, or null when production is free and no balance should be shown.
    /// </summary>
    private int? GetTreasury(EntityUid console, DroneCarrierComponent? carrier)
    {
        if (carrier is not { ChargeTreasury: true })
            return null;

        // No faction vault to draw on means production is free, so show nothing rather than a bogus 0.
        if (_station.GetOwningStation(console) is not { } station || _market.GetStationFaction(station) == null)
            return null;

        return _market.GetTreasury(station);
    }
}
