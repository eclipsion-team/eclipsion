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
using Content.Shared.Pointing;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
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
/// at once or to one soldier at a time. Pointing at an enemy has the squad focus it.
/// </para>
/// <para>
/// Orders reach the HTN as <see cref="NPCBlackboard.CurrentOrders"/>, the same key the rat king uses for its
/// servants, and FactionSoldierCompound branches on it. Where the soldier may go and what it may shoot while
/// following or defending is enforced through <see cref="TryGetLeash"/> and <see cref="IsTargetAllowed"/>.
/// </para>
/// </remarks>
public sealed class NpcSquadSystem : EntitySystem
{
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly NpcGunHandlingSystem _npcGun = default!;
    [Dependency] private readonly NpcIffSystem _iff = default!;
    [Dependency] private readonly NpcTacticalSystem _tactical = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public const int MaxMembers = 6;

    private static readonly TimeSpan MedicClaimTime = TimeSpan.FromSeconds(3);

    private const string ActionProto = "ActionNpcSquadCommand";
    private const string BuiType = "NpcSquadBoundUserInterface";

    // FollowCompound's own keys, which have defaults in NPCBlackboard but no constants.
    private const string FollowRangeKey = "FollowRange";
    private const string FollowCloseRangeKey = "FollowCloseRange";

    private static readonly SpriteSpecifier RecruitIcon =
        new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/sentient.svg.192dpi.png"));

    private static readonly SpriteSpecifier DismissIcon =
        new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/close.svg.192dpi.png"));

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextUpdate;

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

        Subs.BuiEvents<NpcSquadLeaderComponent>(NpcSquadUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<NpcSquadOrderMessage>(OnOrderMessage);
            subs.Event<NpcSquadDismissMessage>(OnDismissMessage);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

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
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            ForceReplan(npc, htn);
        }

        var leaderUid = ent.Comp.Leader;

        if (!TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader) || !leader.Members.Remove(npc))
            return;

        if (leader.Medic == npc)
            leader.Medic = null;

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
                // The leader is down: everyone closes in around them and holds.
                var acknowledged = false;
                foreach (var npc in ent.Comp.Members.ToArray())
                {
                    if (!TryComp<NpcSquadMemberComponent>(npc, out var member))
                        continue;

                    member.OrderBeforeLeaderDown ??= member.Order;
                    SetOrder(npc, member, NpcSquadOrder.Defend);

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

                    member.OrderBeforeLeaderDown = null;
                    SetOrder(npc, member, previous);

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

        if (ent.Comp.Members.Contains(target) || !HasComp<MobStateComponent>(target) || !_mobState.IsAlive(target))
            return;

        var anyone = false;
        var acknowledged = false;

        foreach (var npc in ent.Comp.Members)
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || member.Order == NpcSquadOrder.HoldFire)
                continue;

            // Pointing out a friend is not an order to shoot them. Friend to this one, anyway: a squad can mix
            // allied factions, and the others may still go for it.
            if (_iff.IsFriendly(npc, target))
                continue;

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

    #endregion

    #region Orders

    /// <summary>
    /// Gives an order to one squad member.
    /// </summary>
    public void SetOrder(EntityUid npc, NpcSquadMemberComponent member, NpcSquadOrder order)
    {
        member.Order = order;
        member.DefendPoint = null;

        if (!TryComp<HTNComponent>(npc, out var htn))
            return;

        var blackboard = htn.Blackboard;
        blackboard.SetValue(NPCBlackboard.CurrentOrders, order);

        switch (order)
        {
            case NpcSquadOrder.Defend:
                // Grid-relative, so the post stays put when the leader walks off.
                member.DefendPoint = _transform.GetMoverCoordinates(member.Leader);
                blackboard.SetValue(NPCBlackboard.FollowTarget, member.DefendPoint.Value);
                blackboard.SetValue(FollowRangeKey, 2.5f);
                blackboard.SetValue(FollowCloseRangeKey, 1.5f);
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
    /// Throws away whatever the NPC was doing so the new order takes effect now rather than whenever its
    /// current task happens to end.
    /// </summary>
    private void ForceReplan(EntityUid npc, HTNComponent? htn = null)
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
    /// under a hold-fire order, and only near its anchor while following or defending.
    /// </summary>
    public bool IsTargetAllowed(EntityUid npc, EntityUid target)
    {
        if (_iff.IsFriendly(npc, target))
            return false;

        if (!TryComp<NpcSquadMemberComponent>(npc, out var member))
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

        _ui.SetUiState(leaderUid, NpcSquadUiKey.Key, new NpcSquadBuiState(states, MaxMembers));
    }

    #endregion
}
