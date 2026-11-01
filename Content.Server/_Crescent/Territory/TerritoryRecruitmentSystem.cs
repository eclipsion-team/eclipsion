using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._Crescent.NpcSquad;
using Content.Server.GameTicking;
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
/// The territory's recruitment console: tops the garrison back up and sends soldiers along with the holder, for
/// money, by drop pod. How many of each may stand at once goes by the territory's level.
/// </summary>
public sealed class TerritoryRecruitmentSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly TerritoryGarrisonSystem _garrison = default!;
    [Dependency] private readonly TerritoryPaymentSystem _payment = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<TerritoryRecruitmentConsoleComponent>(TerritoryRecruitmentUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<TerritoryRecruitMessage>(OnRecruit);
        });
    }

    private void OnOpened(Entity<TerritoryRecruitmentConsoleComponent> ent, ref BoundUIOpenedEvent args)
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

        // soldiers die and pods land without telling the console
        var consoles = EntityQueryEnumerator<TerritoryRecruitmentConsoleComponent>();
        while (consoles.MoveNext(out var uid, out var console))
        {
            if (_ui.IsUiOpen(uid, TerritoryRecruitmentUiKey.Key))
                UpdateConsole((uid, console));
        }
    }

    private void OnRecruit(Entity<TerritoryRecruitmentConsoleComponent> ent, ref TerritoryRecruitMessage args)
    {
        if (!TryRecruit(ent, args.Actor, args.Kind, args.Role, args.Count, args.Source, out var called, out var price, out var error))
        {
            _popup.PopupEntity(Loc.GetString(error), ent, args.Actor, PopupType.SmallCaution);
            UpdateConsole(ent);
            return;
        }

        _popup.PopupEntity(Loc.GetString(args.Kind == TerritoryRecruitKind.Escort
                ? "territory-recruit-escort-called"
                : "territory-recruit-garrison-called",
            ("count", called),
            ("role", Loc.GetString(_proto.Index(args.Role).Name)),
            ("price", price)), ent, args.Actor);
        UpdateConsole(ent);
    }

    public bool TryRecruit(
        Entity<TerritoryRecruitmentConsoleComponent> console,
        EntityUid actor,
        TerritoryRecruitKind kind,
        ProtoId<TerritorySoldierRolePrototype> roleId,
        int count,
        PaymentSource source,
        out int called,
        out int price,
        [NotNullWhen(false)] out string? error)
    {
        called = 0;
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
            !_garrison.TryGetStandard(grid, out var standard))
        {
            error = "territory-recruit-not-holder";
            return false;
        }

        if (!_garrison.HasSoldiers(standard.Value, owner))
        {
            error = "territory-recruit-no-soldiers";
            return false;
        }

        if (!TerritoryGarrisonSystem.Offers(owner, kind))
        {
            error = "territory-recruit-mercenaries-only";
            return false;
        }

        var role = _garrison.GetRoles(standard.Value, owner).FirstOrDefault(r => r.ID == roleId.Id);
        if (role == null)
        {
            error = "territory-recruit-unknown-role";
            return false;
        }

        if (!this.IsPowered(console, EntityManager))
        {
            error = "territory-recruit-unpowered";
            return false;
        }

        var room = Room(territory, kind, actor);
        called = Math.Min(count, room);
        if (called <= 0)
        {
            error = kind == TerritoryRecruitKind.Escort && SquadRoom(actor) <= 0
                ? "territory-recruit-squad-full"
                : "territory-recruit-full";
            return false;
        }

        var escort = kind == TerritoryRecruitKind.Escort;
        price = called * PriceOf(role, kind, console.Comp);
        if (!_payment.TryCharge(actor, owner, source, price, console.Comp.TreasuryShare,
                $"{called} {kind.ToString().ToLowerInvariant()} {role.ID}(s) on territory '{territory.RegionId}'", out error))
        {
            return false;
        }

        _garrison.CallIn(standard.Value, kind, called, role.ID,
            escort ? Transform(console).Coordinates : null,
            escort ? actor : null);
        return true;
    }

    public static int PriceOf(TerritorySoldierRolePrototype role, TerritoryRecruitKind kind,
        TerritoryRecruitmentConsoleComponent console)
    {
        return role.Price + (kind == TerritoryRecruitKind.Escort ? console.EscortSurcharge : 0);
    }

    private int Room(PersistentPoiGridComponent territory, TerritoryRecruitKind kind, EntityUid actor)
    {
        if (territory.Owner is not { } owner)
            return 0;

        var level = _levels.GetLevel(territory.Level);
        var limit = kind == TerritoryRecruitKind.Escort ? level?.EscortSoldiers ?? 0 : level?.GarrisonSoldiers ?? 0;
        var room = limit - _garrison.CountSoldiers(territory.RegionId, owner, kind);

        if (kind == TerritoryRecruitKind.Escort)
            room = Math.Min(room, SquadRoom(actor));

        return Math.Max(0, room);
    }

    private int SquadRoom(EntityUid actor)
    {
        var members = TryComp<NpcSquadLeaderComponent>(actor, out var leader) ? leader.Members.Count : 0;
        return Math.Max(0, NpcSquadSystem.MaxMembers - members - _garrison.CountInbound(actor));
    }

    private void UpdateConsole(Entity<TerritoryRecruitmentConsoleComponent> ent)
    {
        var viewer = _ui.GetActors(ent.Owner, TerritoryRecruitmentUiKey.Key).FirstOrDefault();
        _ui.SetUiState(ent.Owner, TerritoryRecruitmentUiKey.Key, BuildState(ent, viewer.IsValid() ? viewer : null));
    }

    public TerritoryRecruitmentState BuildState(Entity<TerritoryRecruitmentConsoleComponent> console, EntityUid? viewer)
    {
        var state = new TerritoryRecruitmentState
        {
            Powered = this.IsPowered(console, EntityManager),
            EscortSurcharge = console.Comp.EscortSurcharge,
        };

        if (Transform(console).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            return state;
        }

        state.Owner = territory.Owner;
        state.Level = territory.Level;

        var level = _levels.GetLevel(territory.Level);
        state.GarrisonLimit = level?.GarrisonSoldiers ?? 0;
        state.EscortLimit = level?.EscortSoldiers ?? 0;

        // an independent hires mercenaries for their squad and keeps no garrison
        state.Mercenaries = PersistentTerritoryFactions.IsIndependent(territory.Owner);
        if (state.Mercenaries)
            state.GarrisonLimit = 0;

        if (territory.Owner is not { } owner)
            return state;

        if (_garrison.TryGetStandard(grid, out var standard))
        {
            foreach (var role in _garrison.GetRoles(standard.Value, owner))
            {
                state.Roles.Add(role.ID);
            }
        }

        state.Garrison = _garrison.CountSoldiers(territory.RegionId, owner, TerritoryRecruitKind.Garrison);
        state.Escorts = _garrison.CountSoldiers(territory.RegionId, owner, TerritoryRecruitKind.Escort);

        if (viewer is { } player)
        {
            state.SquadRoom = SquadRoom(player);
            state.Payment = _payment.GetState(player, owner, console.Comp.TreasuryShare);
        }

        return state;
    }
}
