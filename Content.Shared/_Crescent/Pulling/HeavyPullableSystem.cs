using Content.Shared.Mech.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Pulling.Events;
using Robust.Shared.Containers;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Crescent.Pulling;

/// <summary>
/// Makes <see cref="HeavyPullableComponent"/> entities draggable despite their mass, see the component for why.
/// Shared so the puller's movement and the pull joint predict the same way on client and server.
/// </summary>
public sealed class HeavyPullableSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly FixtureSystem _fixtures = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HeavyPullableComponent, BeingPulledAttemptEvent>(OnBeingPulledAttempt);
        SubscribeLocalEvent<HeavyPullableComponent, PullStartedMessage>(OnPullStarted);
        SubscribeLocalEvent<HeavyPullableComponent, PullStoppedMessage>(OnPullStopped);
        SubscribeLocalEvent<HeavyPullableComponent, GetPullingSpeedModifiersEvent>(OnGetPullingSpeedModifiers);
        SubscribeLocalEvent<HeavyPullableComponent, EntInsertedIntoContainerMessage>(OnInserted);
    }

    private void OnBeingPulledAttempt(EntityUid uid, HeavyPullableComponent comp, BeingPulledAttemptEvent args)
    {
        if (args.Cancelled || comp.PullableWhilePiloted)
            return;

        if (TryComp<MechComponent>(uid, out var mech) && mech.PilotSlot.ContainedEntity != null)
            args.Cancel();
    }

    private void OnInserted(EntityUid uid, HeavyPullableComponent comp, EntInsertedIntoContainerMessage args)
    {
        // Somebody climbed in mid-drag: let go, the pilot is driving now.
        if (comp.PullableWhilePiloted
            || !TryComp<MechComponent>(uid, out var mech)
            || args.Container.ID != mech.PilotSlotId
            || !TryComp<PullableComponent>(uid, out var pullable)
            || !pullable.BeingPulled)
            return;

        _pulling.TryStopPull(uid, pullable, ignoreGrab: true);
    }

    private void OnPullStarted(EntityUid uid, HeavyPullableComponent comp, PullStartedMessage args)
    {
        // Also raised on us when a piloted mech is the one doing the pulling.
        if (args.PulledUid != uid || _timing.ApplyingState || comp.AppliedDensityScale != 1f)
            return;

        if (!TryComp<PhysicsComponent>(uid, out var body) || body.Mass <= comp.DragMass)
            return;

        var scale = comp.DragMass / body.Mass;
        ScaleDensity(uid, scale);
        comp.AppliedDensityScale = scale;
        Dirty(uid, comp);
    }

    private void OnPullStopped(EntityUid uid, HeavyPullableComponent comp, PullStoppedMessage args)
    {
        if (args.PulledUid != uid || _timing.ApplyingState || comp.AppliedDensityScale == 1f)
            return;

        ScaleDensity(uid, 1f / comp.AppliedDensityScale);
        comp.AppliedDensityScale = 1f;
        Dirty(uid, comp);
    }

    private void OnGetPullingSpeedModifiers(Entity<HeavyPullableComponent> ent, ref GetPullingSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.DragWalkSpeedMultiplier, ent.Comp.DragSprintSpeedMultiplier);
    }

    private void ScaleDensity(EntityUid uid, float scale)
    {
        if (!TryComp<FixturesComponent>(uid, out var manager))
            return;

        foreach (var (id, fixture) in manager.Fixtures)
        {
            _physics.SetDensity(uid, id, fixture, fixture.Density * scale, update: false, manager: manager);
        }

        _fixtures.FixtureUpdate(uid, manager: manager);
    }
}
