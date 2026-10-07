using Content.Shared._Crescent.DegradeableArmor; // Crescent
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;

namespace Content.Shared.Blocking;

public sealed partial class BlockingSystem
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly DegradeableArmorSystem _degradeableArmor = default!; // Crescent

    private void InitializeUser()
    {
        SubscribeLocalEvent<BlockingUserComponent, DamageModifyEvent>(OnUserDamageModified);
        SubscribeLocalEvent<BlockingComponent, DamageModifyEvent>(OnDamageModified);

        SubscribeLocalEvent<BlockingUserComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<BlockingUserComponent, ContainerGettingInsertedAttemptEvent>(OnInsertAttempt);
        SubscribeLocalEvent<BlockingUserComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<BlockingUserComponent, EntityTerminatingEvent>(OnEntityTerminating);
    }

    private void OnParentChanged(EntityUid uid, BlockingUserComponent component, ref EntParentChangedMessage args)
    {
        UserStopBlocking(uid, component);
    }

    private void OnInsertAttempt(
        EntityUid uid,
        BlockingUserComponent component,
        ContainerGettingInsertedAttemptEvent args
    )
    {
        UserStopBlocking(uid, component);
    }

    private void OnAnchorChanged(EntityUid uid, BlockingUserComponent component, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
            return;

        UserStopBlocking(uid, component);
    }

    private void OnUserDamageModified(EntityUid uid, BlockingUserComponent component, DamageModifyEvent args)
    {
        if (!TryComp<BlockingComponent>(component.BlockingItem, out var blocking) || args.Damage.GetTotal() <= 0)
            return;

        // Crescent: shields plated with degradeable armor soak hits with it instead of taking damage themselves.
        TryComp<DegradeableArmorComponent>(component.BlockingItem, out var armor);

        // A shield should only block damage it can itself absorb. To determine that we need the Damageable component on it.
        DamageableComponent? dmgComp = null;
        if (armor == null && !TryComp(component.BlockingItem, out dmgComp))
            return;

        if (!_toggle.IsActivated(component.BlockingItem.Value)) // Goobstation
            return;

        var ev = new BeforeBlockingEvent(uid, args.Origin);
        RaiseLocalEvent(component.BlockingItem.Value, ev);
        if (ev.Cancelled)
            return;

        var blockFraction = blocking.IsBlocking ? blocking.ActiveBlockFraction : blocking.PassiveBlockFraction;
        blockFraction = Math.Clamp(blockFraction, 0, 1);

        // Crescent: the block fraction is how much of each hit the plating catches.
        if (armor != null)
        {
            if (_degradeableArmor.AbsorbDamage(component.BlockingItem.Value, armor, args, uid, blockFraction) && blocking.IsBlocking)
                _audio.PlayPvs(blocking.BlockSound, uid);
            return;
        }

        _damageable.TryChangeDamage(component.BlockingItem, blockFraction * args.OriginalDamage);

        var modify = new DamageModifierSet();
        foreach (var key in dmgComp!.Damage.DamageDict.Keys)
            modify.Coefficients.TryAdd(key, 1 - blockFraction);

        args.Damage = DamageSpecifier.ApplyModifierSet(args.Damage, modify);
        if (blocking.IsBlocking && !args.Damage.Equals(args.OriginalDamage))
            _audio.PlayPvs(blocking.BlockSound, uid);
    }

    private void OnDamageModified(EntityUid uid, BlockingComponent component, DamageModifyEvent args)
    {
        var modifier = component.IsBlocking ? component.ActiveBlockDamageModifier : component.PassiveBlockDamageModifer;
        if (modifier == null)
        {
            return;
        }

        args.Damage = DamageSpecifier.ApplyModifierSet(args.Damage, modifier);
    }

    private void OnEntityTerminating(EntityUid uid, BlockingUserComponent component, ref EntityTerminatingEvent args)
    {
        if (!TryComp<BlockingComponent>(component.BlockingItem, out var blockingComponent))
            return;

        StopBlockingHelper(component.BlockingItem.Value, blockingComponent, uid);
    }

    /// <summary>
    /// Check for the shield and has the user stop blocking
    /// Used where you'd like the user to stop blocking, but also don't want to remove the <see cref="BlockingUserComponent"/>
    /// </summary>
    /// <param name="uid">The user blocking</param>
    /// <param name="component">The <see cref="BlockingUserComponent"/></param>
    private void UserStopBlocking(EntityUid uid, BlockingUserComponent component)
    {
        if (TryComp<BlockingComponent>(component.BlockingItem, out var blockComp) && blockComp.IsBlocking)
            StopBlocking(component.BlockingItem.Value, blockComp, uid);
    }
}
