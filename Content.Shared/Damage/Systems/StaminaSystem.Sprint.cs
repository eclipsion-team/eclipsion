using Content.Shared.Damage.Components;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;

namespace Content.Shared.Damage.Systems;

public sealed partial class StaminaSystem
{
    [Dependency] private readonly SharedGravitySystem _gravity = default!;

    /// <summary>
    /// How much stamina is drained per second while sprinting.
    /// </summary>
    private const float SprintStaminaCost = 5f;

    private void InitializeSprint()
    {
        SubscribeLocalEvent<StaminaComponent, MoveInputEvent>(OnSprintMoveInput);
    }

    private void OnSprintMoveInput(Entity<StaminaComponent> entity, ref MoveInputEvent args)
    {
        var sprinting = args.Entity.Comp.Sprinting && args.HasDirectionalMovement;

        // Sprinting without traction uses walk speed and should not drain stamina.
        SetSprintDrain(entity, sprinting && _gravity.HasTraction(entity.Owner));
    }

    /// <summary>
    /// Updates the sprint drain when traction changes without new movement input.
    /// </summary>
    private void UpdateSprint()
    {
        var query = EntityQueryEnumerator<StaminaComponent, InputMoverComponent>();
        while (query.MoveNext(out var uid, out var stamina, out var mover))
        {
            var sprinting = mover.Sprinting &&
                            (mover.HeldMoveButtons & MoveButtons.AnyDirection) != MoveButtons.None;

            if (!sprinting && !stamina.SprintDraining)
                continue;

            SetSprintDrain((uid, stamina), sprinting && _gravity.HasTraction(uid));
        }
    }

    /// <summary>
    /// Registers or clears the sprint stamina drain.
    /// </summary>
    private void SetSprintDrain(Entity<StaminaComponent> entity, bool enabled)
    {
        // Done before the early-out so UpdateSprint re-adds the component if something
        // else removed it (rejuvenate, leaving stam crit) while the drain stayed on.
        if (enabled)
            EnsureComp<ActiveStaminaComponent>(entity);

        if (entity.Comp.SprintDraining == enabled)
            return;

        entity.Comp.SprintDraining = enabled;
    }
}
