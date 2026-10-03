using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Religion.Components;
using Content.Server.Antag;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.EUI;
using Content.Shared._Crescent.Religion;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions;
using Content.Shared.Chat;
using Content.Shared.Crescent.Psionics;
using Content.Shared.DoAfter;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Verbs;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Religion;

/// <summary>
///     The Prophet's side of the Covenant of the Veiled Mother: Veiled Eyes, the forced Binding, and the Host menu.
/// </summary>
public sealed partial class VeiledProphetSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly AntagSelectionSystem _antag = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly EuiManager _eui = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReligionSystem _religion = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    private static readonly TimeSpan HostRefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Far enough to reach the corner of the biggest eye area from its centre.
    /// </summary>
    private const float EyeLookupRange = 3f;

    /// <summary>
    ///     Eyes are drawn two tiles across, so another one cannot be started this close.
    /// </summary>
    private const float EyeSpacing = 1.9f;

    /// <summary>
    ///     Host windows currently open, by the Prophet they show.
    /// </summary>
    private readonly Dictionary<EntityUid, VeiledHostEui> _openHosts = new();

    private TimeSpan _nextHostRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<VeiledProphetComponent, ComponentStartup>(OnProphetStartup);
        SubscribeLocalEvent<VeiledProphetComponent, ComponentShutdown>(OnProphetShutdown);
        SubscribeLocalEvent<VeiledProphetComponent, MobStateChangedEvent>(OnProphetMobStateChanged);
        SubscribeLocalEvent<VeiledProphetComponent, DrawVeiledEyeActionEvent>(OnDrawEye);
        SubscribeLocalEvent<VeiledProphetComponent, DrawVeiledEyeDoAfterEvent>(OnDrawEyeDoAfter);
        SubscribeLocalEvent<VeiledProphetComponent, OpenVeiledHostActionEvent>(OnOpenHost);
        SubscribeLocalEvent<VeiledProphetComponent, VeiledBindingDoAfterEvent>(OnBindingStep);

        SubscribeLocalEvent<VeiledEyeComponent, ComponentShutdown>(OnEyeShutdown);
        SubscribeLocalEvent<VeiledEyeComponent, GetVerbsEvent<Verb>>(OnEyeVerbs);
        SubscribeLocalEvent<VeiledEyeComponent, ScrubVeiledEyeDoAfterEvent>(OnEyeScrubbed);

        SubscribeLocalEvent<BelieverComponent, GetVerbsEvent<Verb>>(OnBelieverVerbs);
        SubscribeLocalEvent<BelieverComponent, ReligionChangedEvent>(OnReligionChanged);

        SubscribeLocalEvent<VeiledHostMemberComponent, ReligionChangeAttemptEvent>(OnMemberChangeAttempt);
        SubscribeLocalEvent<VeiledHostMemberComponent, ComponentShutdown>(OnMemberShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Distances and cooldowns move on their own, so an open Host window is pushed fresh state every so often.
        if (_openHosts.Count == 0 || _timing.CurTime < _nextHostRefresh)
            return;

        _nextHostRefresh = _timing.CurTime + HostRefreshInterval;

        foreach (var eui in _openHosts.Values)
        {
            if (!eui.IsShutDown)
                eui.StateDirty();
        }
    }

    #region Prophet

    private void OnProphetStartup(Entity<VeiledProphetComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent, ref ent.Comp.DrawEyeActionEntity, ent.Comp.DrawEyeAction);
        _actions.AddAction(ent, ref ent.Comp.HostActionEntity, ent.Comp.HostAction);
    }

    private void OnProphetShutdown(Entity<VeiledProphetComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent, ent.Comp.DrawEyeActionEntity);
        _actions.RemoveAction(ent, ent.Comp.HostActionEntity);

        foreach (var eye in ent.Comp.Eyes.ToArray())
            QueueDel(eye);

        // Members keep the faith, but there is no longer a Prophet for them to answer to. Nobody needs telling when
        // the whole map is going away at round end.
        var announce = !TerminatingOrDeleted(Transform(ent).MapUid);
        foreach (var member in ent.Comp.Host.ToArray())
        {
            if (announce && TryComp<VeiledHostMemberComponent>(member, out var comp) && comp.Bound)
                SendReleased(member);

            RemComp<VeiledHostMemberComponent>(member);
        }

        ent.Comp.Host.Clear();

        if (_openHosts.Remove(ent, out var eui) && !eui.IsShutDown)
            eui.Close();
    }

    /// <summary>
    ///     A dead Prophet's grip on the bound breaks. They stay in the Covenant, but are free to leave it.
    /// </summary>
    private void OnProphetMobStateChanged(Entity<VeiledProphetComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        foreach (var member in ent.Comp.Host)
        {
            if (!TryComp<VeiledHostMemberComponent>(member, out var comp) || !comp.Bound)
                continue;

            comp.Bound = false;
            SendReleased(member);
        }

        RefreshHost(ent);
    }

    private void SendReleased(EntityUid member)
    {
        _antag.SendBriefing(member, Loc.GetString("religion-veiled-released"), Color.LightGray, null);
    }

    #endregion

    #region Eyes

    private void OnDrawEye(Entity<VeiledProphetComponent> ent, ref DrawVeiledEyeActionEvent args)
    {
        if (args.Handled)
            return;

        if (HasComp<PsionicallyNullifiedComponent>(ent))
        {
            _popup.PopupEntity(Loc.GetString("religion-veiled-nullified"), ent, ent, PopupType.MediumCaution);
            return;
        }

        if (!TryGetEyeTile(args.Target, out _, out var reason))
        {
            _popup.PopupEntity(reason, ent, ent, PopupType.MediumCaution);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager,
            ent,
            ent.Comp.DrawTime,
            new DrawVeiledEyeDoAfterEvent(GetNetCoordinates(args.Target)),
            ent)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return;

        args.Handled = true;

        _popup.PopupEntity(Loc.GetString("religion-veiled-eye-drawing-others", ("prophet", ent.Owner)),
            ent,
            Filter.PvsExcept(ent),
            true,
            PopupType.Medium);

        if (ent.Comp.DrawLines.Count > 0)
            _religion.SpeakRiteLine(ent, _random.Pick(ent.Comp.DrawLines), InGameICChatType.Whisper);
    }

    private void OnDrawEyeDoAfter(Entity<VeiledProphetComponent> ent, ref DrawVeiledEyeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;

        if (!TryGetEyeTile(GetCoordinates(args.Coordinates), out var center, out var reason))
        {
            _popup.PopupEntity(reason, ent, ent, PopupType.MediumCaution);
            return;
        }

        var eye = Spawn(ent.Comp.EyePrototype, center);
        EnsureComp<VeiledEyeComponent>(eye).Prophet = ent;
        ent.Comp.Eyes.Add(eye);

        while (ent.Comp.Eyes.Count > ent.Comp.MaxEyes)
        {
            var oldest = ent.Comp.Eyes[0];
            ent.Comp.Eyes.RemoveAt(0);
            QueueDel(oldest);
            _popup.PopupEntity(Loc.GetString("religion-veiled-eye-oldest-closed"), ent, ent, PopupType.Small);
        }

        _popup.PopupEntity(Loc.GetString("religion-veiled-eye-drawn"), ent, ent, PopupType.Medium);
    }

    /// <summary>
    ///     Finds the centre of the floor tile under <paramref name="target"/>, if an eye can be drawn there.
    /// </summary>
    private bool TryGetEyeTile(EntityCoordinates target, out EntityCoordinates center, out string reason)
    {
        center = default;
        reason = Loc.GetString("religion-veiled-eye-bad-tile");

        if (_transform.GetGrid(target) is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var tile = _map.GetTileRef(gridUid, grid, target);
        if (tile.Tile.IsEmpty || _turf.IsTileBlocked(tile, CollisionGroup.Impassable))
            return false;

        center = _map.GridTileToLocal(gridUid, grid, tile.GridIndices);

        if (_lookup.GetEntitiesInRange<VeiledEyeComponent>(center, EyeSpacing).Count > 0)
        {
            reason = Loc.GetString("religion-veiled-eye-occupied");
            return false;
        }

        return true;
    }

    private void OnEyeShutdown(Entity<VeiledEyeComponent> ent, ref ComponentShutdown args)
    {
        if (TryComp<VeiledProphetComponent>(ent.Comp.Prophet, out var prophet))
            prophet.Eyes.Remove(ent);
    }

    private void OnEyeVerbs(Entity<VeiledEyeComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;

        // Only the Prophet who drew it can close it at will. Anyone else, another Prophet included, has to scrub it.
        if (ent.Comp.Prophet == user)
        {
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("religion-veiled-verb-close-eye"),
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/close.svg.192dpi.png")),
                Act = () => QueueDel(ent),
            });
            return;
        }

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("religion-veiled-verb-scrub-eye"),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/delete.svg.192dpi.png")),
            Act = () =>
            {
                var doAfter = new DoAfterArgs(EntityManager, user, ent.Comp.ScrubTime, new ScrubVeiledEyeDoAfterEvent(), ent, target: ent)
                {
                    BreakOnMove = true,
                    BreakOnDamage = true,
                    NeedHand = true,
                };

                _doAfter.TryStartDoAfter(doAfter);
            },
        });
    }

    private void OnEyeScrubbed(Entity<VeiledEyeComponent> ent, ref ScrubVeiledEyeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("religion-veiled-eye-scrubbed"), ent, args.User, PopupType.Medium);
        QueueDel(ent);
    }

    /// <summary>
    ///     Finds a Veiled Eye whose area <paramref name="uid"/> stands in. The area is a square of
    ///     <see cref="VeiledEyeComponent.HalfExtent"/> each way, measured in the eye's grid so it turns with it.
    /// </summary>
    private bool TryGetEyeUnder(EntityUid uid, out EntityUid eye)
    {
        eye = default;
        var xform = Transform(uid);
        var worldPos = _transform.GetWorldPosition(xform);

        foreach (var found in _lookup.GetEntitiesInRange<VeiledEyeComponent>(xform.Coordinates, EyeLookupRange))
        {
            var eyeXform = Transform(found);
            if (eyeXform.MapID != xform.MapID)
                continue;

            var local = Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(eyeXform.ParentUid));
            var offset = local - eyeXform.LocalPosition;
            if (MathF.Abs(offset.X) > found.Comp.HalfExtent || MathF.Abs(offset.Y) > found.Comp.HalfExtent)
                continue;

            eye = found;
            return true;
        }

        return false;
    }

    #endregion

    #region Binding

    private void OnBelieverVerbs(Entity<BelieverComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        // Only one system may own this event on BelieverComponent, so the admin faith verbs ride along here.
        _religion.AddAdminVerbs(ent, args);

        if (!args.CanAccess
            || !args.CanInteract
            || args.User == args.Target
            || !TryComp<VeiledProphetComponent>(args.User, out var prophetComp)
            || !TryGetEyeUnder(ent, out _))
            return;

        var prophet = new Entity<VeiledProphetComponent>(args.User, prophetComp);
        var target = ent.Owner;

        var verb = new Verb
        {
            Text = Loc.GetString("religion-veiled-verb-bind"),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/group.svg.192dpi.png")),
            Priority = 3,
            Act = () => TryBeginBinding(prophet, target),
        };

        if (!CanBind(prophet, target, out _, out var reason))
        {
            verb.Disabled = true;
            verb.Message = reason;
        }

        args.Verbs.Add(verb);
    }

    private bool CanBind(Entity<VeiledProphetComponent> prophet,
        EntityUid target,
        out EntityUid eye,
        out string? reason)
    {
        eye = default;
        reason = null;

        if (!_mobState.IsAlive(prophet))
            return false;

        if (HasComp<PsionicallyNullifiedComponent>(prophet))
        {
            reason = Loc.GetString("religion-veiled-nullified");
            return false;
        }

        if (target == prophet.Owner || _mobState.IsDead(target))
            return false;

        if (!TryGetEyeUnder(target, out eye))
        {
            reason = Loc.GetString("religion-veiled-bind-not-on-eye");
            return false;
        }

        if (TryComp<VeiledHostMemberComponent>(target, out var member) && member.Bound)
        {
            reason = Loc.GetString("religion-veiled-bind-already");
            return false;
        }

        if (TryComp<PsionicInsulationComponent>(target, out var insulation) && !insulation.Passthrough)
        {
            reason = Loc.GetString("religion-veiled-bind-insulated");
            return false;
        }

        // The Binding ignores faction on purpose, but not whatever else holds the target where they are.
        if (!_religion.CanChangeReligion(target, prophet.Comp.Religion, out reason))
            return false;

        return true;
    }

    private void TryBeginBinding(Entity<VeiledProphetComponent> prophet, EntityUid target)
    {
        if (!CanBind(prophet, target, out var eye, out var reason))
        {
            if (reason != null)
                _popup.PopupEntity(reason, prophet, prophet, PopupType.MediumCaution);

            return;
        }

        if (!StartBindingStep(prophet, target, eye, 0))
            return;

        _popup.PopupEntity(Loc.GetString("religion-veiled-bind-begin-target"), target, target, PopupType.LargeCaution);
        SpeakBindingLine(prophet, 0);
    }

    private bool StartBindingStep(Entity<VeiledProphetComponent> prophet, EntityUid target, EntityUid eye, int step)
    {
        var doAfter = new DoAfterArgs(EntityManager,
            prophet,
            prophet.Comp.BindingStepDelay,
            new VeiledBindingDoAfterEvent(step, GetNetEntity(eye)),
            prophet,
            target: target)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            DistanceThreshold = 2f,
        };

        return _doAfter.TryStartDoAfter(doAfter);
    }

    private void SpeakBindingLine(Entity<VeiledProphetComponent> prophet, int step)
    {
        if (step < prophet.Comp.BindingLines.Count)
            _religion.SpeakRiteLine(prophet, prophet.Comp.BindingLines[step]);
    }

    private void OnBindingStep(Entity<VeiledProphetComponent> ent, ref VeiledBindingDoAfterEvent args)
    {
        if (args.Handled || args.Target is not { } target)
            return;

        args.Handled = true;

        if (args.Cancelled)
        {
            _popup.PopupEntity(Loc.GetString("religion-veiled-bind-broken"), ent, ent, PopupType.MediumCaution);
            _popup.PopupEntity(Loc.GetString("religion-veiled-bind-broken-target"), target, target, PopupType.Medium);
            return;
        }

        // Stepping off the eye, or having it scrubbed out from under you, breaks the Binding.
        if (!CanBind(ent, target, out var eye, out var reason) || eye != GetEntity(args.Eye))
        {
            _popup.PopupEntity(reason ?? Loc.GetString("religion-veiled-bind-broken"), ent, ent, PopupType.MediumCaution);
            return;
        }

        var next = args.Step + 1;
        if (next < ent.Comp.BindingLines.Count)
        {
            if (StartBindingStep(ent, target, eye, next))
                SpeakBindingLine(ent, next);

            return;
        }

        _religion.SetReligion(target, ent.Comp.Religion, ent, forced: true);

        _popup.PopupEntity(Loc.GetString("religion-veiled-bound-popup"), target, target, PopupType.LargeCaution);
        _antag.SendBriefing(target,
            Loc.GetString("religion-veiled-bound-briefing", ("prophet", Name(ent))),
            ent.Comp.BindingColor,
            ent.Comp.BindingSound);

        _popup.PopupEntity(Loc.GetString("religion-veiled-bound-prophet", ("target", target)), ent, ent, PopupType.Medium);
    }

    #endregion

    #region Host

    private void OnReligionChanged(Entity<BelieverComponent> ent, ref ReligionChangedEvent args)
    {
        // Leaving the Covenant leaves the Host.
        if (TryComp<VeiledHostMemberComponent>(ent, out var member)
            && (!TryComp<VeiledProphetComponent>(member.Prophet, out var current) || current.Religion != args.NewReligion))
        {
            RemComp<VeiledHostMemberComponent>(ent);
        }

        if (args.Officiant is not { } officiant
            || !TryComp<VeiledProphetComponent>(officiant, out var prophet)
            || prophet.Religion != args.NewReligion)
            return;

        AddMember((officiant, prophet), ent, args.Forced);
    }

    private void AddMember(Entity<VeiledProphetComponent> prophet, EntityUid uid, bool bound)
    {
        var member = EnsureComp<VeiledHostMemberComponent>(uid);

        if (member.Prophet != prophet.Owner && TryComp<VeiledProphetComponent>(member.Prophet, out var previous))
        {
            previous.Host.Remove(uid);
            RefreshHost(member.Prophet);
        }

        member.Prophet = prophet;
        member.Bound |= bound;
        prophet.Comp.Host.Add(uid);
        RefreshHost(prophet);
    }

    private void OnMemberChangeAttempt(Entity<VeiledHostMemberComponent> ent, ref ReligionChangeAttemptEvent args)
    {
        if (!ent.Comp.Bound
            || !TryComp<VeiledProphetComponent>(ent.Comp.Prophet, out var prophet)
            || prophet.Religion == args.NewReligion)
            return;

        args.Cancelled = true;
        args.Reason = Loc.GetString("religion-veiled-bound-cannot-leave");
    }

    private void OnMemberShutdown(Entity<VeiledHostMemberComponent> ent, ref ComponentShutdown args)
    {
        if (!TryComp<VeiledProphetComponent>(ent.Comp.Prophet, out var prophet))
            return;

        prophet.Host.Remove(ent);
        RefreshHost(ent.Comp.Prophet);
    }

    private void OnOpenHost(Entity<VeiledProphetComponent> ent, ref OpenVeiledHostActionEvent args)
    {
        if (args.Handled || !TryComp<ActorComponent>(ent, out var actor))
            return;

        // Pressing the action again reopens the window rather than stacking another on top.
        if (_openHosts.Remove(ent, out var previous) && !previous.IsShutDown)
            previous.Close();

        var eui = new VeiledHostEui(ent, this);
        _openHosts[ent] = eui;
        _eui.OpenEui(eui, actor.PlayerSession);
        args.Handled = true;
    }

    internal void OnEuiClosed(VeiledHostEui eui)
    {
        if (_openHosts.TryGetValue(eui.Prophet, out var current) && current == eui)
            _openHosts.Remove(eui.Prophet);
    }

    private void RefreshHost(EntityUid prophet)
    {
        if (_openHosts.TryGetValue(prophet, out var eui) && !eui.IsShutDown)
            eui.StateDirty();
    }

    internal VeiledHostEuiState BuildHostState(EntityUid prophet)
    {
        var members = new List<VeiledHostMember>();

        if (!TryComp<VeiledProphetComponent>(prophet, out var comp))
            return new VeiledHostEuiState(members);

        var now = _timing.CurTime;
        var prophetXform = Transform(prophet);
        var prophetPos = _transform.GetWorldPosition(prophetXform);

        foreach (var uid in comp.Host)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<VeiledHostMemberComponent>(uid, out var member))
                continue;

            var status = _mobState.IsDead(uid)
                ? VeiledHostMemberStatus.Dead
                : _mobState.IsCritical(uid)
                    ? VeiledHostMemberStatus.Critical
                    : VeiledHostMemberStatus.Alive;

            float? distance = null;
            string? direction = null;

            var xform = Transform(uid);
            if (xform.MapID == prophetXform.MapID)
            {
                var offset = _transform.GetWorldPosition(xform) - prophetPos;
                distance = offset.Length();

                if (offset.LengthSquared() > 0.25f)
                    direction = Loc.GetString($"zzzz-fmt-direction-{offset.GetDir()}");
            }

            var canChastise = member.Bound
                              && distance != null
                              && status == VeiledHostMemberStatus.Alive
                              && now >= member.NextChastise;

            members.Add(new VeiledHostMember(GetNetEntity(uid),
                Name(uid),
                member.Bound,
                status,
                distance,
                direction,
                canChastise));
        }

        members.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return new VeiledHostEuiState(members);
    }

    /// <summary>
    ///     Stuns a bound member of the Host who has strayed from the Prophet.
    /// </summary>
    internal void TryChastise(EntityUid prophet, NetEntity netMember)
    {
        if (!TryComp<VeiledProphetComponent>(prophet, out var comp)
            || !TryGetEntity(netMember, out var found)
            || found is not { } uid
            || !TryComp<VeiledHostMemberComponent>(uid, out var member)
            || member.Prophet != prophet
            || !member.Bound)
            return;

        if (!_mobState.IsAlive(prophet))
            return;

        if (HasComp<PsionicallyNullifiedComponent>(prophet))
        {
            _popup.PopupEntity(Loc.GetString("religion-veiled-nullified"), prophet, prophet, PopupType.MediumCaution);
            return;
        }

        if (Transform(uid).MapID != Transform(prophet).MapID || !_mobState.IsAlive(uid))
        {
            _popup.PopupEntity(Loc.GetString("religion-veiled-chastise-out-of-reach"), prophet, prophet, PopupType.MediumCaution);
            return;
        }

        var now = _timing.CurTime;
        if (now < member.NextChastise)
            return;

        // Insulation picked up after the Binding still shuts the Mother out.
        if (TryComp<PsionicInsulationComponent>(uid, out var insulation) && !insulation.Passthrough)
        {
            _popup.PopupEntity(Loc.GetString("religion-veiled-bind-insulated"), prophet, prophet, PopupType.MediumCaution);
            return;
        }

        member.NextChastise = now + comp.ChastiseCooldown;
        _stun.TryParalyze(uid, comp.ChastiseStun, true);

        var message = Loc.GetString("religion-veiled-chastised", ("prophet", Name(prophet)));
        _popup.PopupEntity(message, uid, uid, PopupType.LargeCaution);

        if (TryComp<ActorComponent>(uid, out var actor))
        {
            var wrapped = $"[color={comp.BindingColor.ToHex()}][bold]{FormattedMessage.EscapeText(message)}[/bold][/color]";
            _chatManager.ChatMessageToOne(ChatChannel.Notifications,
                message,
                wrapped,
                EntityUid.Invalid,
                false,
                actor.PlayerSession.Channel);
        }

        _popup.PopupEntity(Loc.GetString("religion-veiled-chastise-prophet", ("target", uid)), prophet, prophet, PopupType.Medium);

        if (comp.ChastiseLines.Count > 0)
            _religion.SpeakRiteLine(prophet, _random.Pick(comp.ChastiseLines));

        RefreshHost(prophet);
    }

    #endregion
}
