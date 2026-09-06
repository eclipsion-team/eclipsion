using System.Numerics;
using System.Linq;
using Content.Shared._Crescent;
using Content.Server._Crescent.ShipShields;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Physics;
using Content.Shared.Projectiles;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Server.PointCannons;
using Content.Shared.Maps;
using Content.Shared.Tiles;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;

public sealed class ProjectilePhasePreventerSystem : EntitySystem
{
    [Dependency] private readonly PhysicsSystem _phys = default!;
    [Dependency] private readonly TransformSystem _trans = default!;
    [Dependency] private readonly SharedProjectileSystem _projectile = default!;
    [Dependency] private readonly ShipShieldsSystem _shipShields = default!;
    [Dependency] private readonly ILogManager _logs = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly ExplosionSystem _explosions = default!;
    [Dependency] private readonly TriggerSystem _triggers = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinitions = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<FixturesComponent> _fixturesQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private readonly Dictionary<EntityUid, Entity<ProjectilePhasePreventComponent, ProjectileComponent>> _projectiles = new();

    private ISawmill _sawmill = default!;

    // xtra forgiveness beyond the projectile's exact movement distance. modify this if we ever raise tps opr have issues with phasing again
    private const float RaycastExtraDistance = 2f;

    // prevents tiny zero-length raycasts
    private const float MinimumTravelDistance = 0.001f;

    // Equivalent to the guaranteed-break intensity of Shipgun, applied to accumulated direct damage.
    // Standard plating (0.35 multiplier) takes about 86 structural damage; lattice takes 30.
    private const float DirectTileBreakDamage = 30f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProjectilePhasePreventComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ProjectilePhasePreventComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ProjectilePhasePreventComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);

        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        _sawmill = _logs.GetSawmill("Phase-Prevention");
    }

    private void OnStartup(EntityUid uid, ProjectilePhasePreventComponent comp, ref ComponentStartup args)
    {
        if (!TryComp<ProjectileComponent>(uid, out var projectile))
        {
            _sawmill.Error($"Tried to initialize ProjectilePhasePreventComponent on entity without ProjectileComponent. Prototype: {MetaData(uid).EntityPrototype?.ID}");
            RemComp<ProjectilePhasePreventComponent>(uid);
            return;
        }

        comp.start = _trans.GetWorldPosition(uid);
        comp.mapId = _trans.GetMapId(uid);

        _projectiles[uid] = (uid, comp, projectile);
    }

    private void OnShutdown(EntityUid uid, ProjectilePhasePreventComponent comp, ref ComponentShutdown args)
    {
        _projectiles.Remove(uid);
    }

    private void OnPreventCollide(EntityUid uid, ProjectilePhasePreventComponent comp, ref PreventCollideEvent args)
    {
        // The ordered sweep owns collisions in this mode, including rounds that normally use hard
        // fixtures. Physics must not detonate them on a farther wall before the swept floor hit runs.
        if (comp.TargetTiles)
            args.Cancelled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var (owner, phase, projectile) in _projectiles.Values)
        {
            if (TerminatingOrDeleted(owner) || projectile.DamagedEntity)
                continue;

            if (!_physicsQuery.TryGetComponent(owner, out var bulletPhysics))
                continue;

            if (!_fixturesQuery.TryGetComponent(owner, out var bulletFixtures))
                continue;

            if (bulletFixtures.Fixtures.Count == 0)
                continue;

            var currentPos = _trans.GetWorldPosition(owner);
            var currentMap = _trans.GetMapId(owner);

            // Never raycast across maps
            if (currentMap != phase.mapId)
            {
                phase.start = currentPos;
                phase.mapId = currentMap;
                continue;
            }

            var previousPos = phase.start;
            var delta = currentPos - previousPos;
            var distance = delta.Length();

            if (distance <= MinimumTravelDistance)
                continue;

            var direction = delta / distance;

            KeyValuePair<string, Fixture> bulletFixturePair = default;
            foreach (var kv in bulletFixtures.Fixtures) { bulletFixturePair = kv; break; }
            var bulletFixtureKey = bulletFixturePair.Key;

            var ignoredGrid = EntityUid.Invalid;

            if (projectile.Weapon != null &&
                _xformQuery.TryGetComponent(projectile.Weapon, out var weaponXform) &&
                weaponXform.GridUid != null)
            {
                ignoredGrid = weaponXform.GridUid.Value;
            }

            // PhasePrevention is a query-only layer used by soft shield bubbles. Normal collision masks do not
            // include it, so adding it here cannot make shields physically solid.
            var ray = new CollisionRay(previousPos,
                direction,
                phase.relevantBitmasks | (int) CollisionGroup.PhasePrevention);

            var hits = _phys.IntersectRay(currentMap, ray, distance + RaycastExtraDistance,
                projectile.Weapon, false);
            if (phase.TargetTiles)
            {
                var tileHits = hits.ToList();
                AddTileHits(currentMap, previousPos, direction, distance + RaycastExtraDistance,
                    projectile.IgnoreWeaponGrid ? ignoredGrid : EntityUid.Invalid, tileHits);
                tileHits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                hits = tileHits;
            }

            foreach (var hit in hits)
            {
                var hitEntity = hit.HitEntity;

                if (hitEntity == owner)
                    continue;

                if (projectile.IgnoreShooter && projectile.Shooter == hitEntity)
                    continue;

                if (projectile.IgnoredEntities.Contains(hitEntity))
                    continue;

                if (!_xformQuery.TryGetComponent(hitEntity, out var hitXform))
                    continue;

                if (projectile.IgnoreWeaponGrid &&
                    ignoredGrid != EntityUid.Invalid &&
                    (hitXform.GridUid == ignoredGrid || hitEntity == ignoredGrid))
                {
                    continue;
                }

                // Rockets from the same shuttle pass through each other. A saturation launcher fires its whole
                // salvo from one muzzle, and this raycast reaches further than the gap between two shots, so
                // without this the burst detonates on itself. Keep scanning - a real target may be behind it.
                if (_projectile.IsFriendlyShipProjectile(owner, projectile, hitEntity))
                    continue;

                if (TryComp<ShipShieldComponent>(hitEntity, out var shield))
                {
                    if (_shipShields.TryQueueDeflection((hitEntity, shield), owner))
                        break;

                    // Shield-ignoring and same-grid rounds must keep scanning for a real target behind the bubble.
                    continue;
                }

                if (phase.TargetTiles && TryComp<MapGridComponent>(hitEntity, out var grid))
                {
                    if (HitTile(owner, projectile, bulletPhysics, hitEntity, grid,
                            hit.HitPos, currentMap))
                        break;

                    continue;
                }

                if (!_physicsQuery.TryGetComponent(hitEntity, out _))
                    continue;

                if (!_fixturesQuery.TryGetComponent(hitEntity, out var targetFixtures))
                    continue;

                if (targetFixtures.Fixtures.Count == 0)
                    continue;

                KeyValuePair<string, Fixture> targetFixturePair = default;
                foreach (var kv in targetFixtures.Fixtures) { targetFixturePair = kv; break; }

                var bulletEvent = new HullrotBulletHitEvent
                {
                    selfEntity = owner,
                    hitEntity = hitEntity,
                    selfFixtureKey = bulletFixtureKey,
                    targetFixture = targetFixturePair.Value,
                    targetFixtureKey = targetFixturePair.Key,
                    selfPhys = bulletPhysics
                };

                try
                {
                    if (phase.TargetTiles)
                        _trans.SetWorldPosition(owner, hit.HitPos);

                    RaiseLocalEvent(owner, ref bulletEvent, true);

                    if (phase.TargetTiles && projectile.DamagedEntity && HasComp<TriggerOnCollideComponent>(owner))
                        _triggers.Trigger(owner, projectile.Shooter);
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Failed to raise phase-prevent hit event: {e}");
                }

                break;
            }

            phase.start = currentPos;
            phase.mapId = currentMap;
        }
    }

    /// <summary>
    /// A grid intercept is resolved against its covering wall first. Once the wall is gone, later rounds
    /// stop on the remaining floor layers instead of travelling through them to the next wall.
    /// </summary>
    private bool HitTile(EntityUid uid, ProjectileComponent projectile, PhysicsComponent physics,
        EntityUid gridUid, MapGridComponent grid, Vector2 position, MapId mapId)
    {
        var tile = _map.GetTileRef(gridUid, grid, new MapCoordinates(position, mapId));
        if (tile.Tile.IsEmpty || HasComp<ProtectedGridComponent>(gridUid))
            return false;

        EntityUid target = gridUid;
        var anchored = _map.GetAnchoredEntities(gridUid, grid, tile.GridIndices);
        while (anchored.MoveNext(out var entity))
        {
            if (TerminatingOrDeleted(entity) || !_physicsQuery.TryGetComponent(entity, out var body) ||
                !body.CanCollide || !_fixturesQuery.TryGetComponent(entity, out var fixtures))
                continue;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (!fixture.Hard ||
                    (fixture.CollisionLayer & (int) (CollisionGroup.Impassable | CollisionGroup.BulletImpassable)) == 0)
                    continue;

                target = entity.Value;
                break;
            }

            if (target != gridUid)
                break;
        }

        // Put effects and explosions at the intercepted tile, not at the end of a fast round's tick.
        _trans.SetWorldPosition(uid, position);

        if (target == gridUid)
            DamageTargetedTile(uid, projectile, gridUid, grid, tile);

        _projectile.ProjectileCollide((uid, projectile, physics), target);
        if (HasComp<TriggerOnCollideComponent>(uid))
            _triggers.Trigger(uid, projectile.Shooter);

        return true;
    }

    private void DamageTargetedTile(EntityUid uid, ProjectileComponent projectile,
        EntityUid gridUid, MapGridComponent grid, TileRef tile)
    {
        var structural = (float) projectile.Damage.DamageDict.GetValueOrDefault("Structural");
        if (structural <= 0 ||
            _tileDefinitions[tile.Tile.TypeId] is not ContentTileDefinition definition ||
            definition.ExplosionBreakMultiplier <= 0 || string.IsNullOrEmpty(definition.BaseTurf) ||
            _tileDefinitions[definition.BaseTurf] is not ContentTileDefinition nextLayer)
            return;

        // Retain map and ammunition restrictions on making a vacuum, even for a direct hit.
        var canCreateVacuum = _explosions.CanCreateVacuum &&
            (definition.MapAtmosphere || !TryComp<ExplosiveComponent>(uid, out var explosive) || explosive.CanCreateVacuum);
        if (nextLayer.MapAtmosphere && !canCreateVacuum)
            return;

        var damage = EnsureComp<ShipWeaponTileDamageComponent>(gridUid).Damage;
        var accumulated = damage.GetValueOrDefault(tile.GridIndices) + structural * definition.ExplosionBreakMultiplier;
        if (accumulated < DirectTileBreakDamage)
        {
            damage[tile.GridIndices] = accumulated;
            return;
        }

        // A direct hit breaks one layer. Excess damage cannot skip the next floor or reach another wall.
        damage.Remove(tile.GridIndices);
        _map.SetTile(gridUid, grid, tile.GridIndices, new Tile(nextLayer.TileId));
    }

    private void OnTileChanged(ref TileChangedEvent args)
    {
        if (!TryComp<ShipWeaponTileDamageComponent>(args.Entity, out var damage))
            return;

        // A replaced, repaired or explosion-destroyed layer must not donate damage to its successor.
        foreach (var change in args.Changes)
            damage.Damage.Remove(change.GridIndices);
    }

    private void AddTileHits(MapId mapId, Vector2 start, Vector2 direction, float length,
        EntityUid ignoredGrid, List<RayCastResults> hits)
    {
        var end = start + direction * length;
        var grids = new List<Entity<MapGridComponent>>();
        _map.FindGridsIntersecting(mapId, new Box2(Vector2.Min(start, end), Vector2.Max(start, end)).Enlarged(0.01f), ref grids);
        foreach (var (gridUid, grid) in grids)
        {
            if (gridUid == ignoredGrid || HasComp<ProtectedGridComponent>(gridUid))
                continue;

            // Walk exact tile boundaries in grid-local space. A grid fixture's world AABB can include
            // empty space on rotated ships and cannot reliably identify the first remaining floor.
            var inverse = _trans.GetInvWorldMatrix(gridUid);
            var localStart = Vector2.Transform(start, inverse) / grid.TileSize;
            var localDirection = Vector2.TransformNormal(direction, inverse) / grid.TileSize;
            var cell = new Vector2i((int) MathF.Floor(localStart.X), (int) MathF.Floor(localStart.Y));
            var stepX = Math.Sign(localDirection.X);
            var stepY = Math.Sign(localDirection.Y);
            var deltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1 / localDirection.X);
            var deltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1 / localDirection.Y);
            var nextX = stepX == 0 ? float.PositiveInfinity :
                (cell.X + (stepX > 0 ? 1 : 0) - localStart.X) / localDirection.X;
            var nextY = stepY == 0 ? float.PositiveInfinity :
                (cell.Y + (stepY > 0 ? 1 : 0) - localStart.Y) / localDirection.Y;

            var travelled = 0f;
            while (travelled <= length)
            {
                var tile = _map.GetTileRef(gridUid, grid, cell);
                if (!tile.Tile.IsEmpty && MathF.Min(nextX, nextY) > travelled)
                {
                    var inside = MathF.Min(travelled + 0.001f, (travelled + MathF.Min(nextX, nextY)) / 2);
                    hits.Add(new RayCastResults(travelled, start + direction * inside, gridUid));
                    break;
                }

                // Step both axes at corners so a tile touched only at a point does not absorb a shot.
                if (nextX < nextY)
                {
                    travelled = nextX;
                    nextX += deltaX;
                    cell.X += stepX;
                }
                else if (nextY < nextX)
                {
                    travelled = nextY;
                    nextY += deltaY;
                    cell.Y += stepY;
                }
                else
                {
                    travelled = nextX;
                    nextX += deltaX;
                    nextY += deltaY;
                    cell.X += stepX;
                    cell.Y += stepY;
                }
            }
        }
    }
}
