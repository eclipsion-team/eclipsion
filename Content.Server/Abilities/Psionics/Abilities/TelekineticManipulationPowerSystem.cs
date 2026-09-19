using Content.Shared.Crescent.Psionics;
using Content.Server.Popups;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Map.Components;

namespace Content.Server.Abilities.Psionics;

/// <summary>
/// Two-stage remote object manipulation: select an unanchored object, then throw it toward a world target.
/// </summary>
public sealed class TelekineticManipulationPowerSystem : EntitySystem
{
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedPsionicAbilitiesSystem _psionics = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TelekineticManipulationComponent, SelectTelekineticObjectActionEvent>(OnSelect);
        SubscribeLocalEvent<TelekineticManipulationComponent, MoveTelekineticObjectActionEvent>(OnMove);
    }

    private void OnSelect(
        Entity<TelekineticManipulationComponent> ent,
        ref SelectTelekineticObjectActionEvent args)
    {
        if (args.Handled
            || args.Target == ent.Owner
            || HasComp<MobStateComponent>(args.Target)
            || HasComp<MapGridComponent>(args.Target)
            || !TryComp<PhysicsComponent>(args.Target, out var physics)
            || Transform(args.Target).Anchored
            || (physics.BodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0
            || physics.Mass > ent.Comp.MaximumMass
            || !_transform.GetMapCoordinates(ent).InRange(
                _transform.GetMapCoordinates(args.Target),
                ent.Comp.MaximumRange))
            return;

        ent.Comp.SelectedObject = args.Target;
        _popup.PopupEntity(
            Loc.GetString("telekinetic-manipulation-selected", ("target", args.Target)),
            ent,
            ent);
        args.Handled = true;
    }

    private void OnMove(
        Entity<TelekineticManipulationComponent> ent,
        ref MoveTelekineticObjectActionEvent args)
    {
        if (args.Handled
            || ent.Comp.SelectedObject is not { } selected
            || !Exists(selected)
            || !TryComp<PhysicsComponent>(selected, out var physics)
            || Transform(selected).Anchored
            || physics.Mass > ent.Comp.MaximumMass
            || !_transform.GetMapCoordinates(ent).InRange(
                _transform.GetMapCoordinates(selected),
                ent.Comp.MaximumRange)
            || !_psionics.OnAttemptPowerUse(args.Performer, "telekinetic manipulation", true))
            return;

        _throwing.TryThrow(
            selected,
            args.Target,
            baseThrowSpeed: 8f,
            user: ent,
            pushbackRatio: 0f,
            compensateFriction: true,
            recoil: false);

        // Marked so a null field can tell this apart from a hand-thrown object and drop it short.
        EnsureComp<PsionicManifestationComponent>(selected).DeleteOnNullify = false;

        ent.Comp.SelectedObject = null;
        _psionics.LogPowerUsed(ent, "telekinetic manipulation", 3, 5);
        args.Handled = true;
    }
}
