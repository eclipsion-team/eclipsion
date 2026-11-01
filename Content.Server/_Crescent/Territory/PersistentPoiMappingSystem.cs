using Content.Server.Popups;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Territory;
using Content.Shared.ActionBlocker;
using Content.Shared.Administration.Logs;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiMappingSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly PersistentPoiBuildSystem _build = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);
    private const float HeavyHit = 10f;

    private TimeSpan _nextCheck;
    private readonly List<EntityUid> _stopping = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiMappingConsoleComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
        SubscribeLocalEvent<PersistentPoiMappingConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<PersistentPoiMappingConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<PersistentPoiMappingConsoleComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<PersistentPoiMappingConsoleComponent, EntityTerminatingEvent>(OnConsoleTerminating);

        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, DamageChangedEvent>(OnViewerDamaged);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, MobStateChangedEvent>(OnViewerMobState);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, PlayerDetachedEvent>(OnViewerDetached);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, ComponentShutdown>(OnViewerShutdown);

        Subs.BuiEvents<PersistentPoiMappingConsoleComponent>(PersistentPoiMappingUiKey.Key, subs =>
        {
            subs.Event<PersistentPoiMappingOpenMenuMessage>(OnOpenMenu);
            subs.Event<PersistentPoiMappingRecenterMessage>(OnRecenter);
        });

        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _nextCheck = TimeSpan.Zero);
    }

    private void OnOpenAttempt(Entity<PersistentPoiMappingConsoleComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (!_power.IsPowered(ent))
        {
            args.Cancel();
            _popup.PopupEntity(Loc.GetString("persistent-poi-mapping-unpowered"), ent, args.User, PopupType.SmallCaution);
            return;
        }

        // someone already riding a relay (a jetpack, a mech) would lose it to the view
        if (HasComp<RelayInputMoverComponent>(args.User) || !_build.CanBuild(args.User))
        {
            args.Cancel();
            _popup.PopupEntity(Loc.GetString("persistent-poi-mapping-cannot"), ent, args.User, PopupType.SmallCaution);
        }
    }

    private void OnUiOpened(Entity<PersistentPoiMappingConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!Equals(args.UiKey, PersistentPoiMappingUiKey.Key))
            return;

        if (!StartViewing(ent, args.Actor))
            _ui.CloseUi(ent.Owner, PersistentPoiMappingUiKey.Key, args.Actor);
    }

    private void OnUiClosed(Entity<PersistentPoiMappingConsoleComponent> ent, ref BoundUIClosedEvent args)
    {
        if (Equals(args.UiKey, PersistentPoiMappingUiKey.Key) &&
            TryComp<PersistentPoiMappingViewerComponent>(args.Actor, out var viewer) &&
            viewer.Console == ent.Owner)
        {
            StopViewing(args.Actor);
        }
    }

    private void OnPowerChanged(Entity<PersistentPoiMappingConsoleComponent> ent, ref PowerChangedEvent args)
    {
        if (!args.Powered)
            _ui.CloseUi(ent.Owner, PersistentPoiMappingUiKey.Key);
    }

    private void OnConsoleTerminating(Entity<PersistentPoiMappingConsoleComponent> ent, ref EntityTerminatingEvent args)
    {
        _stopping.Clear();
        var viewers = EntityQueryEnumerator<PersistentPoiMappingViewerComponent>();
        while (viewers.MoveNext(out var uid, out var viewer))
        {
            if (viewer.Console == ent.Owner)
                _stopping.Add(uid);
        }

        foreach (var uid in _stopping)
        {
            StopViewing(uid);
        }
    }

    private void OnViewerDamaged(Entity<PersistentPoiMappingViewerComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta is not { } delta)
            return;

        // a blow or a shot throws you out; bleeding, cold or thin air ticking away does not
        var total = delta.GetTotal();
        if (total <= 0 || args.Origin == null && total < HeavyHit)
            return;

        _popup.PopupEntity(Loc.GetString("persistent-poi-mapping-jolted"), ent, ent, PopupType.MediumCaution);
        StopViewing(ent);
    }

    private void OnViewerMobState(Entity<PersistentPoiMappingViewerComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Alive)
            StopViewing(ent);
    }

    private void OnViewerDetached(Entity<PersistentPoiMappingViewerComponent> ent, ref PlayerDetachedEvent args)
    {
        StopViewing(ent);
    }

    // covers every way the component can go, including the body being deleted
    private void OnViewerShutdown(Entity<PersistentPoiMappingViewerComponent> ent, ref ComponentShutdown args)
    {
        Release(ent.Owner, ent.Comp);
    }

    private void OnOpenMenu(Entity<PersistentPoiMappingConsoleComponent> ent, ref PersistentPoiMappingOpenMenuMessage args)
    {
        if (!IsViewingFrom(args.Actor, ent))
            return;

        if (!_ui.HasUi(args.Actor, PersistentPoiBuildUiKey.Key))
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-build-not-held"), args.Actor, args.Actor, PopupType.SmallCaution);
            return;
        }

        _ui.OpenUi(args.Actor, PersistentPoiBuildUiKey.Key, args.Actor);
    }

    private void OnRecenter(Entity<PersistentPoiMappingConsoleComponent> ent, ref PersistentPoiMappingRecenterMessage args)
    {
        if (TryComp<PersistentPoiMappingViewerComponent>(args.Actor, out var viewer) && viewer.Console == ent.Owner)
            _transform.SetCoordinates(viewer.Eye, Transform(ent).Coordinates);
    }

    private bool IsViewingFrom(EntityUid user, EntityUid console)
    {
        return TryComp<PersistentPoiMappingViewerComponent>(user, out var viewer) && viewer.Console == console;
    }

    public bool StartViewing(Entity<PersistentPoiMappingConsoleComponent> console, EntityUid user)
    {
        if (HasComp<PersistentPoiMappingViewerComponent>(user))
            StopViewing(user);

        var consoleXform = Transform(console);
        if (consoleXform.GridUid is not { } grid ||
            !HasComp<PersistentPoiGridComponent>(grid) ||
            Transform(user).GridUid != grid ||
            !_build.CanBuild(user) ||
            HasComp<RelayInputMoverComponent>(user) ||
            !TryComp<EyeComponent>(user, out var eye))
        {
            return false;
        }

        var view = SpawnAttachedTo(console.Comp.EyePrototype, consoleXform.Coordinates);

        var viewer = EnsureComp<PersistentPoiMappingViewerComponent>(user);
        viewer.Console = console;
        viewer.Eye = view;
        viewer.HadFov = eye.DrawFov;
        Dirty(user, viewer);

        _eye.SetTarget(user, view, eye);
        _eye.SetDrawFov(user, false, eye);
        _mover.SetRelay(user, view);

        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(user):user} started working territory {ToPrettyString(grid)} from {ToPrettyString(console):console}");
        return true;
    }

    public void StopViewing(EntityUid user)
    {
        if (!TryComp<PersistentPoiMappingViewerComponent>(user, out var viewer))
            return;

        var console = viewer.Console;
        RemComp(user, viewer);

        if (!TerminatingOrDeleted(console))
            _ui.CloseUi(console, PersistentPoiMappingUiKey.Key, user);
    }

    private void Release(EntityUid user, PersistentPoiMappingViewerComponent viewer)
    {
        if (!TerminatingOrDeleted(user) && TryComp<RelayInputMoverComponent>(user, out var relay) && relay.RelayEntity == viewer.Eye)
            RemComp(user, relay);

        if (!TerminatingOrDeleted(user) && TryComp<EyeComponent>(user, out var eye))
        {
            if (eye.Target == viewer.Eye)
                _eye.SetTarget(user, null, eye);

            _eye.SetDrawFov(user, viewer.HadFov, eye);
        }

        if (!TerminatingOrDeleted(viewer.Eye))
            QueueDel(viewer.Eye);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;
        _stopping.Clear();

        var viewers = EntityQueryEnumerator<PersistentPoiMappingViewerComponent>();
        while (viewers.MoveNext(out var uid, out var viewer))
        {
            if (TerminatingOrDeleted(viewer.Eye) ||
                TerminatingOrDeleted(viewer.Console) ||
                !_build.CanBuild(uid) ||
                Transform(uid).GridUid != Transform(viewer.Console).GridUid ||
                !_power.IsPowered(viewer.Console) ||
                !_blocker.CanInteract(uid, viewer.Console) ||
                HasComp<ActorComponent>(uid) && !_ui.IsUiOpen(viewer.Console, PersistentPoiMappingUiKey.Key, uid))
            {
                _stopping.Add(uid);
            }
        }

        foreach (var uid in _stopping)
        {
            StopViewing(uid);
        }
    }
}
