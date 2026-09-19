using Content.Shared.Actions;
using Content.Shared.CCVar;
using Content.Shared.Clothing;
using Content.Shared.Gravity;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Popups;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared.Movement.Systems;

public abstract class SharedJetpackSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeedModifier = default!;
    [Dependency] protected readonly SharedAppearanceSystem Appearance = default!;
    [Dependency] protected readonly SharedContainerSystem Container = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly ActionContainerSystem _actionContainer = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<JetpackComponent, GetItemActionsEvent>(OnJetpackGetAction);
        SubscribeLocalEvent<JetpackComponent, DroppedEvent>(OnJetpackDropped);
        SubscribeLocalEvent<JetpackComponent, ToggleJetpackEvent>(OnJetpackToggle);
        SubscribeLocalEvent<JetpackComponent, CanWeightlessMoveEvent>(OnJetpackCanWeightlessMove);

        SubscribeLocalEvent<JetpackUserComponent, CanWeightlessMoveEvent>(OnJetpackUserCanWeightless);
        SubscribeLocalEvent<JetpackUserComponent, EntParentChangedMessage>(OnJetpackUserEntParentChanged);
        SubscribeLocalEvent<JetpackUserComponent, MagbootsStateChangedEvent>(OnMagbootsStateChanged);

        SubscribeLocalEvent<GravityChangedEvent>(OnJetpackUserGravityChanged);
        SubscribeLocalEvent<JetpackComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, JetpackComponent component, MapInitEvent args)
    {
        _actionContainer.EnsureAction(uid, ref component.ToggleActionEntity, component.ToggleAction);
        Dirty(uid, component);
    }

    private void OnJetpackCanWeightlessMove(EntityUid uid, JetpackComponent component, ref CanWeightlessMoveEvent args)
    {
        args.CanMove = true;
    }

    private void OnJetpackUserGravityChanged(ref GravityChangedEvent ev)
    {
        var gridUid = ev.ChangedGridIndex;

        var query = EntityQueryEnumerator<JetpackUserComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var user, out var transform))
        {
            if (transform.GridUid != gridUid)
                continue;

            RefreshOrDisableUser(uid, user, transform);
        }
    }

    private void OnJetpackDropped(EntityUid uid, JetpackComponent component, DroppedEvent args)
    {
        SetEnabled(uid, component, false, args.User);
    }

    private void OnJetpackUserCanWeightless(EntityUid uid, JetpackUserComponent component, ref CanWeightlessMoveEvent args)
    {
        args.CanMove = true;
    }

    private void OnJetpackUserEntParentChanged(EntityUid uid, JetpackUserComponent component, ref EntParentChangedMessage args)
    {
        // The client re-parents predicted entities while resetting them during state application. Adding or removing
        // the relay components here then mutates the collection the engine is iterating ("Collection was modified").
        // The server state already carries the right components.
        if (_timing.ApplyingState)
            return;

        RefreshOrDisableUser(uid, component, args.Transform);
    }

    private void OnMagbootsStateChanged(Entity<JetpackUserComponent> ent, ref MagbootsStateChangedEvent args)
    {
        RefreshOrDisableUser(ent.Owner, ent.Comp);
    }

    private void SetupUser(EntityUid user, EntityUid jetpackUid)
    {
        var userComp = EnsureComp<JetpackUserComponent>(user);
        userComp.Jetpack = jetpackUid;
        RefreshUserMovement(user, userComp);
    }

    private void RemoveUser(EntityUid uid)
    {
        if (!RemComp<JetpackUserComponent>(uid))
            return;

        if (TryComp<PhysicsComponent>(uid, out var physics))
            _physics.SetBodyStatus(uid, physics, BodyStatus.OnGround);

        RemComp<RelayInputMoverComponent>(uid);
    }

    private void RefreshUserMovement(
        EntityUid user,
        JetpackUserComponent? component = null,
        TransformComponent? xform = null)
    {
        if (!Resolve(user, ref component, false))
            return;

        var suppressed = IsMagbootsGrounded(user, xform);
        if (suppressed)
        {
            if (TryComp<RelayInputMoverComponent>(user, out var relay) &&
                relay.RelayEntity == component.Jetpack)
            {
                RemComp(user, relay);
            }
        }
        else
        {
            _mover.SetRelay(user, component.Jetpack);
        }

        if (TryComp<PhysicsComponent>(user, out var physics))
        {
            _physics.SetBodyStatus(user,
                physics,
                suppressed ? BodyStatus.OnGround : BodyStatus.InAir);
        }
    }

    private void RefreshOrDisableUser(
        EntityUid user,
        JetpackUserComponent component,
        TransformComponent? xform = null)
    {
        xform ??= Transform(user);
        if (CanEnableOnGrid(xform.GridUid) || IsMagbootsGrounded(user, xform))
        {
            RefreshUserMovement(user, component, xform);
            return;
        }

        if (!TryComp<JetpackComponent>(component.Jetpack, out var jetpack))
            return;

        _popup.PopupClient(Loc.GetString("jetpack-to-grid"), user, user);
        SetEnabled(component.Jetpack, jetpack, false, user);
    }

    private bool IsMagbootsGrounded(EntityUid user, TransformComponent? xform = null)
    {
        var onSupportingGrid = _gravity.EntityOnGravitySupportingGridOrMap((user, xform));
        var enumerator = _inventory.GetSlotEnumerator(user);

        while (enumerator.NextItem(out var item, out var slot))
        {
            if (!TryComp<MagbootsComponent>(item, out var magboots) ||
                !magboots.Active ||
                magboots.Slot != slot.Name)
            {
                continue;
            }

            if (!magboots.RequiresGrid || onSupportingGrid)
                return true;
        }

        return false;
    }

    private void OnJetpackToggle(EntityUid uid, JetpackComponent component, ToggleJetpackEvent args)
    {
        if (args.Handled)
            return;

        if (TryComp<TransformComponent>(uid, out var xform) &&
            !CanEnableOnGrid(xform.GridUid) &&
            !IsMagbootsGrounded(args.Performer))
        {
            _popup.PopupClient(Loc.GetString("jetpack-no-station"), uid, args.Performer);

            return;
        }

        SetEnabled(uid, component, !IsEnabled(uid));
    }

    private bool CanEnableOnGrid(EntityUid? gridUid)
    {
        return _config.GetCVar(CCVars.JetpackEnableAnywhere) || gridUid == null || _config.GetCVar(CCVars.JetpackEnableInNoGravity) && TryComp<GravityComponent>(gridUid, out var comp) && !comp.Enabled;
    }

    private void OnJetpackGetAction(EntityUid uid, JetpackComponent component, GetItemActionsEvent args)
    {
        args.AddAction(ref component.ToggleActionEntity, component.ToggleAction);
    }

    private bool IsEnabled(EntityUid uid)
    {
        return HasComp<ActiveJetpackComponent>(uid);
    }

    public void SetEnabled(EntityUid uid, JetpackComponent component, bool enabled, EntityUid? user = null)
    {
        if (IsEnabled(uid) == enabled ||
            enabled && !CanEnable(uid, component))
        {
            return;
        }

        if (user == null)
        {
            Container.TryGetContainingContainer((uid, null, null), out var container);
            user = container?.Owner;
        }

        // A jetpack needs an available wearer before it can be enabled.
        if (enabled && (user == null || HasComp<JetpackUserComponent>(user.Value)))
            return;

        if (enabled)
            EnsureComp<ActiveJetpackComponent>(uid);
        else
            RemComp<ActiveJetpackComponent>(uid);

        if (user != null)
        {
            if (enabled)
            {
                SetupUser(user.Value, uid);
            }
            else
            {
                RemoveUser(user.Value);
            }

            _movementSpeedModifier.RefreshMovementSpeedModifiers(user.Value);
        }

        Appearance.SetData(uid, JetpackVisuals.Enabled, enabled);
        Dirty(uid, component);
    }

    public bool IsUserFlying(EntityUid uid)
    {
        return HasComp<JetpackUserComponent>(uid);
    }

    /// <summary>
    /// Returns whether an enabled jetpack is currently providing thrust rather than
    /// waiting for its user's magboots to release from a grid.
    /// </summary>
    public bool IsProvidingThrust(EntityUid uid)
    {
        if (!Container.TryGetContainingContainer((uid, null, null), out var container) ||
            !TryComp<JetpackUserComponent>(container.Owner, out var user) ||
            user.Jetpack != uid ||
            !TryComp<RelayInputMoverComponent>(container.Owner, out var relay))
        {
            return false;
        }

        return relay.RelayEntity == uid;
    }

    protected virtual bool CanEnable(EntityUid uid, JetpackComponent component)
    {
        return true;
    }
}

[Serializable, NetSerializable]
public enum JetpackVisuals : byte
{
    Enabled,
}
