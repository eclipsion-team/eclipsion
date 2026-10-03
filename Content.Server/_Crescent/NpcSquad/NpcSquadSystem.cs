using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Server._Crescent.NPC;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Systems;
using Content.Server.Popups;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Actions;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Pointing;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: players recruiting faction soldier NPCs into a squad and ordering them around.
/// </summary>
/// <remarks>
/// <para>
/// Recruiting is a right-click verb on the NPC, open to anyone the NPC counts as its own side. The first
/// recruit gives the player a hotbar action that opens the squad window, where orders are given to everyone
/// at once or to one soldier at a time. Pointing at anyone - friend or foe - has the squad go for them, and
/// for a while for everyone of their faction too.
/// </para>
/// <para>
/// Orders reach the HTN as <see cref="NPCBlackboard.CurrentOrders"/>, the same key the rat king uses for its
/// servants, and FactionSoldierCompound branches on it. Where the soldier may go and what it may shoot while
/// following or defending is enforced through <see cref="TryGetLeash"/> and <see cref="IsTargetAllowed"/>.
/// </para>
/// <para>
/// Defending and holding pin the soldier to one spot, <see cref="NpcSquadMemberComponent.DefendPoint"/>: it
/// moves only to get back onto it. Formations are in NpcSquadSystem.Formation, the barricade ring in
/// NpcSquadSystem.Fort.
/// </para>
/// </remarks>
public sealed partial class NpcSquadSystem : EntitySystem
{
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NpcGunHandlingSystem _npcGun = default!;
    [Dependency] private readonly NpcFriendlyFireRetaliationSystem _retaliation = default!;
    [Dependency] private readonly NpcIffSystem _iff = default!;
    [Dependency] private readonly NpcSquadHostilitySystem _hostility = default!;
    [Dependency] private readonly NpcPassiveTargetSystem _passiveTarget = default!;
    [Dependency] private readonly NpcTacticalSystem _tactical = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public const int MaxMembers = 6;

    /// <summary>
    /// How far a holding soldier will go to a spot its leader points out.
    /// </summary>
    public const float HoldMoveRange = 10f;

    private static readonly TimeSpan MedicClaimTime = TimeSpan.FromSeconds(3);

    private const string ActionProto = "ActionNpcSquadCommand";
    private const string BuiType = "NpcSquadBoundUserInterface";

    // FollowCompound's own keys, which have defaults in NPCBlackboard but no constants.
    private const string FollowRangeKey = "FollowRange";
    private const string FollowCloseRangeKey = "FollowCloseRange";

    // The post a defending or holding soldier keeps to, see soldier_tactical.yml.
    private const string DefendCoordinatesKey = "DefendCoordinates";
    private const string DefendRangeKey = "DefendRange";
    private const string DefendLeaveRangeKey = "DefendLeaveRange";

    /// <summary>
    /// How close to its post a soldier gets when it walks back onto it.
    /// </summary>
    private const float DefendArriveRange = 0.4f;

    /// <summary>
    /// How far off its post a soldier may be pushed before it walks back onto it.
    /// </summary>
    private const float DefendLeaveRange = 0.9f;

    /// <summary>
    /// How far from a downed leader the squad takes up posts round them.
    /// </summary>
    private const float LeaderDownRingRadius = 1.5f;

    private static readonly SpriteSpecifier RecruitIcon =
        new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/sentient.svg.192dpi.png"));

    private static readonly SpriteSpecifier DismissIcon =
        new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/close.svg.192dpi.png"));

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextUpdate;

    private readonly List<string> _targetAllegiances = new();
    private readonly List<string> _leaderAllegiances = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcSquadRecruitableComponent, GetVerbsEvent<Verb>>(OnGetVerbs);

        SubscribeLocalEvent<NpcSquadMemberComponent, MobStateChangedEvent>(OnMemberStateChanged);
        SubscribeLocalEvent<NpcSquadMemberComponent, ComponentShutdown>(OnMemberShutdown);

        SubscribeLocalEvent<NpcSquadLeaderComponent, MobStateChangedEvent>(OnLeaderStateChanged);
        SubscribeLocalEvent<NpcSquadLeaderComponent, ComponentShutdown>(OnLeaderShutdown);
        SubscribeLocalEvent<NpcSquadLeaderComponent, NpcSquadMenuActionEvent>(OnMenuAction);
        SubscribeLocalEvent<NpcSquadLeaderComponent, AfterPointedAtEvent>(OnLeaderPointed);
        SubscribeLocalEvent<NpcSquadLeaderComponent, AfterPointedAtTileEvent>(OnLeaderPointedAtTile);

        Subs.BuiEvents<NpcSquadLeaderComponent>(NpcSquadUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<NpcSquadOrderMessage>(OnOrderMessage);
            subs.Event<NpcSquadDismissMessage>(OnDismissMessage);
            subs.Event<NpcSquadFormationMessage>(OnFormationMessage);
            subs.Event<NpcSquadBuildFortMessage>(OnBuildFortMessage);
            subs.Event<NpcSquadKillAllMessage>(OnKillAllMessage);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateHeadings();

        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;

        var members = EntityQueryEnumerator<NpcSquadMemberComponent>();
        while (members.MoveNext(out var uid, out var member))
        {
            // Stop chasing a pointed-out target once it's down.
            if (member.FocusTarget is { } focus && (TerminatingOrDeleted(focus) || !_mobState.IsAlive(focus)))
            {
                member.FocusTarget = null;

                if (TryComp<HTNComponent>(uid, out var htn))
                    htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            }
        }

        var leaders = EntityQueryEnumerator<NpcSquadLeaderComponent>();
        while (leaders.MoveNext(out var uid, out var leader))
        {
            UpdateFort(uid, leader);

            if (_ui.IsUiOpen(uid, NpcSquadUiKey.Key))
                UpdateUi(uid, leader);
        }
    }

    #region Recruiting

    private void OnGetVerbs(Entity<NpcSquadRecruitableComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;
        var npc = ent.Owner;

        if (user == npc || HasComp<ActorComponent>(npc) || !_mobState.IsAlive(npc))
            return;

        if (TryComp<NpcSquadMemberComponent>(npc, out var member))
        {
            if (member.Leader != user)
                return;

            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("npc-squad-verb-dismiss"),
                Icon = DismissIcon,
                Act = () => Dismiss(npc, announce: true),
                Priority = 1,
            });
            return;
        }

        var canRecruit = CanRecruit(user, npc, out var reason);

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("npc-squad-verb-recruit"),
            Icon = RecruitIcon,
            Act = () => TryRecruit(user, npc),
            Disabled = !canRecruit,
            Message = reason,
            Priority = 1,
        });
    }

    public bool CanRecruit(EntityUid user, EntityUid npc, [NotNullWhen(false)] out string? reason)
    {
        reason = null;

        if (!_mobState.IsAlive(user))
        {
            reason = Loc.GetString("npc-squad-recruit-fail-incapacitated");
            return false;
        }

        if (HasComp<NpcSquadMemberComponent>(npc))
        {
            reason = Loc.GetString("npc-squad-recruit-fail-taken", ("npc", npc));
            return false;
        }

        if (!_iff.IsFriendly(npc, user))
        {
            reason = Loc.GetString("npc-squad-recruit-fail-faction", ("npc", npc));
            return false;
        }

        if (TryComp<NpcSquadLeaderComponent>(user, out var leader) && leader.Members.Count >= MaxMembers)
        {
            reason = Loc.GetString("npc-squad-recruit-fail-full", ("max", MaxMembers));
            return false;
        }

        return true;
    }

    public bool TryRecruit(EntityUid user, EntityUid npc)
    {
        if (!CanRecruit(user, npc, out var reason))
        {
            _popup.PopupEntity(reason, npc, user, PopupType.SmallCaution);
            return false;
        }

        var leader = EnsureComp<NpcSquadLeaderComponent>(user);

        if (leader.Members.Count == 0)
        {
            _actions.AddAction(user, ref leader.Action, ActionProto);
            _ui.SetUi(user, NpcSquadUiKey.Key, new InterfaceData(BuiType, interactionRange: -1f));
        }

        leader.Members.Add(npc);

        var member = EnsureComp<NpcSquadMemberComponent>(npc);
        member.Leader = user;
        EnsureComp<NpcIffComponent>(npc).SquadLeader = user;

        SetOrder(npc, member, NpcSquadOrder.Follow);

        _tactical.TryCallout(npc, NpcCalloutType.Recruited, force: true);
        _popup.PopupEntity(Loc.GetString("npc-squad-recruited", ("npc", npc)), npc, user);

        UpdateUi(user, leader);
        return true;
    }

    /// <summary>
    /// Releases an NPC from whatever squad it is in. It goes back to being an ordinary soldier of its side.
    /// </summary>
    public void Dismiss(EntityUid npc, bool announce = false)
    {
        if (!TryComp<NpcSquadMemberComponent>(npc, out var member))
            return;

        var leaderUid = member.Leader;

        if (announce)
        {
            _tactical.TryCallout(npc, NpcCalloutType.Dismissed, force: true);

            if (!TerminatingOrDeleted(leaderUid))
                _popup.PopupEntity(Loc.GetString("npc-squad-dismissed", ("npc", npc)), npc, leaderUid);
        }

        // The shutdown handler does the rest: taking it off the roster and clearing its orders.
        RemComp<NpcSquadMemberComponent>(npc);
    }

    private void OnMemberShutdown(Entity<NpcSquadMemberComponent> ent, ref ComponentShutdown args)
    {
        var npc = ent.Owner;

        if (TryComp<NpcIffComponent>(npc, out var iff))
            iff.SquadLeader = null;

        if (!TerminatingOrDeleted(npc) && TryComp<HTNComponent>(npc, out var htn))
        {
            htn.Blackboard.Remove<NpcSquadOrder>(NPCBlackboard.CurrentOrders);
            htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
            htn.Blackboard.Remove<EntityCoordinates>(DefendCoordinatesKey);
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            ForceReplan(npc, htn);
        }

        var leaderUid = ent.Comp.Leader;

        if (!TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader) || !leader.Members.Remove(npc))
            return;

        if (leader.Medic == npc)
            leader.Medic = null;

        ReleaseFortClaim(leader, npc);

        if (leader.Members.Count == 0)
        {
            DisbandLeader(leaderUid, leader);
            return;
        }

        UpdateUi(leaderUid, leader);
    }

    private void OnMemberStateChanged(Entity<NpcSquadMemberComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
        {
            if (!TerminatingOrDeleted(ent.Comp.Leader))
                _popup.PopupEntity(Loc.GetString("npc-squad-member-died", ("npc", ent.Owner)), ent.Comp.Leader, ent.Comp.Leader, PopupType.MediumCaution);

            RemComp<NpcSquadMemberComponent>(ent);
            return;
        }

        if (TryComp<NpcSquadLeaderComponent>(ent.Comp.Leader, out var leader))
            UpdateUi(ent.Comp.Leader, leader);
    }

    #endregion

    #region Leader

    private void OnLeaderStateChanged(Entity<NpcSquadLeaderComponent> ent, ref MobStateChangedEvent args)
    {
        switch (args.NewMobState)
        {
            case MobState.Critical:
            {
                // The leader is down: everyone takes up a post in a ring round them and holds it.
                var acknowledged = false;
                var members = ent.Comp.Members.ToArray();
                _takenTiles.Clear();

                for (var i = 0; i < members.Length; i++)
                {
                    var npc = members[i];
                    if (!TryComp<NpcSquadMemberComponent>(npc, out var member))
                        continue;

                    if (member.OrderBeforeLeaderDown == null)
                    {
                        member.OrderBeforeLeaderDown = member.Order;
                        member.DefendPointBeforeLeaderDown = member.DefendPoint;
                    }

                    SetOrder(npc, member, NpcSquadOrder.Defend, GetRingPost(ent, i, members.Length));

                    if (!acknowledged)
                        acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
                }

                break;
            }
            case MobState.Alive when args.OldMobState == MobState.Critical:
            {
                // Back up: everyone goes back to what they were doing before.
                var acknowledged = false;
                foreach (var npc in ent.Comp.Members.ToArray())
                {
                    if (!TryComp<NpcSquadMemberComponent>(npc, out var member) ||
                        member.OrderBeforeLeaderDown is not { } previous)
                    {
                        continue;
                    }

                    var previousPost = member.DefendPointBeforeLeaderDown;
                    member.OrderBeforeLeaderDown = null;
                    member.DefendPointBeforeLeaderDown = null;
                    SetOrder(npc, member, previous, previousPost);

                    if (!acknowledged)
                        acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
                }

                UpdateUi(ent, ent.Comp);
                break;
            }
            case MobState.Dead:
                DisbandLeader(ent, ent.Comp);
                break;
        }
    }

    private void OnLeaderShutdown(Entity<NpcSquadLeaderComponent> ent, ref ComponentShutdown args)
    {
        // Emptied first so the members' own shutdown doesn't come back here trying to disband it again.
        var members = ent.Comp.Members.ToArray();
        ent.Comp.Members.Clear();

        foreach (var npc in members)
        {
            RemComp<NpcSquadMemberComponent>(npc);
        }

        if (TerminatingOrDeleted(ent))
            return;

        _actions.RemoveAction(ent.Owner, ent.Comp.Action);
        ent.Comp.Action = null;
        _ui.CloseUi(ent.Owner, NpcSquadUiKey.Key);
    }

    private void DisbandLeader(EntityUid leader, NpcSquadLeaderComponent comp)
    {
        // The component shutdown does the actual releasing, so every way a squad can end goes the same way.
        RemComp(leader, comp);
    }

    private void OnMenuAction(Entity<NpcSquadLeaderComponent> ent, ref NpcSquadMenuActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        _ui.TryToggleUi(ent.Owner, NpcSquadUiKey.Key, ent.Owner);
    }

    private void OnLeaderPointed(Entity<NpcSquadLeaderComponent> ent, ref AfterPointedAtEvent args)
    {
        var target = args.Pointed;

        if (target == ent.Owner)
            return;

        // Not someone to shoot: somewhere to go, for whoever is holding.
        if (!HasComp<MobStateComponent>(target) || !_mobState.IsAlive(target))
        {
            MoveHoldersTo(ent, Transform(target).Coordinates);
            return;
        }

        // Pointing out one of its own squad turns the rest on them: it is out of the squad first, so it no
        // longer counts as their squadmate.
        if (ent.Comp.Members.Contains(target))
            Dismiss(target);

        // Whoever is pointed out gets attacked, friend or foe, and for a while so does everyone of their
        // faction - except the soldier's own, and its leader's.
        _hostility.GetAllegiances(target, _targetAllegiances);
        _hostility.GetAllegiances(ent.Owner, _leaderAllegiances);

        var anyone = false;
        var acknowledged = false;

        foreach (var npc in ent.Comp.Members.ToArray())
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || member.Order == NpcSquadOrder.HoldFire)
                continue;

            var duration = CompOrNull<NpcSquadRecruitableComponent>(npc)?.OrderedHostilityTime ?? TimeSpan.FromMinutes(2);

            // Also what lets it shoot a friend: its rounds would pass straight through one otherwise.
            _hostility.AddTarget(npc, target, duration);

            foreach (var faction in _targetAllegiances)
            {
                if (!_npcFaction.IsMember(npc, faction) && !_leaderAllegiances.Contains(faction))
                    _hostility.AddFaction(npc, faction, duration);
            }

            // A pointed-out soldier of its own side fights back instead of standing there taking it.
            if (HasComp<HTNComponent>(target) && !HasComp<ActorComponent>(target) && _iff.IsFriendly(target, npc))
            {
                _hostility.AddTarget(target, npc, duration);
                ForceReplan(target);
            }

            member.FocusTarget = target;
            _npc.SetBlackboard(npc, NPCBlackboard.CurrentOrderedTarget, target);
            ForceReplan(npc);
            anyone = true;

            if (!acknowledged)
                acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
        }

        if (anyone)
            _popup.PopupEntity(Loc.GetString("npc-squad-focus", ("target", target)), ent, ent, PopupType.Small);
    }

    private void OnLeaderPointedAtTile(Entity<NpcSquadLeaderComponent> ent, ref AfterPointedAtTileEvent args)
    {
        MoveHoldersTo(ent, args.Coordinates);
    }

    /// <summary>
    /// Sends every squadmate holding within <see cref="HoldMoveRange"/> of <paramref name="coords"/> over to it,
    /// to hold there instead. The nearest gets the spot itself and the rest spread over the floor round it
    /// rather than pile onto the one tile.
    /// </summary>
    /// <returns>How many went.</returns>
    public int MoveHoldersTo(Entity<NpcSquadLeaderComponent> ent, EntityCoordinates coords)
    {
        if (!_mobState.IsAlive(ent) ||
            _transform.GetGrid(coords) is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            return 0;
        }

        var target = _transform.ToMapCoordinates(coords);
        var movers = new List<(float Distance, EntityUid Npc, NpcSquadMemberComponent Member)>();
        var anyHolding = false;

        _takenTiles.Clear();

        foreach (var npc in ent.Comp.Members)
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || !_mobState.IsAlive(npc))
                continue;

            var holding = member.Order == NpcSquadOrder.HoldFire;
            anyHolding |= holding;

            var pos = _transform.GetMapCoordinates(npc);
            var distance = pos.MapId == target.MapId ? (pos.Position - target.Position).Length() : float.MaxValue;

            if (holding && distance <= HoldMoveRange)
            {
                movers.Add((distance, npc, member));
                continue;
            }

            // Whoever stays put keeps their spot.
            if (member.DefendPoint is { } post && _transform.GetGrid(post) == gridUid)
                _takenTiles.Add(_map.TileIndicesFor(gridUid, grid, post));
        }

        if (movers.Count == 0)
        {
            if (anyHolding)
                _popup.PopupEntity(Loc.GetString("npc-squad-hold-move-too-far", ("range", (int) HoldMoveRange)), ent, ent, PopupType.SmallCaution);

            return 0;
        }

        movers.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var moved = 0;
        var acknowledged = false;

        foreach (var (_, npc, member) in movers)
        {
            if (!_tactical.TryFindStandableNear(gridUid, target, 2, _takenTiles, out var spot, out var tile))
                continue;

            _takenTiles.Add(tile);
            member.OrderBeforeLeaderDown = null;
            member.DefendPointBeforeLeaderDown = null;
            SetOrder(npc, member, NpcSquadOrder.HoldFire, spot);
            moved++;

            if (!acknowledged)
                acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
        }

        if (moved > 0)
            _popup.PopupEntity(Loc.GetString("npc-squad-hold-move"), ent, ent, PopupType.Small);

        return moved;
    }

    #endregion

    #region Orders

    /// <summary>
    /// Gives an order to one squad member.
    /// </summary>
    /// <param name="post">
    /// For <see cref="NpcSquadOrder.Defend"/> and <see cref="NpcSquadOrder.HoldFire"/>, the spot to hold.
    /// Where the NPC stands now if not given.
    /// </param>
    public void SetOrder(EntityUid npc, NpcSquadMemberComponent member, NpcSquadOrder order, EntityCoordinates? post = null)
    {
        member.Order = order;
        member.DefendPoint = null;

        if (!TryComp<HTNComponent>(npc, out var htn))
            return;

        var blackboard = htn.Blackboard;
        blackboard.SetValue(NPCBlackboard.CurrentOrders, order);
        blackboard.Remove<EntityCoordinates>(DefendCoordinatesKey);

        switch (order)
        {
            case NpcSquadOrder.Defend:
            case NpcSquadOrder.HoldFire:
                // Grid-relative, so the post stays put whoever walks off.
                member.DefendPoint = post is { } given && given.IsValid(EntityManager)
                    ? given
                    : _transform.GetMoverCoordinates(npc);

                blackboard.SetValue(DefendCoordinatesKey, member.DefendPoint.Value);
                blackboard.SetValue(DefendRangeKey, DefendArriveRange);
                blackboard.SetValue(DefendLeaveRangeKey, DefendLeaveRange);
                blackboard.SetValue(NPCBlackboard.FollowTarget, member.DefendPoint.Value);
                blackboard.SetValue(FollowRangeKey, DefendLeaveRange);
                blackboard.SetValue(FollowCloseRangeKey, DefendArriveRange);
                break;
            default:
                blackboard.SetValue(NPCBlackboard.FollowTarget, new EntityCoordinates(member.Leader, Vector2.Zero));
                blackboard.SetValue(FollowRangeKey, order == NpcSquadOrder.Attack ? 6f : 4f);
                blackboard.SetValue(FollowCloseRangeKey, 2.5f);
                break;
        }

        if (order == NpcSquadOrder.HoldFire)
        {
            member.FocusTarget = null;
            blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
        }

        ForceReplan(npc, htn);
    }

    /// <summary>
    /// Throws away whatever the NPC was doing so a new order - or a new target - takes effect now rather than
    /// whenever its current task happens to end.
    /// </summary>
    public void ForceReplan(EntityUid npc, HTNComponent? htn = null)
    {
        if (!Resolve(npc, ref htn, false))
            return;

        if (htn.Plan != null)
        {
            _htn.ShutdownTask(htn.Plan.CurrentOperator, htn.Blackboard, HTNOperatorStatus.BetterPlan);
            _htn.ShutdownPlan(htn);
        }

        // A plan already being worked out was worked out for the old order.
        htn.PlanningToken?.Cancel();
        htn.PlanningJob = null;
        htn.PlanningToken = null;

        _htn.Replan(htn);
    }

    private void OnOrderMessage(Entity<NpcSquadLeaderComponent> ent, ref NpcSquadOrderMessage args)
    {
        var acknowledged = false;

        foreach (var npc in ent.Comp.Members.ToArray())
        {
            if (args.Member != null && GetNetEntity(npc) != args.Member)
                continue;

            if (!TryComp<NpcSquadMemberComponent>(npc, out var member))
                continue;

            // A fresh order from the leader replaces whatever the squad would have gone back to.
            member.OrderBeforeLeaderDown = null;
            member.DefendPointBeforeLeaderDown = null;
            SetOrder(npc, member, args.Order);

            if (!acknowledged)
                acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
        }

        _popup.PopupEntity(Loc.GetString($"npc-squad-order-given-{args.Order.ToString().ToLowerInvariant()}"), ent, ent);
        UpdateUi(ent, ent.Comp);
    }

    private void OnDismissMessage(Entity<NpcSquadLeaderComponent> ent, ref NpcSquadDismissMessage args)
    {
        foreach (var npc in ent.Comp.Members.ToArray())
        {
            if (args.Member != null && GetNetEntity(npc) != args.Member)
                continue;

            Dismiss(npc, announce: true);
        }
    }

    #endregion

    #region Leash

    /// <summary>
    /// How far from the squad's anchor - the leader, or its post when defending - this NPC may move to fight.
    /// </summary>
    /// <returns>False when the NPC isn't held anywhere and may go where it likes.</returns>
    public bool TryGetLeash(EntityUid npc, out MapCoordinates centre, out float range)
    {
        centre = MapCoordinates.Nullspace;
        range = 0f;

        if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || member.Order == NpcSquadOrder.Attack)
            return false;

        if (!TryGetAnchor(member, out centre))
            return false;

        var recruitable = CompOrNull<NpcSquadRecruitableComponent>(npc);
        range = member.Order == NpcSquadOrder.Defend
            ? recruitable?.DefendMoveRange ?? 5f
            : recruitable?.FollowMoveRange ?? 7f;

        // Someone is patching the leader up: the rest fight from right around them instead of wandering off
        // after better angles and leaving the two of them in the open.
        if (member.Order != NpcSquadOrder.Defend && TryGetActiveMedic(member.Leader, out _))
            range = MathF.Min(range, recruitable?.GuardMoveRange ?? 4f);

        return true;
    }

    /// <summary>
    /// Whether this NPC should be shooting at <paramref name="target"/>: never at its own side, never at all
    /// under a hold-fire order, and only near its anchor while following or defending - except back at
    /// whoever just shot it. Anti-boarder guns are left be until they shoot one of its side, and so is anyone
    /// wearing an ally's ID unless the NPC has been set on them.
    /// </summary>
    public bool IsTargetAllowed(EntityUid npc, EntityUid target)
    {
        if (_iff.IsFriendly(npc, target))
            return false;

        // Someone who shot it without being an enemy - its own side, an ally, a neutral - gets shot back,
        // whatever the orders.
        if (_retaliation.HasGrudge(npc, target))
            return true;

        TryComp<NpcSquadMemberComponent>(npc, out var member);

        // NPC factions know nothing of diplomacy, so the hostile list still names a faction its side has since
        // allied with. The ally's ID settles it - unless the leader pointed them out, or their faction is hated.
        if (member?.FocusTarget != target && IsAllied(npc, target) && !_npcFaction.GetHostiles(npc).Contains(target))
            return false;

        // Anti-boarder guns and the like are left be until they shoot one of its side, unless pointed out.
        if (member?.FocusTarget != target && _passiveTarget.IsLeftAlone(npc, target))
            return false;

        if (member == null)
            return true;

        if (member.Order == NpcSquadOrder.HoldFire)
            return false;

        if (member.Order == NpcSquadOrder.Attack || member.FocusTarget == target)
            return true;

        if (!TryGetAnchor(member, out var centre))
            return true;

        var recruitable = CompOrNull<NpcSquadRecruitableComponent>(npc);
        var range = member.Order == NpcSquadOrder.Defend
            ? recruitable?.DefendEngageRange ?? 14f
            : recruitable?.FollowEngageRange ?? 12f;

        var targetPos = _transform.GetMapCoordinates(target);
        return targetPos.MapId == centre.MapId && (targetPos.Position - centre.Position).Length() <= range;
    }

    private bool TryGetAnchor(NpcSquadMemberComponent member, out MapCoordinates centre)
    {
        centre = MapCoordinates.Nullspace;

        if (member.Order == NpcSquadOrder.Defend && member.DefendPoint is { } post)
        {
            if (!post.IsValid(EntityManager))
                return false;

            centre = _transform.ToMapCoordinates(post);
            return true;
        }

        if (TerminatingOrDeleted(member.Leader))
            return false;

        centre = _transform.GetMapCoordinates(member.Leader);
        return true;
    }

    #endregion

    #region Leader care

    /// <summary>
    /// The player this NPC follows, if it is in a squad.
    /// </summary>
    public bool TryGetLeader(EntityUid npc, out EntityUid leader)
    {
        leader = EntityUid.Invalid;

        if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || TerminatingOrDeleted(member.Leader))
            return false;

        leader = member.Leader;
        return true;
    }

    /// <summary>
    /// Makes this NPC the one squadmate looking after the leader, unless someone else already is.
    /// </summary>
    /// <returns>True if this NPC is (now) the squad's medic.</returns>
    public bool TryClaimMedic(EntityUid npc)
    {
        if (!TryComp<NpcSquadMemberComponent>(npc, out var member) ||
            !TryComp<NpcSquadLeaderComponent>(member.Leader, out var leader))
        {
            return false;
        }

        var now = _timing.CurTime;

        if (leader.Medic is { } current && current != npc && now < leader.MedicUntil &&
            leader.Members.Contains(current) && _mobState.IsAlive(current))
        {
            return false;
        }

        if (leader.Medic != npc)
            _tactical.TryCallout(npc, NpcCalloutType.Medic, force: true);

        leader.Medic = npc;
        leader.MedicUntil = now + MedicClaimTime;
        return true;
    }

    /// <summary>
    /// The squadmate patching <paramref name="leaderUid"/> up right now, if anyone is.
    /// </summary>
    public bool TryGetActiveMedic(EntityUid leaderUid, out EntityUid medic)
    {
        medic = EntityUid.Invalid;

        if (!TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader) ||
            leader.Medic is not { } current ||
            _timing.CurTime >= leader.MedicUntil ||
            !leader.Members.Contains(current) ||
            !_mobState.IsAlive(current))
        {
            return false;
        }

        medic = current;
        return true;
    }

    /// <summary>
    /// The squad's members that are up and about, in the order they joined - everyone who could stand guard.
    /// </summary>
    public IEnumerable<EntityUid> GetActiveMembers(EntityUid leaderUid)
    {
        if (!TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader))
            yield break;

        foreach (var member in leader.Members)
        {
            if (_mobState.IsAlive(member))
                yield return member;
        }
    }

    public void ReleaseMedic(EntityUid npc)
    {
        if (TryComp<NpcSquadMemberComponent>(npc, out var member) &&
            TryComp<NpcSquadLeaderComponent>(member.Leader, out var leader) &&
            leader.Medic == npc)
        {
            leader.Medic = null;
        }
    }

    /// <summary>
    /// Whether a squadmate may put a medipen into the leader now.
    /// </summary>
    public bool CanMedipenLeader(EntityUid leaderUid)
    {
        return TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader) && _timing.CurTime >= leader.NextMedipen;
    }

    /// <summary>
    /// Starts the squad-wide cooldown after a medipen actually went into the leader.
    /// </summary>
    public void StartLeaderMedipenCooldown(EntityUid leaderUid, TimeSpan cooldown)
    {
        if (TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader))
            leader.NextMedipen = _timing.CurTime + cooldown;
    }

    public NpcSquadOrder? GetOrder(EntityUid npc)
    {
        return TryComp<NpcSquadMemberComponent>(npc, out var member) ? member.Order : null;
    }

    /// <summary>
    /// Whether this NPC is pinned to a post: defending or holding, so it fights from where it stands.
    /// </summary>
    public bool IsHoldingPost(EntityUid npc)
    {
        return TryComp<NpcSquadMemberComponent>(npc, out var member) &&
               member.Order is NpcSquadOrder.Defend or NpcSquadOrder.HoldFire &&
               member.DefendPoint != null;
    }

    #endregion

    #region UI

    private void OnUiOpened(Entity<NpcSquadLeaderComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent, ent.Comp);
    }

    private void UpdateUi(EntityUid leaderUid, NpcSquadLeaderComponent leader)
    {
        if (!_ui.IsUiOpen(leaderUid, NpcSquadUiKey.Key))
            return;

        var leaderPos = _transform.GetMapCoordinates(leaderUid);
        var states = new List<NpcSquadMemberState>(leader.Members.Count);

        foreach (var npc in leader.Members)
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member))
                continue;

            var state = new NpcSquadMemberState
            {
                Entity = GetNetEntity(npc),
                Name = Name(npc),
                Order = member.Order,
                InCombat = HasComp<NPCRangedCombatComponent>(npc) || HasComp<NPCMeleeCombatComponent>(npc),
            };

            _tactical.TryGetDamageFraction(npc, out var damage);
            state.Health = Math.Clamp(1f - damage, 0f, 1f);

            state.Condition = _mobState.IsDead(npc) ? NpcSquadMemberCondition.Dead
                : _mobState.IsCritical(npc) ? NpcSquadMemberCondition.Critical
                : damage >= 0.35f ? NpcSquadMemberCondition.Wounded
                : NpcSquadMemberCondition.Healthy;

            if (_gun.TryGetGun(npc, out var gunUid, out _) && gunUid != npc)
                state.SpareMagazines = _npcGun.CountSpareMagazines(npc, gunUid);

            var pos = _transform.GetMapCoordinates(npc);
            if (pos.MapId == leaderPos.MapId)
                state.Distance = (pos.Position - leaderPos.Position).Length();

            states.Add(state);
        }

        _ui.SetUiState(leaderUid, NpcSquadUiKey.Key, new NpcSquadBuiState(states, MaxMembers, leader.Formation, leader.KillAll));
    }

    #endregion
}
