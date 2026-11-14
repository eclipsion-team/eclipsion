using System.Numerics;
using Content.Server.ImmovableRod;
using Content.Server.Physics.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._Crescent.Misc;

// loose stuff in space never stops so it never sleeps, slow it down
public sealed class SpaceDebrisDragSystem : EntitySystem
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    private const float Interval = 0.5f;
    private const float Keep = 0.85f;
    private const float StopSpeed = 0.2f;

    private float _accumulator;
    private readonly List<Entity<PhysicsComponent>> _toSlow = new();

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<InputMoverComponent> _moverQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<ThrownItemComponent> _thrownQuery;
    private EntityQuery<PullableComponent> _pullableQuery;
    private EntityQuery<ImmovableRodComponent> _rodQuery;
    private EntityQuery<RandomWalkComponent> _walkQuery;
    private EntityQuery<ActorComponent> _actorQuery;
    private EntityQuery<ContainerManagerComponent> _containerQuery;

    public override void Initialize()
    {
        base.Initialize();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _moverQuery = GetEntityQuery<InputMoverComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _thrownQuery = GetEntityQuery<ThrownItemComponent>();
        _pullableQuery = GetEntityQuery<PullableComponent>();
        _rodQuery = GetEntityQuery<ImmovableRodComponent>();
        _walkQuery = GetEntityQuery<RandomWalkComponent>();
        _actorQuery = GetEntityQuery<ActorComponent>();
        _containerQuery = GetEntityQuery<ContainerManagerComponent>();
    }

    public override void Update(float frameTime)
    {
        _accumulator += frameTime;
        if (_accumulator < Interval)
            return;

        _accumulator -= Interval;

        // cant change velocity while looping AwakeBodies
        _toSlow.Clear();
        foreach (var ent in _physics.AwakeBodies)
        {
            var (uid, body, xform) = ent;
            if (body.BodyType != BodyType.Dynamic || xform.GridUid != null || xform.MapUid == null)
                continue;

            if (_gridQuery.HasComp(uid) || _mobQuery.HasComp(uid) || _moverQuery.HasComp(uid) ||
                _projectileQuery.HasComp(uid) || _thrownQuery.HasComp(uid))
                continue;

            // rods and singulo are meant to keep flying
            if (_rodQuery.HasComp(uid) || _walkQuery.HasComp(uid))
                continue;

            if (_pullableQuery.TryComp(uid, out var pullable) && pullable.BeingPulled)
                continue;

            // player stuffed in a locker/bodybag/mech keeps drifting too
            if (_actorQuery.HasComp(uid) || HasPlayerInside(uid))
                continue;

            _toSlow.Add((uid, body));
        }

        foreach (var (uid, body) in _toSlow)
        {
            if (TerminatingOrDeleted(uid))
                continue;

            var velocity = body.LinearVelocity * Keep;
            var angular = body.AngularVelocity * Keep;

            if (velocity.Length() < StopSpeed && MathF.Abs(angular) < StopSpeed)
            {
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
                _physics.SetAngularVelocity(uid, 0f, body: body);
                continue;
            }

            _physics.SetLinearVelocity(uid, velocity, body: body);
            _physics.SetAngularVelocity(uid, angular, body: body);
        }
    }

    private bool HasPlayerInside(EntityUid uid, int depth = 0)
    {
        if (depth > 3 || !_containerQuery.TryComp(uid, out var manager))
            return false;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var inside in container.ContainedEntities)
            {
                if (_actorQuery.HasComp(inside) || _mobQuery.HasComp(inside) || HasPlayerInside(inside, depth + 1))
                    return true;
            }
        }

        return false;
    }
}
