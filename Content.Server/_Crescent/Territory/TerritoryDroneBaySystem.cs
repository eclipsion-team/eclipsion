using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.ShipAI;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent;
using Content.Shared._Crescent.Economy;
using Content.Shared._Crescent.Territory;
using Content.Shared.GameTicking;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryDroneBaySystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly DiplomacySystem _diplomacy = default!;
    [Dependency] private readonly ShipHullMonitorSystem _hulls = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly TerritoryDronePicketSystem _pickets = default!;
    [Dependency] private readonly TerritoryPaymentSystem _payment = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly TerritoryConsoleSystem _consoles = default!;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<TerritoryDroneBayConsoleComponent>(TerritoryDroneBayUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<TerritoryDroneBayLaunchMessage>(OnLaunch);
            subs.Event<TerritoryDroneBayCommandMessage>(OnCommand);
        });
    }

    private void OnCommand(Entity<TerritoryDroneBayConsoleComponent> ent, ref TerritoryDroneBayCommandMessage args)
    {
        var drone = GetEntity(args.Drone);
        var target = GetEntity(args.Target);
        if (!TryCommand(ent, args.Actor, drone, args.Command, target, out var error))
            _popup.PopupEntity(Loc.GetString(error), ent, args.Actor, PopupType.SmallCaution);

        UpdateConsole(ent);
    }

    public bool TryCommand(
        Entity<TerritoryDroneBayConsoleComponent> console,
        EntityUid actor,
        EntityUid? drone,
        TerritoryDroneCommand command,
        EntityUid? target,
        [NotNullWhen(false)] out string? error)
    {
        if (Transform(console).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner is not { } owner ||
            !PersistentTerritoryFactions.Holds(EntityManager, actor, territory) ||
            !TryGetPickets(grid, out var standard))
        {
            error = "territory-drone-bay-not-holder-command";
            return false;
        }

        if (!_consoles.HasCommandRank(owner, actor))
        {
            error = "territory-drone-bay-no-rank";
            return false;
        }

        if (!this.IsPowered(console, EntityManager))
        {
            error = "territory-drone-bay-unpowered";
            return false;
        }

        if (_pickets.FliesForSomeoneElse(standard.Value.Comp, owner))
        {
            error = "territory-drone-bay-old-pickets-command";
            return false;
        }

        var drones = new List<EntityUid>();
        if (drone is { } one)
        {
            if (!_pickets.InPicket(standard.Value.Comp, one))
            {
                error = "territory-drone-bay-lost-drone";
                return false;
            }

            drones.Add(one);
        }
        else
        {
            drones.AddRange(standard.Value.Comp.Pickets);
        }

        if (drones.Count == 0)
        {
            error = "territory-drone-bay-no-drones";
            return false;
        }

        switch (command)
        {
            case TerritoryDroneCommand.Attack:
                if (target is not { } victim ||
                    !_pickets.IsValidAttackTarget(standard.Value.Comp, _transform.GetMapCoordinates(standard.Value),
                        grid, victim))
                {
                    error = "territory-drone-bay-bad-target";
                    return false;
                }

                foreach (var uid in drones)
                {
                    _pickets.SetOrder(uid, TerritoryDroneOrder.Attack, victim);
                }

                break;
            case TerritoryDroneCommand.Defend:
                foreach (var uid in drones)
                {
                    _pickets.SetOrder(uid, TerritoryDroneOrder.Defend);
                }

                break;
            case TerritoryDroneCommand.Hold:
                foreach (var uid in drones)
                {
                    _pickets.SetOrder(uid, TerritoryDroneOrder.Hold);
                }

                break;
            case TerritoryDroneCommand.PowerDown:
                foreach (var uid in drones)
                {
                    _pickets.SetOrder(uid, TerritoryDroneOrder.Offline);
                }

                break;
            case TerritoryDroneCommand.PowerUp:
                foreach (var uid in drones)
                {
                    if (TryComp<TerritoryPicketDroneComponent>(uid, out var picket) && picket.Order == TerritoryDroneOrder.Offline)
                        _pickets.SetOrder(uid, TerritoryDroneOrder.Defend);
                }

                break;
            case TerritoryDroneCommand.Scuttle:
                // only one drone at a time
                if (drone == null)
                {
                    error = "territory-drone-bay-lost-drone";
                    return false;
                }

                // the hull gets deleted, whoever stands on it would go with it
                if (_pickets.HasCrew(drone.Value))
                {
                    error = "territory-drone-bay-scuttle-crewed";
                    return false;
                }

                _pickets.Scuttle(drone.Value);
                break;
        }

        error = null;
        return true;
    }

    private void OnOpened(Entity<TerritoryDroneBayConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateConsole(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshInterval;

        // drones get shot down without telling the console
        var consoles = EntityQueryEnumerator<TerritoryDroneBayConsoleComponent>();
        while (consoles.MoveNext(out var uid, out var console))
        {
            if (_ui.IsUiOpen(uid, TerritoryDroneBayUiKey.Key))
                UpdateConsole((uid, console));
        }
    }

    private void OnLaunch(Entity<TerritoryDroneBayConsoleComponent> ent, ref TerritoryDroneBayLaunchMessage args)
    {
        if (!TryLaunch(ent, args.Actor, args.Hull, args.Source, out var drone, out var price, out var error))
        {
            _popup.PopupEntity(Loc.GetString(error), ent, args.Actor, PopupType.SmallCaution);
            UpdateConsole(ent);
            return;
        }

        _popup.PopupEntity(Loc.GetString("territory-drone-bay-launched",
            ("drone", Name(Transform(drone.Value).GridUid ?? drone.Value)),
            ("price", price)), ent, args.Actor);
        UpdateConsole(ent);
    }

    public bool TryLaunch(
        Entity<TerritoryDroneBayConsoleComponent> console,
        EntityUid actor,
        string hull,
        PaymentSource source,
        [NotNullWhen(true)] out EntityUid? drone,
        out int price,
        [NotNullWhen(false)] out string? error)
    {
        drone = null;
        price = 0;

        if (_ticker.RunLevel == GameRunLevel.PostRound)
        {
            error = "territory-recruit-round-over";
            return false;
        }

        if (Transform(console).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner is not { } owner ||
            !PersistentTerritoryFactions.Holds(EntityManager, actor, territory) ||
            !TryGetPickets(grid, out var standard))
        {
            error = "territory-drone-bay-not-holder";
            return false;
        }

        if (string.IsNullOrEmpty(hull) ||
            !console.Comp.Hulls.TryGetValue(hull, out price) ||
            !_proto.HasIndex<GameMapPrototype>(hull))
        {
            error = "territory-drone-bay-unknown-hull";
            return false;
        }

        if (!this.IsPowered(console, EntityManager))
        {
            error = "territory-drone-bay-unpowered";
            return false;
        }

        if (_pickets.FliesForSomeoneElse(standard.Value.Comp, owner))
        {
            error = "territory-drone-bay-old-pickets";
            return false;
        }

        if (Room(territory, standard.Value.Comp) <= 0)
        {
            error = "territory-drone-bay-full";
            return false;
        }

        if (!_payment.TryCharge(actor, owner, source, price, console.Comp.TreasuryShare,
                $"{hull} drone on territory '{territory.RegionId}'", out error))
        {
            return false;
        }

        if (!_pickets.TryLaunchBought(standard.Value, hull, owner, out drone))
        {
            // the hull never made it up, so the money goes back to whatever paid
            _payment.Refund(actor, owner, source, price);
            error = "territory-drone-bay-launch-failed";
            return false;
        }

        return true;
    }

    private int Room(PersistentPoiGridComponent territory, TerritoryDronePicketsComponent pickets)
    {
        var limit = _levels.GetLevel(territory.Level)?.BayDrones ?? 0;
        return Math.Max(0, limit - _pickets.CountBought(pickets));
    }

    private bool TryGetPickets(EntityUid grid, [NotNullWhen(true)] out Entity<TerritoryDronePicketsComponent>? standard)
    {
        var query = EntityQueryEnumerator<TerritoryDronePicketsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var pickets, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            standard = (uid, pickets);
            return true;
        }

        standard = null;
        return false;
    }

    private void UpdateConsole(Entity<TerritoryDroneBayConsoleComponent> ent)
    {
        foreach (var viewer in _ui.GetActors(ent.Owner, TerritoryDroneBayUiKey.Key).ToArray())
        {
            _ui.ServerSendUiMessage(ent.Owner, TerritoryDroneBayUiKey.Key, new ViewerUiStateMessage(BuildState(ent, viewer)), viewer);
        }
    }

    public TerritoryDroneBayState BuildState(Entity<TerritoryDroneBayConsoleComponent> console, EntityUid? viewer)
    {
        var state = new TerritoryDroneBayState
        {
            Powered = this.IsPowered(console, EntityManager),
            AttackRange = (int) TerritoryDronePicketsComponent.DefaultAttackRange,
        };

        foreach (var (hull, price) in console.Comp.Hulls)
        {
            if (!_proto.TryIndex(hull, out var gameMap))
                continue;

            // "DSM Valor" -> "Valor"
            var words = gameMap.MapName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            state.Hulls.Add(new TerritoryDroneBayHull
            {
                Id = hull,
                Name = words.Length > 1 ? string.Join(' ', words[1..]) : gameMap.MapName,
                Price = price,
            });
        }

        state.Hulls.Sort((a, b) => a.Price != b.Price ? a.Price.CompareTo(b.Price) : string.CompareOrdinal(a.Name, b.Name));

        if (Transform(console).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            return state;
        }

        state.Owner = territory.Owner;
        state.Level = territory.Level;
        state.Limit = _levels.GetLevel(territory.Level)?.BayDrones ?? 0;

        if (territory.Owner is not { } owner || !TryGetPickets(grid, out var standard))
            return state;

        var pickets = standard.Value.Comp;
        state.Bought = _pickets.CountBought(pickets);
        state.OldPickets = _pickets.FliesForSomeoneElse(pickets, owner);
        state.AttackRange = (int) pickets.AttackRange;

        if (viewer is { } player)
        {
            state.Payment = _payment.GetState(player, owner, console.Comp.TreasuryShare);
            var holds = PersistentTerritoryFactions.Holds(EntityManager, player, territory);
            var rank = holds && _consoles.HasCommandRank(owner, player);
            state.CanCommand = rank;
            state.NoRank = holds && !rank;
        }

        if (state.OldPickets)
            return state;

        var center = _transform.GetMapCoordinates(standard.Value);
        foreach (var drone in pickets.Pickets)
        {
            if (Describe(drone, pickets, center) is { } entry)
                state.Drones.Add(entry);
        }

        state.Drones.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (state.Drones.Count > 0)
        {
            foreach (var (target, distance) in _pickets.ListTargets(standard.Value))
            {
                state.Targets.Add(new TerritoryDroneTarget
                {
                    Target = GetNetEntity(target),
                    Name = Name(target),
                    Faction = _diplomacy.GetGridFaction(target),
                    Distance = (int) distance,
                });
            }
        }

        return state;
    }

    private TerritoryDroneEntry? Describe(EntityUid drone, TerritoryDronePicketsComponent pickets, MapCoordinates center)
    {
        if (!TryComp<TerritoryPicketDroneComponent>(drone, out var picket) || Transform(drone).GridUid is not { } droneGrid)
            return null;

        var entry = new TerritoryDroneEntry
        {
            Drone = GetNetEntity(drone),
            Name = Name(droneGrid),
            Bought = picket.Bought,
            Order = picket.Order,
            Returning = picket.Returning && picket.Order == TerritoryDroneOrder.Defend,
            Unpowered = HasComp<ApcPowerReceiverComponent>(drone) && !this.IsPowered(drone, EntityManager),
            Distance = (int) Vector2.Distance(_transform.GetWorldPosition(droneGrid), center.Position),
            Integrity = (int) MathF.Round(_hulls.GetIntegrity(drone) * 100f),
        };

        var target = picket.Order switch
        {
            TerritoryDroneOrder.Attack => picket.OrderTarget,
            TerritoryDroneOrder.Defend when !picket.Returning && !pickets.Dormant => pickets.Target,
            _ => null,
        };

        if (target is { } shot && !TerminatingOrDeleted(shot))
        {
            entry.Engaging = true;
            entry.Target = Name(shot);
        }

        return entry;
    }
}
