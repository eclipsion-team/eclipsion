using System.Numerics;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Territory;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class TerritorySpawnBeaconSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    // only set for the length of a joingame call
    private readonly Dictionary<NetUserId, EntityUid> _requests = new();
    private List<TerritoryDeployPoint> _published = new();
    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerJoinedLobby);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<TerritorySpawnBeaconComponent, ExaminedEvent>(OnExamined);
    }

    private void OnExamined(Entity<TerritorySpawnBeaconComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString(IsActive(ent, out _)
            ? "territory-spawn-beacon-examine-online"
            : "territory-spawn-beacon-examine-offline"));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshInterval;

        var points = BuildPoints();
        if (SamePoints(points, _published))
            return;

        _published = points;
        RaiseNetworkEvent(new TerritoryDeployPointsEvent { Points = points });
    }

    public void SetRequest(ICommonSession player, NetEntity? beacon)
    {
        if (beacon != null && TryGetEntity(beacon, out var uid))
            _requests[player.UserId] = uid.Value;
        else
            _requests.Remove(player.UserId);
    }

    public bool IsActive(EntityUid beacon, out PersistentPoiGridComponent territory)
    {
        territory = default!;

        if (!TryComp<TerritorySpawnBeaconComponent>(beacon, out var comp) || TerminatingOrDeleted(beacon))
            return false;

        var xform = Transform(beacon);
        if (!xform.Anchored || xform.GridUid is not { } grid || _container.IsEntityInContainer(beacon))
            return false;

        if (!TryComp<PersistentPoiGridComponent>(grid, out var held) ||
            held.Owner == null ||
            held.Level < comp.MinLevel)
        {
            return false;
        }

        territory = held;
        return _power.IsPowered(beacon);
    }

    private List<TerritoryDeployPoint> BuildPoints()
    {
        var points = new List<TerritoryDeployPoint>();
        var query = EntityQueryEnumerator<TerritorySpawnBeaconComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!IsActive(uid, out var territory))
                continue;

            points.Add(new TerritoryDeployPoint
            {
                Beacon = GetNetEntity(uid),
                Name = Name(xform.GridUid!.Value),
                Faction = territory.Owner!,
            });
        }

        points.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return points;
    }

    private static bool SamePoints(List<TerritoryDeployPoint> a, List<TerritoryDeployPoint> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Beacon != b[i].Beacon || a[i].Name != b[i].Name || a[i].Faction != b[i].Faction)
                return false;
        }

        return true;
    }

    private void OnPlayerJoinedLobby(PlayerJoinedLobbyEvent ev)
    {
        RaiseNetworkEvent(new TerritoryDeployPointsEvent { Points = _published }, ev.PlayerSession.Channel);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _requests.Clear();
        _published = new();
        _nextRefresh = TimeSpan.Zero;
        RaiseNetworkEvent(new TerritoryDeployPointsEvent());
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!_requests.Remove(ev.Player.UserId, out var beacon) || !ev.LateJoin)
            return;

        if (!TryDeploy(ev.Mob, beacon))
        {
            _chat.DispatchServerMessage(ev.Player, Loc.GetString("territory-spawn-beacon-unavailable"));
            return;
        }

        _chat.DispatchServerMessage(ev.Player,
            Loc.GetString("territory-spawn-beacon-arrived", ("territory", Name(Transform(beacon).GridUid!.Value))));
    }

    public bool TryDeploy(EntityUid mob, EntityUid beacon)
    {
        if (!IsActive(beacon, out var territory) || !PersistentTerritoryFactions.Holds(EntityManager, mob, territory))
            return false;

        if (_container.TryGetContainingContainer((mob, null, null), out var container))
            _container.Remove(mob, container, force: true);

        _transform.SetCoordinates(mob, Transform(beacon).Coordinates);
        _transform.AttachToGridOrMap(mob);
        _physics.SetLinearVelocity(mob, Vector2.Zero);
        return true;
    }
}
