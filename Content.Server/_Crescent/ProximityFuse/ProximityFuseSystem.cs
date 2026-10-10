using System.Numerics;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Map;

namespace Content.Server._Crescent.ProximityFuse;

public sealed class ProximityFuseSystem : EntitySystem
{
    private const float DiscoveryInterval = 0.08f;

    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedShuttleSystem _shuttle = default!;

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;

    private readonly List<EntityUid> _keysScratch = new();
    private readonly HashSet<Entity<ProximityFuseTargetComponent>> _nearbyScratch = new();

    public override void Initialize()
    {
        base.Initialize();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
    }

    private bool IsFriendlyTarget(TransformComponent shooterTransform, EntityUid target, TransformComponent targetTransform)
    {
        if (shooterTransform.GridUid is not { } owner)
            return false;

        var targetOwner = targetTransform.GridUid;

        // A missile in flight has no grid of its own, so fall back to whoever launched it.
        if (targetOwner == null &&
            _projectileQuery.TryGetComponent(target, out var targetProjectile) &&
            _xformQuery.TryGetComponent(targetProjectile.Shooter, out var targetShooterXform))
        {
            targetOwner = targetShooterXform.GridUid;
        }

        if (targetOwner is not { } resolvedTargetOwner)
            return false;

        if (resolvedTargetOwner == owner)
            return true;

        return _shuttle.GetIFFRelation(owner, _shuttle.GetIFFFaction(resolvedTargetOwner)) == Relations.Ally;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ProximityFuseComponent, ProjectileComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var projectile, out var xform))
        {
            if (!_xformQuery.TryGetComponent(projectile.Shooter, out var shooterTransform))
                continue;

            if (comp.Safety > 0)
            {
                comp.Safety -= frameTime;
                continue;
            }

            var ourMapPos = _transform.ToMapCoordinates(xform.Coordinates).Position;
            var maxRangeSq = comp.MaxRange * comp.MaxRange;

            if (!ProcessTrackedTargets(uid, comp, ourMapPos, shooterTransform, maxRangeSq))
                continue;

            comp.NextDiscoveryScan -= frameTime;
            if (comp.NextDiscoveryScan > 0)
                continue;

            comp.NextDiscoveryScan = DiscoveryInterval;

            // Query the target component directly into a reused set. Pulling every entity in range and filtering
            // afterwards allocated a fresh set per projectile every 0.08s and walked every wall and item near a ship.
            _nearbyScratch.Clear();
            _lookup.GetEntitiesInRange(new MapCoordinates(ourMapPos, xform.MapID), comp.MaxRange, _nearbyScratch,
                LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Sundries);

            foreach (var (near, _) in _nearbyScratch)
            {
                if (near == uid)
                    continue;

                if (!_xformQuery.TryGetComponent(near, out var txform))
                    continue;

                if (IsFriendlyTarget(shooterTransform, near, txform))
                    continue;

                if (comp.Targets.ContainsKey(near))
                    continue;

                var distance = Vector2.Distance(ourMapPos, _transform.ToMapCoordinates(txform.Coordinates).Position);

                if (distance <= comp.MaxRange)
                    comp.Targets[near] = distance;
            }
        }
    }

    private bool ProcessTrackedTargets(
        EntityUid uid,
        ProximityFuseComponent comp,
        Vector2 ourMapPos,
        TransformComponent shooterTransform,
        float maxRangeSq)
    {
        _keysScratch.Clear();
        _keysScratch.AddRange(comp.Targets.Keys);

        foreach (var near in _keysScratch)
        {
            if (!_xformQuery.TryGetComponent(near, out var txform))
            {
                comp.Targets.Remove(near);
                continue;
            }

            if (IsFriendlyTarget(shooterTransform, near, txform))
            {
                comp.Targets.Remove(near);
                continue;
            }

            var distance = Vector2.Distance(ourMapPos, _transform.ToMapCoordinates(txform.Coordinates).Position);

            if (distance * distance > maxRangeSq)
            {
                comp.Targets.Remove(near);
                continue;
            }

            if (comp.Targets.TryGetValue(near, out var lastDistance))
            {
                comp.Targets[near] = distance;
                if (distance > lastDistance)
                {
                    Detonate(uid);
                    return false;
                }
            }
            else
            {
                comp.Targets[near] = distance;
            }
        }

        return true;
    }

    public void Detonate(EntityUid uid)
    {
        if (HasComp<ExplosiveComponent>(uid))
            _explosion.TriggerExplosive(uid);
        else
            QueueDel(uid); 
    }
}
