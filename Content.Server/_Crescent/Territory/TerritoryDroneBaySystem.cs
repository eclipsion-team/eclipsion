using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server.Popups;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Economy;
using Content.Shared._Crescent.Territory;
using Content.Shared.GameTicking;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// The level 3 drone bay: holders buy light drones that join the territory's picket and never leave it.
/// </summary>
public sealed class TerritoryDroneBaySystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly TerritoryDronePicketSystem _pickets = default!;
    [Dependency] private readonly TerritoryPaymentSystem _payment = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<TerritoryDroneBayConsoleComponent>(TerritoryDroneBayUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<TerritoryDroneBayLaunchMessage>(OnLaunch);
        });
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
        var viewer = _ui.GetActors(ent.Owner, TerritoryDroneBayUiKey.Key).FirstOrDefault();
        _ui.SetUiState(ent.Owner, TerritoryDroneBayUiKey.Key, BuildState(ent, viewer.IsValid() ? viewer : null));
    }

    public TerritoryDroneBayState BuildState(Entity<TerritoryDroneBayConsoleComponent> console, EntityUid? viewer)
    {
        var state = new TerritoryDroneBayState
        {
            Powered = this.IsPowered(console, EntityManager),
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

        state.Drones = _pickets.CountBought(standard.Value.Comp);
        state.OldPickets = _pickets.FliesForSomeoneElse(standard.Value.Comp, owner);

        if (viewer is { } player)
            state.Payment = _payment.GetState(player, owner, console.Comp.TreasuryShare);

        return state;
    }
}
