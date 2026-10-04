using System.Linq;
using System.Numerics;
using Content.Server.Abilities.Psionics;
using Content.Server.Chat.Managers;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions;
using Content.Shared.Chat;
using Content.Shared.Crescent.Psionics;
using Content.Shared.DoAfter;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Psionics;

/// <summary>
/// The Saint's Militia's answer to psions. Hunters (<see cref="PsionHunterComponent"/>) are never psionic and find
/// psions with Hunter's Sense and Scrutiny, which run on training rather than the noosphere: no psionic component, no
/// glimmer, nothing a null field or insulation on the hunter's side would stop. Hunt leaders also carry a
/// <see cref="NullWardComponent"/> and can hold a null field for a while.
/// </summary>
public sealed class PsionHunterSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PsionicNullifierSystem _nullifier = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly PsionicAbilitiesSystem _psionicAbilities = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// Hunters who picked up psionics since the last tick. Stripped in <see cref="Update"/> rather than on the spot:
    /// the psionic component is still starting up when we hear of it, and a trait may hand out its powers right after.
    /// </summary>
    private readonly HashSet<EntityUid> _toStrip = new();

    private readonly HashSet<Entity<PsionicComponent>> _psions = new();
    private readonly List<Entity<NullWardComponent>> _expired = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PsionHunterComponent, ComponentStartup>(OnHunterStartup);
        SubscribeLocalEvent<PsionHunterComponent, ComponentShutdown>(OnHunterShutdown);
        SubscribeLocalEvent<PsionHunterComponent, PsionHunterSenseActionEvent>(OnSense);
        SubscribeLocalEvent<PsionHunterComponent, PsionHunterScrutinyActionEvent>(OnScrutiny);
        SubscribeLocalEvent<PsionHunterComponent, PsionHunterScrutinyDoAfterEvent>(OnScrutinyDoAfter);
        // ComponentInit, not Startup: PsionicsSystem already holds PsionicComponent's Startup.
        SubscribeLocalEvent<PsionicComponent, ComponentInit>(OnPsionicInit);

        SubscribeLocalEvent<NullWardComponent, ComponentStartup>(OnWardStartup);
        SubscribeLocalEvent<NullWardComponent, ComponentShutdown>(OnWardShutdown);
        SubscribeLocalEvent<NullWardComponent, NullWardActionEvent>(OnWard);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _toStrip.Clear());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_toStrip.Count > 0)
        {
            var hunters = _toStrip.ToList();
            _toStrip.Clear();

            foreach (var hunter in hunters)
                StripPsionics(hunter);
        }

        var now = _timing.CurTime;
        _expired.Clear();

        var wards = EntityQueryEnumerator<NullWardComponent>();
        while (wards.MoveNext(out var uid, out var ward))
        {
            if (ward.ActiveUntil is { } until && until <= now)
                _expired.Add((uid, ward));
        }

        foreach (var ward in _expired)
            EndWard(ward, true);
    }

    #region Hunter

    private void OnHunterStartup(Entity<PsionHunterComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent, ref ent.Comp.SenseActionEntity, ent.Comp.SenseAction);
        _actions.AddAction(ent, ref ent.Comp.ScrutinyActionEntity, ent.Comp.ScrutinyAction);

        if (HasComp<PsionicComponent>(ent))
            _toStrip.Add(ent);
    }

    private void OnHunterShutdown(Entity<PsionHunterComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent, ent.Comp.SenseActionEntity);
        _actions.RemoveAction(ent, ent.Comp.ScrutinyActionEntity);
    }

    private void OnPsionicInit(Entity<PsionicComponent> ent, ref ComponentInit args)
    {
        if (HasComp<PsionHunterComponent>(ent))
            _toStrip.Add(ent);
    }

    /// <summary>
    /// Takes every psionic power off a hunter, and the psionic component with them. Done the way a mindbreak does
    /// it, without the mindbreak: the hunter is simply not a psion.
    /// </summary>
    private void StripPsionics(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid)
            || !HasComp<PsionHunterComponent>(uid)
            || !TryComp<PsionicComponent>(uid, out var psionic))
            return;

        // Some caster traits make their psion irremovable. A hunter is a hunter first.
        psionic.Removable = true;
        _psionicAbilities.RemoveAllPsionicPowers(uid);
        RemComp<InnatePsionicPowersComponent>(uid);
        RemComp(uid, psionic);

        if (_player.TryGetSessionByEntity(uid, out var session))
            SendToChat(session, Loc.GetString("psion-hunter-psionics-stripped"), Color.Orange);
    }

    private void OnSense(Entity<PsionHunterComponent> ent, ref PsionHunterSenseActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        var xform = Transform(ent);
        var origin = _transform.GetWorldPosition(xform);

        var count = 0;
        var nearest = Vector2.Zero;
        var nearestDistance = float.MaxValue;

        _psions.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(ent, xform), ent.Comp.SenseRange, _psions);
        foreach (var psion in _psions)
        {
            if (!CanSense(ent, psion))
                continue;

            var delta = _transform.GetWorldPosition(psion) - origin;
            var distance = delta.Length();
            count++;

            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = delta;
        }

        string message;
        if (count == 0)
        {
            message = Loc.GetString("psion-hunter-sense-none");
        }
        else
        {
            // Read off in the frame the hunter sees, so "north" on a turned ship is still up their screen.
            var gridRotation = xform.GridUid is { } grid ? _transform.GetWorldRotation(grid) : Angle.Zero;
            var direction = nearestDistance < 0.5f
                ? "here"
                : (-gridRotation).RotateVec(nearest).GetDir().ToString().ToLowerInvariant();

            message = Loc.GetString("psion-hunter-sense-found",
                ("count", count),
                ("direction", Loc.GetString($"psion-hunter-direction-{direction}")));

            if (nearestDistance <= ent.Comp.SenseCloseRange)
                message += " " + Loc.GetString("psion-hunter-sense-close");
        }

        _popup.PopupEntity(message, ent, ent, count > 0 ? PopupType.LargeCaution : PopupType.Medium);
        if (_player.TryGetSessionByEntity(ent, out var session))
            SendToChat(session, message, count > 0 ? Color.OrangeRed : Color.LightGray);
    }

    /// <summary>
    /// Living psions only, and only those not hiding behind insulation: the same psions a metapsionic pulse finds.
    /// </summary>
    private bool CanSense(EntityUid hunter, Entity<PsionicComponent> psion)
    {
        if (psion.Owner == hunter
            || !HasComp<MobStateComponent>(psion)
            || _mobState.IsDead(psion))
            return false;

        return !TryComp<PsionicInsulationComponent>(psion, out var insulation) || insulation.Passthrough;
    }

    private void OnScrutiny(Entity<PsionHunterComponent> ent, ref PsionHunterScrutinyActionEvent args)
    {
        if (args.Handled || args.Target == ent.Owner)
            return;

        var doAfter = new DoAfterArgs(EntityManager,
            ent,
            ent.Comp.ScrutinyDelay,
            new PsionHunterScrutinyDoAfterEvent(),
            ent,
            target: args.Target)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            BlockDuplicate = true,
            NeedHand = false,
            DistanceThreshold = 2f,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return;

        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("psion-hunter-scrutiny-start", ("target", args.Target)), ent, ent);
    }

    private void OnScrutinyDoAfter(Entity<PsionHunterComponent> ent, ref PsionHunterScrutinyDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } target || TerminatingOrDeleted(target))
            return;

        args.Handled = true;

        var psion = TryComp<PsionicComponent>(target, out _)
                    && (!TryComp<PsionicInsulationComponent>(target, out var insulation) || insulation.Passthrough);

        string locale;
        if (psion)
            locale = "psion-hunter-scrutiny-psion";
        else if (HasComp<MindbrokenComponent>(target))
            locale = "psion-hunter-scrutiny-mindbroken";
        else
            locale = "psion-hunter-scrutiny-clean";

        var message = Loc.GetString(locale, ("target", target));
        _popup.PopupEntity(message, ent, ent, psion ? PopupType.LargeCaution : PopupType.Medium);

        if (_player.TryGetSessionByEntity(ent, out var session))
            SendToChat(session, message, psion ? Color.OrangeRed : Color.LightGray);
    }

    #endregion

    #region Null Ward

    private void OnWardStartup(Entity<NullWardComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent, ref ent.Comp.ActionEntity, ent.Comp.Action);
    }

    private void OnWardShutdown(Entity<NullWardComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent, ent.Comp.ActionEntity);
        EndWard(ent, false);
    }

    private void OnWard(Entity<NullWardComponent> ent, ref NullWardActionEvent args)
    {
        if (args.Handled || !_mobState.IsAlive(ent))
            return;

        // A field already up, from the ward, a trait or a nullifier helmet, is not ours to stack on or take down.
        // Left unhandled so the ward does not go on cooldown for nothing.
        if (HasComp<PsionicNullifierComponent>(ent))
        {
            _popup.PopupEntity(Loc.GetString("psion-hunter-ward-already"), ent, ent);
            return;
        }

        args.Handled = true;

        var field = AddComp<PsionicNullifierComponent>(ent);
        field.Range = ent.Comp.Range;
        ent.Comp.ActiveUntil = _timing.CurTime + ent.Comp.Duration;

        _popup.PopupEntity(Loc.GetString("psion-hunter-ward-start"), ent, ent, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("psion-hunter-ward-start-others", ("user", ent.Owner)),
            ent,
            Filter.PvsExcept(ent),
            true,
            PopupType.MediumCaution);
    }

    private void EndWard(Entity<NullWardComponent> ent, bool announce)
    {
        if (ent.Comp.ActiveUntil == null)
            return;

        ent.Comp.ActiveUntil = null;

        if (TerminatingOrDeleted(ent))
            return;

        // Only the ward's own field: gear put on meanwhile found it there and granted none of its own.
        if (TryComp<PsionicNullifierComponent>(ent, out var field) && !field.FromClothing)
            RemComp(ent, field);

        _nullifier.RestoreWornField(ent);

        if (announce)
            _popup.PopupEntity(Loc.GetString("psion-hunter-ward-end"), ent, ent, PopupType.Medium);
    }

    #endregion

    private void SendToChat(ICommonSession session, string message, Color color)
    {
        // Escaped: the message carries character names, which must not be read as markup.
        _chat.ChatMessageToOne(ChatChannel.Emotes,
            message,
            FormattedMessage.EscapeText(message),
            EntityUid.Invalid,
            false,
            session.Channel,
            color);
    }
}
