using Content.Server._Crescent.DroneControl;
using Content.Server.Shuttles.Components;
using Content.Shared.Atmos.Components;
using Content.Shared.Audio;
using Content.Shared.CCVar;
using Content.Shared.Clothing;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Projectiles;
using Content.Shared.Slippery;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using System.Numerics;

namespace Content.Server.Shuttles.Systems;

// shuttle impact damage ported from Goobstation (AGPLv3) with agreement of all coders involved
public sealed partial class ShuttleSystem
{
    private bool _enabled;
    private float _minimumImpactInertia;
    private float _minimumImpactVelocity;
    private float _tileBreakEnergyMultiplier;
    private float _damageMultiplier;
    private float _structuralDamage;
    private float _sparkEnergy;
    private float _impactRadius;
    private float _impactSlowdown;
    private float _minThrowVelocity;
    private float _massBias;
    private float _inertiaScaling;
    private float _energyMultiplier;
    // this doesn't update if plating mass is changed but edgecase
    private float _platingMass;

    private const float _sparkChance = 0.2f;
    // shuttle mass to consider the neutral point for inertia scaling
    private const float _baseShuttleMass = 50f;
    // exists primarily for optimisation so not a cvar
    private const float _minImpulseVelocity = 0.07f;
    // high-speed collisions tend to be a series of increasingly smaller collisions so don't spam admin logs
    private readonly TimeSpan _adminLogSpacing = TimeSpan.FromSeconds(3);
    // A grind resolves many contacts a second and every impact sound is a networked entity that each client in
    // range turns into an OpenAL source. Unbounded, a ram exhausts the client's audio sources mid-collision
    // ("Error creating audio source" / "AL error: OutOfMemory") and the entity churn stalls it, so space them out.
    private readonly TimeSpan _impactSoundSpacing = TimeSpan.FromSeconds(0.25);
    // Sparks are networked entities too, so cap how many one drain is allowed to spawn.
    private const int _maxSparksPerDrain = 24;

    private readonly SoundCollectionSpecifier _shuttleImpactSound = new("ShuttleImpactSound");
    private readonly ProtoId<ContentTileDefinition> _platingId = "Plating";
    private readonly EntProtoId _sparkEffect = "EffectSparks";

    private EntityQuery<DamageableComponent> _dmgQuery;
    private EntityQuery<ProjectileComponent> _projQuery;

    private HashSet<EntityUid> _countedEnts = new();
    private HashSet<EntityUid> _intersecting = new();
    // for _adminLogSpacing
    private Dictionary<EntityUid, TimeSpan> _impactedAt = new();
    // for _impactSoundSpacing
    private readonly Dictionary<EntityUid, TimeSpan> _impactSoundAt = new();
    // Spark budget left in the drain currently being processed, see _maxSparksPerDrain.
    private int _sparkBudget;

    /// <summary>
    /// One contact point of a grid-on-grid collision, captured during the physics step and resolved afterwards.
    /// </summary>
    private readonly record struct PendingImpact(
        EntityUid OurEntity,
        EntityUid OtherEntity,
        Vector2 WorldPoint,
        Vector2 OurLocalPoint,
        Vector2 OtherLocalPoint,
        Vector2 OurVelocity,
        Vector2 OtherVelocity,
        Vector2 OurBodyVelocity,
        Vector2 OtherBodyVelocity,
        float OurAngularVelocity,
        float OtherAngularVelocity,
        Vector2 WorldNormal,
        float EffectiveInertiaMult,
        float OurFixtureDensity,
        float OtherFixtureDensity);

    // Crescent: impacts are queued here by the collision handler and drained from Update().
    // StartCollideEvent is raised from the middle of SharedPhysicsSystem.CollideContacts, while it iterates a
    // pooled Contact[]. Anything we do that destroys a contact - gibbing a mob (QueueDel purges its contacts
    // immediately), reparenting gibs/dropped items across broadphases, or breaking tiles (which makes the grid
    // regenerate its fixtures) - hands that Contact back to the pool mid-iteration. If the pool then reissues it
    // the loop dereferences FixtureA/FixtureB on a recycled contact and the server dies. So: look, don't touch.
    private readonly List<PendingImpact> _pendingImpacts = new();
    private readonly List<PendingImpact> _processingImpacts = new();

    /// <summary>
    /// Per-grid velocity bookkeeping for one <see cref="UpdateImpact"/> drain. Base values are the pre-solve
    /// velocities from the collision; Delta is the impact slowdown accumulated so far this drain.
    /// </summary>
    private record struct ImpactVelocity(Vector2 Linear, float Angular, Vector2 Delta, bool Restore);
    private readonly Dictionary<EntityUid, ImpactVelocity> _impactVelocities = new();

    // Scratch buffers for ProcessImpactZone, see the note there.
    private readonly List<ImpactTileData> _tilesToProcess = new();
    private readonly List<(Vector2i, Tile)> _brokenTiles = new();
    private readonly List<Vector2i> _sparkTiles = new();
    private readonly HashSet<Entity<TransformComponent>> _entitiesOnTile = new();
    private readonly List<EntityUid> _staleImpacts = new();

    private void InitializeImpact()
    {
        SubscribeLocalEvent<ShuttleComponent, StartCollideEvent>(OnShuttleCollide);

        _dmgQuery = GetEntityQuery<DamageableComponent>();
        _projQuery = GetEntityQuery<ProjectileComponent>();

        Subs.CVar(_cfg, CCVars.ImpactEnabled, value => _enabled = value, true);
        Subs.CVar(_cfg, CCVars.MinimumImpactInertia, value => _minimumImpactInertia = value, true);
        Subs.CVar(_cfg, CCVars.MinimumImpactVelocity, value => _minimumImpactVelocity = value, true);
        Subs.CVar(_cfg, CCVars.TileBreakEnergyMultiplier, value => _tileBreakEnergyMultiplier = value, true);
        Subs.CVar(_cfg, CCVars.ImpactDamageMultiplier, value => _damageMultiplier = value, true);
        Subs.CVar(_cfg, CCVars.ImpactStructuralDamage, value => _structuralDamage = value, true);
        Subs.CVar(_cfg, CCVars.SparkEnergy, value => _sparkEnergy = value, true);
        Subs.CVar(_cfg, CCVars.ImpactRadius, value => _impactRadius = value, true);
        Subs.CVar(_cfg, CCVars.ImpactSlowdown, value => _impactSlowdown = value, true);
        Subs.CVar(_cfg, CCVars.ImpactMinThrowVelocity, value => _minThrowVelocity = value, true);
        Subs.CVar(_cfg, CCVars.ImpactMassBias, value => _massBias = value, true);
        Subs.CVar(_cfg, CCVars.ImpactInertiaScaling, value => _inertiaScaling = value, true);
        Subs.CVar(_cfg, CCVars.ImpactEnergyMultiplier, value => _energyMultiplier = value, true);

        _platingMass = _protoManager.Index(_platingId).Mass;
    }

    /// <summary>
    /// Drains impacts queued by <see cref="OnShuttleCollide"/>. Runs outside the physics step, so it is safe to
    /// delete entities, reparent them and break tiles here.
    /// </summary>
    private void UpdateImpact()
    {
        if (_pendingImpacts.Count == 0)
        {
            if (_impactedAt.Count > 0 || _impactSoundAt.Count > 0)
                PruneImpactLog();

            return;
        }

        // Swap into a scratch list first: resolving an impact can raise events that queue further impacts,
        // and we do not want to iterate a list that is still being appended to.
        _processingImpacts.Clear();
        _processingImpacts.AddRange(_pendingImpacts);
        _pendingImpacts.Clear();

        _impactVelocities.Clear();
        _sparkBudget = _maxSparksPerDrain;

        foreach (var impact in _processingImpacts)
        {
            _impactVelocities.TryAdd(impact.OurEntity, new ImpactVelocity(impact.OurBodyVelocity, impact.OurAngularVelocity, Vector2.Zero, false));
            _impactVelocities.TryAdd(impact.OtherEntity, new ImpactVelocity(impact.OtherBodyVelocity, impact.OtherAngularVelocity, Vector2.Zero, false));

            // Crescent: before impacts were queued, the engine raising StartCollideEvent for both sides meant every
            // contact resolved twice, the second pass seeing the first pass's slowdown. Ramming was tuned around
            // that - a rammer grinds in and stalls on its own - so keep doing it on purpose.
            ResolveImpact(impact);
            ResolveImpact(impact);
        }

        // The physics solver has already stopped these grids dead against the contact. Where the hull gave way,
        // the contact is gone, so hand back the pre-collision momentum minus the accumulated impact slowdown.
        // Grids where nothing broke keep the solver's response.
        foreach (var (uid, velocity) in _impactVelocities)
        {
            if (velocity.Restore)
                RestoreImpactVelocity(uid, velocity.Linear + velocity.Delta, velocity.Angular);
        }

        _impactVelocities.Clear();
        _processingImpacts.Clear();
    }

    private Vector2 GetImpactDelta(EntityUid uid)
    {
        return _impactVelocities.TryGetValue(uid, out var velocity) ? velocity.Delta : Vector2.Zero;
    }

    private void AddImpactDelta(EntityUid uid, Vector2 deltaV, bool restore)
    {
        if (!_impactVelocities.TryGetValue(uid, out var velocity))
            return;

        velocity.Delta += deltaV;
        velocity.Restore |= restore;
        _impactVelocities[uid] = velocity;
    }

    /// <summary>
    /// Handles collision between two shuttles. Only reads physics state - the actual damage is queued and applied
    /// from <see cref="UpdateImpact"/> once the physics step is over.
    /// </summary>
    private void OnShuttleCollide(EntityUid uid, ShuttleComponent component, ref StartCollideEvent args)
    {
        if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)
            || TerminatingOrDeleted(args.OtherEntity) || EntityManager.IsQueuedForDeletion(args.OtherEntity)
        )
            return;

        // Skip the entire impact so a newly spawned drone cannot damage either hull.
        if (HasDroneSpawnProtection(args.OurEntity) || HasDroneSpawnProtection(args.OtherEntity))
            return;

        if (!_gridQuery.TryComp(args.OurEntity, out var ourGrid) ||
            !_gridQuery.TryComp(args.OtherEntity, out var otherGrid)
        )
            return;

        // The engine raises one StartCollideEvent per side of a contact, and OnGridInit puts a ShuttleComponent on
        // every grid, so this handler sees the same collision twice with Our/Other swapped. Each pass already
        // resolves the impact for BOTH grids, so without this the damage, broken tiles and slowdown impulse all get
        // applied twice. Take only the pass where we are the lower entity.
        if (HasComp<ShuttleComponent>(args.OtherEntity) && args.OurEntity.Id > args.OtherEntity.Id)
            return;

        var ourBody = args.OurBody;
        var otherBody = args.OtherBody;

        // TODO: Would also be nice to have a continuous sound for scraping.
        var ourXform = Transform(args.OurEntity);
        var otherXform = Transform(args.OtherEntity);
        var worldPoints = args.WorldPoints;
        var worldNormal = args.WorldNormal;

        for (var i = 0; i < worldPoints.Length; i++)
        {
            var worldPoint = worldPoints[i];

            var ourPoint = _transform.ToCoordinates((args.OurEntity, ourXform), new MapCoordinates(worldPoint, ourXform.MapID));
            var otherPoint = _transform.ToCoordinates((args.OtherEntity, otherXform), new MapCoordinates(worldPoint, otherXform.MapID));

            var ourVelocity = _physics.GetLinearVelocity(args.OurEntity, ourPoint.Position, ourBody, ourXform);
            var otherVelocity = _physics.GetLinearVelocity(args.OtherEntity, otherPoint.Position, otherBody, otherXform);
            var topDiff = (ourVelocity - otherVelocity);
            var jungleDiff = topDiff.Length();

            // Get the velocity in relation to the contact normal
            // If this still causes issues see https://box2d.org/posts/2020/06/ghost-collisions/
            // This should only be a potential problem on chunk seams.
            var dotProduct = MathF.Abs(Vector2.Dot(topDiff.Normalized(), worldNormal.Normalized()));
            jungleDiff *= dotProduct;

            // this is cursed but makes it so that collisions of small grid with large grid count the inertia as being approximately the small grid's
            var effectiveInertiaMult = (ourBody.FixturesMass * otherBody.FixturesMass) / (ourBody.FixturesMass + otherBody.FixturesMass);
            var effectiveInertia = jungleDiff * effectiveInertiaMult;

            // TODO: squish damage so that a tiny splinter grid can't stop 2 big grids by being in the way
            if (jungleDiff < _minimumImpactVelocity && effectiveInertia < _minimumImpactInertia
                || ourXform.MapUid == null
                || float.IsNaN(jungleDiff))
            {
                continue;
            }

            // Densities are read now because the Fixture objects can be destroyed before we get to resolve this.
            _pendingImpacts.Add(new PendingImpact(
                args.OurEntity,
                args.OtherEntity,
                worldPoint,
                ourPoint.Position,
                otherPoint.Position,
                ourVelocity,
                otherVelocity,
                ourBody.LinearVelocity,
                otherBody.LinearVelocity,
                ourBody.AngularVelocity,
                otherBody.AngularVelocity,
                worldNormal,
                effectiveInertiaMult,
                args.OurFixture.Density,
                args.OtherFixture.Density));
        }
    }

    private bool HasDroneSpawnProtection(EntityUid grid)
    {
        return TryComp<DroneSpawnProtectionComponent>(grid, out var protection)
            && _gameTiming.CurTime < protection.ExpiresAt;
    }

    /// <summary>
    /// Applies one queued impact. Called from <see cref="UpdateImpact"/>, never from a collision handler.
    /// </summary>
    private void ResolveImpact(PendingImpact impact)
    {
        var ourEntity = impact.OurEntity;
        var otherEntity = impact.OtherEntity;

        // A tick has passed since the collision, so re-validate everything.
        if (TerminatingOrDeleted(ourEntity) || EntityManager.IsQueuedForDeletion(ourEntity)
            || TerminatingOrDeleted(otherEntity) || EntityManager.IsQueuedForDeletion(otherEntity))
            return;

        if (HasDroneSpawnProtection(ourEntity) || HasDroneSpawnProtection(otherEntity))
            return;

        if (!_gridQuery.TryComp(ourEntity, out var ourGrid) ||
            !_gridQuery.TryComp(otherEntity, out var otherGrid))
            return;

        if (!_physicsQuery.TryComp(ourEntity, out var ourBody) ||
            !_physicsQuery.TryComp(otherEntity, out var otherBody))
            return;

        var ourXform = Transform(ourEntity);
        var otherXform = Transform(otherEntity);

        if (ourXform.MapUid == null)
            return;

        // One of them may have FTL'd out in the meantime, in which case there is no impact to speak of.
        if (ourXform.MapUid != otherXform.MapUid)
            return;

        // Velocities as of the collision, minus whatever slowdown earlier passes have already dealt this drain.
        var ourVelocity = impact.OurVelocity + GetImpactDelta(ourEntity);
        var otherVelocity = impact.OtherVelocity + GetImpactDelta(otherEntity);
        var topDiff = ourVelocity - otherVelocity;
        var jungleDiff = topDiff.Length() * MathF.Abs(Vector2.Dot(topDiff.Normalized(), impact.WorldNormal.Normalized()));
        var effectiveInertiaMult = impact.EffectiveInertiaMult;

        // Earlier passes may have slowed the grids below the impact threshold.
        if (jungleDiff < _minimumImpactVelocity && jungleDiff * effectiveInertiaMult < _minimumImpactInertia
            || float.IsNaN(jungleDiff))
            return;

        // Anchored to the grid rather than to the stale map position: the grids have kept moving since the
        // collision, and at ramming speed that is several tiles.
        var coordinates = new EntityCoordinates(ourEntity, impact.OurLocalPoint);
        var worldPoint = _transform.ToMapCoordinates(coordinates).Position;

        // Rate-limited because a scrape resolves many contacts per second, see _impactSoundSpacing.
        if (CheckShouldPlaySound(ourEntity) && CheckShouldPlaySound(otherEntity))
        {
            var volume = MathF.Min(10f, MathF.Pow(jungleDiff, 0.5f) - 5f);
            var audioParams = AudioParams.Default.WithVariation(SharedContentAudioSystem.DefaultVariation).WithVolume(volume);
            _audio.PlayPvs(_shuttleImpactSound, coordinates, audioParams);

            _impactSoundAt[ourEntity] = _gameTiming.CurTime;
            _impactSoundAt[otherEntity] = _gameTiming.CurTime;
        }

        // if we're not enabled, stop after playing sound
        if (!_enabled)
            return;

        // Convert the collision point directly to tile indices
        var ourTile = new Vector2i((int)Math.Floor(impact.OurLocalPoint.X / ourGrid.TileSize), (int)Math.Floor(impact.OurLocalPoint.Y / ourGrid.TileSize));
        var otherTile = new Vector2i((int)Math.Floor(impact.OtherLocalPoint.X / otherGrid.TileSize), (int)Math.Floor(impact.OtherLocalPoint.Y / otherGrid.TileSize));

        var ourMass = GetRegionMass(ourEntity, ourGrid, ourTile, _impactRadius, out var ourTiles);
        var otherMass = GetRegionMass(otherEntity, otherGrid, otherTile, _impactRadius, out var otherTiles);

        // just in case
        if (ourTiles == 0 || otherTiles == 0)
            return;

        // E = MV^2/2
        var energyMult = MathF.Pow(jungleDiff, 2) / 2 * _energyMultiplier;
        // mass-based damage reduction to grid with more mass so that plastitanium block rammer doesn't die to lattice
        var ourMassDR = MathF.Max(otherMass / ourMass, 1f);
        var otherMassDR = MathF.Max(ourMass / otherMass, 1f);
        // multiplier to make large grids not just bonk against each other
        var inertiaMult = MathF.Pow(effectiveInertiaMult / _baseShuttleMass, _inertiaScaling);
        var toUsEnergy = otherMass * energyMult * inertiaMult * ourMassDR;
        var toOtherEnergy = ourMass * energyMult * inertiaMult * otherMassDR;

        var logImpact = LogImpact.High;
        // if impact isn't tiny, log it as extreme
        if (toUsEnergy + toOtherEnergy > 2f * _tileBreakEnergyMultiplier * _platingMass)
            logImpact = LogImpact.Extreme;
        // TODO: would be nice for it to also log who is piloting the grid(s)
        // Rate-limit both logs because a scrape raises many collision events.
        if (CheckShouldLog(ourEntity) && CheckShouldLog(otherEntity))
        {
            _logger.Add(LogType.ShuttleImpact, logImpact, $"Shuttle impact of {ToPrettyString(ourEntity)} with {ToPrettyString(otherEntity)} at {worldPoint}");
            Log.Debug($"Shuttle impact of {ToPrettyString(ourEntity)} with {ToPrettyString(otherEntity)}; our mass: {ourMass}, other: {otherMass}, velocity {jungleDiff}, impact point {worldPoint}");
        }

        _impactedAt[ourEntity] = _gameTiming.CurTime;
        _impactedAt[otherEntity] = _gameTiming.CurTime;

        // uses local region mass for slowdown calculation so lattice doesn't have same slowdown as wall block
        var totalInertia = ourVelocity * ourMass + otherVelocity * otherMass;
        var inelasticVel = totalInertia / (ourMass + otherMass);

        var ourDeltaV = DoGridImpact((ourEntity, ourGrid, ourXform, ourBody), impact.OurFixtureDensity, inelasticVel, ourVelocity, ourTile, ourTiles, toUsEnergy, out var ourBroke);

        // The first DoGridImpact can destroy the other grid outright, so re-check before touching it.
        var otherAlive = !TerminatingOrDeleted(otherEntity) && !EntityManager.IsQueuedForDeletion(otherEntity);
        var otherDeltaV = Vector2.Zero;
        var otherBroke = false;
        if (otherAlive)
            otherDeltaV = DoGridImpact((otherEntity, otherGrid, otherXform, otherBody), impact.OtherFixtureDensity, inelasticVel, otherVelocity, otherTile, otherTiles, toOtherEnergy, out otherBroke);

        // Applied once the whole drain is done, see UpdateImpact.
        var hullGaveWay = ourBroke || otherBroke;
        AddImpactDelta(ourEntity, ourDeltaV, hullGaveWay || !otherAlive);
        if (otherAlive)
            AddImpactDelta(otherEntity, otherDeltaV, hullGaveWay);
    }

    private void RestoreImpactVelocity(EntityUid uid, Vector2 linear, float angular)
    {
        if (TerminatingOrDeleted(uid) || !_physicsQuery.TryComp(uid, out var body))
            return;

        if (!float.IsFinite(linear.X) || !float.IsFinite(linear.Y) || !float.IsFinite(angular))
            return;

        _physics.SetLinearVelocity(uid, linear, body: body);
        _physics.SetAngularVelocity(uid, angular, body: body);
    }

    /// <summary>
    /// Damages the impact zone and returns the velocity change the impact should cause. The caller applies it,
    /// because the physics solver has already responded to this contact by the time we get here.
    /// </summary>
    private Vector2 DoGridImpact(Entity<MapGridComponent, TransformComponent, PhysicsComponent> ent,
                              float fixtureDensity,
                              Vector2 inelasticVelocity,
                              Vector2 velocity,
                              Vector2i tile,
                              int tiles,
                              float energy,
                              out bool brokeTiles)
    {
        // for readability to not have .Comp1 .Comp2 for everything
        var (_, grid, xform, body) = ent;

        // radius in which to actually do things so we don't hurt person 4 tiles away on slow bump
        var radius = Math.Min(_impactRadius, MathF.Sqrt(energy / _tileBreakEnergyMultiplier / _platingMass));

        // slow us down since destroying impacting grid tiles prevents the collision
        // without this impacts which destroy tiles just make grids slice straight through each other
        var postImpactVelocity = Vector2.Lerp(velocity, inelasticVelocity, MathF.Min(1f, _impactSlowdown * tiles * fixtureDensity / body.FixturesMass));
        var deltaV = -velocity + postImpactVelocity;

        // process tile and entity damage
        brokeTiles = ProcessImpactZone(ent, grid, tile, energy, deltaV.Normalized(), radius);

        // Rat-start
		//// throw every entity on grid if the impulse is not negligible
        //if (deltaV.Length() > _minImpulseVelocity)
        //    ThrowEntitiesOnGrid(ent, xform, -deltaV);
		// Rat-end

        return deltaV;
    }

    /// <summary>
    /// Knocks and throws all unbuckled entities on the specified grid.
    /// </summary>
    private void ThrowEntitiesOnGrid(EntityUid gridUid, TransformComponent xform, Vector2 direction)
    {
        var movedByPressureQuery = GetEntityQuery<MovedByPressureComponent>();
        var knockdownTime = TimeSpan.FromSeconds(5);

        var minsq = _minThrowVelocity * _minThrowVelocity;
        // iterate all entities on the grid
        // TODO: only iterate non-static entities
        var childEnumerator = xform.ChildEnumerator;
        while (childEnumerator.MoveNext(out var uid))
        {
            // don't throw static bodies
            if (!_physicsQuery.TryGetComponent(uid, out var physics) || (physics.BodyType & BodyType.Static) != 0)
                continue;

            // don't throw if buckled
            if (_buckle.IsBuckled(uid, _buckleQuery.CompOrNull(uid)))
                continue;

            // don't throw them if they have magboots
            if (movedByPressureQuery.TryComp(uid, out var moved) && !moved.Enabled)
                continue;

            if (direction.LengthSquared() > minsq)
            {
                _stuns.TryKnockdown(uid, knockdownTime, true);
                _throwing.TryThrow(uid, direction, physics, Transform(uid), _projQuery, direction.Length(), playSound: false);
            }
            else
            {
                _physics.ApplyLinearImpulse(uid, direction * physics.Mass, body: physics);
            }
        }
    }

    /// <summary>
    /// Structure to hold impact tile processing data for batch processing
    /// </summary>
    private record struct ImpactTileData(Vector2i Tile, float Energy, float DistanceFactor);

    /// <summary>
    /// Gets the total mass of all entities and tiles (using ContentTileDefinition.Mass) belonging to this grid in a circle
    /// </summary>
    private float GetRegionMass(EntityUid uid, MapGridComponent grid, Vector2i centerTile, float radius, out int tileCount)
    {
        tileCount = 0;
        var mass = 0f;
        _countedEnts.Clear();

        foreach (var tileRef in _mapSystem.GetLocalTilesIntersecting(uid, grid, new Circle(centerTile + grid.TileSizeHalfVector, radius)))
        {
            var def = (ContentTileDefinition)_tileDefManager[tileRef.Tile.TypeId];
            mass += def.Mass;
            tileCount++;

            _intersecting.Clear();
            _lookup.GetLocalEntitiesIntersecting(uid, tileRef.GridIndices, _intersecting, gridComp: grid);
            foreach (var localUid in _intersecting)
            {
                if (!_countedEnts.Add(localUid))
                    continue;

                if (_physicsQuery.TryComp(localUid, out var physics))
                    mass += physics.FixturesMass;
            }
        }
        return mass;
    }

    /// <summary>
    /// Processes a zone of tiles around the impact point. Returns whether any tile broke.
    /// </summary>
    private bool ProcessImpactZone(EntityUid uid, MapGridComponent grid, Vector2i centerTile, float energy, Vector2 dir, float radius)
    {
        // Reused across impacts - a scrape resolves many of these per tick and this is a hotspot.
        // Safe because impacts are drained one at a time from Update(), never nested.
        var tilesToProcess = _tilesToProcess;
        var brokenTiles = _brokenTiles;
        var sparkTiles = _sparkTiles;

        tilesToProcess.Clear();
        brokenTiles.Clear();
        sparkTiles.Clear();

        // Pre-calculate all tiles that need processing
        foreach (var tileRef in _mapSystem.GetLocalTilesIntersecting(uid, grid, new Circle(centerTile + grid.TileSizeHalfVector, radius)))
        {
            var distance = centerTile - tileRef.GridIndices;
            // Calculate distance-based energy falloff
            float distanceFactor = 1.0f - distance.Length / (radius + 1);
            float tileEnergy = energy * distanceFactor;

            tilesToProcess.Add(new ImpactTileData(tileRef.GridIndices, tileEnergy, distanceFactor));
        }

        // Process tiles sequentially for safety
        ProcessTileBatch(uid, grid, tilesToProcess, dir, 0, tilesToProcess.Count, brokenTiles, sparkTiles);

        // Only proceed with visual effects if the entity still exists
        if (Exists(uid))
        {
            ProcessBrokenTilesAndSparks(uid, grid, brokenTiles, sparkTiles);
        }

        return brokenTiles.Count > 0;
    }

    /// <summary>
    /// Process a batch of tiles from the impact zone
    /// </summary>
    private void ProcessTileBatch(
        EntityUid uid,
        MapGridComponent grid,
        List<ImpactTileData> tilesToProcess,
        Vector2 throwDirection,
        int startIndex,
        int endIndex,
        List<(Vector2i, Tile)> brokenTiles,
        List<Vector2i> sparkTiles)
    {
        // here so we don't have to `new` it every iteration
        var damageSpec = new DamageSpecifier()
        {
            DamageDict = { ["Blunt"] = 0, ["Structural"] = 0 }
        };

        var entitiesOnTile = _entitiesOnTile;
        var tileCenter = new Vector2(grid.TileSize / 2f, grid.TileSize / 2f);

        for (var i = startIndex; i < endIndex; i++)
        {
            var tileData = tilesToProcess[i];

            bool canBreakTile = true;

            // Process entities on this tile
            entitiesOnTile.Clear();
            _lookup.GetLocalEntitiesIntersecting(uid, tileData.Tile, entitiesOnTile, gridComp: grid);

            // this loop is a hotspot so tell if you know how to optimise it
            foreach (var localEnt in entitiesOnTile)
            {
                // This set was snapshotted before any damage was dealt. Gibbing one mob can destroy others that
                // are still listed here (dropped items, body parts, a chain explosion), so re-check before we read
                // a transform that may no longer be attached to anything.
                if (TerminatingOrDeleted(localEnt) || EntityManager.IsQueuedForDeletion(localEnt))
                    continue;

                // the query can ocassionally return entities barely touching this tile so check for that
                var toCenter = tileData.Tile + tileCenter - localEnt.Comp.Coordinates.Position;
                if (MathF.Abs(toCenter.X) > 0.5f || MathF.Abs(toCenter.Y) > 0.5f)
                    continue;

                if (_dmgQuery.TryComp(localEnt, out var damageable))
                {
                    // Apply damage scaled by distance but capped to prevent gibbing
                    var scaledDamage = tileData.Energy * _damageMultiplier;
                    damageSpec.DamageDict["Blunt"] = scaledDamage;
                    damageSpec.DamageDict["Structural"] = scaledDamage * _structuralDamage;

                    _damageSys.TryChangeDamage(localEnt, damageSpec, damageable: damageable);
                }
                // might've been destroyed
                if (TerminatingOrDeleted(localEnt) || EntityManager.IsQueuedForDeletion(localEnt))
                    continue;

                if (!_physicsQuery.TryComp(localEnt, out var physics))
                    continue;

                // no breaking tiles under walls that haven't been destroyed
                if ((physics.BodyType & BodyType.Static) != 0
                    && (physics.CollisionLayer & (int)CollisionGroup.Impassable) != 0)
                {
                    canBreakTile = false;
                }
                else
                {
					// Rat-start
                    // var direction = throwDirection * tileData.DistanceFactor;
                    // _throwing.TryThrow(localEnt, direction, physics, localEnt.Comp, _projQuery, direction.Length(), playSound: false);
					// Rat-end
                }
            }

            // Mark tiles for spark effects
            if (tileData.Energy > _sparkEnergy && tileData.DistanceFactor > 0.7f && _random.Prob(_sparkChance))
                sparkTiles.Add(tileData.Tile);

            if (!canBreakTile)
                continue;

            // Mark tiles for breaking/effects
            var def = (ContentTileDefinition)_tileDefManager[_mapSystem.GetTileRef(uid, grid, tileData.Tile).Tile.TypeId];
            if (tileData.Energy > def.Mass * _tileBreakEnergyMultiplier)
                brokenTiles.Add((tileData.Tile, Tile.Empty));

        }
    }

    /// <summary>
    /// Process visual effects and tile breaking after entity processing
    /// </summary>
    private void ProcessBrokenTilesAndSparks(
        EntityUid uid,
        MapGridComponent grid,
        List<(Vector2i, Tile)> brokenTiles,
        List<Vector2i> sparkTiles)
    {
        // Break tiles
        _mapSystem.SetTiles(uid, grid, brokenTiles);

        if (TerminatingOrDeleted(uid))
            return;

        // Spawn spark effects, up to the drain's budget
        foreach (var tile in sparkTiles)
        {
            if (_sparkBudget <= 0)
                break;

            _sparkBudget--;
            var coords = _mapSystem.GridTileToLocal(uid, grid, tile);
            Spawn(_sparkEffect, coords);
        }
    }

    /// <summary>
    /// Check whether this impact should be logged to admins.
    /// Used to prevent spamming logs.
    /// </summary>
    private bool CheckShouldLog(EntityUid uid)
    {
        return !(_impactedAt.TryGetValue(uid, out var last) && _gameTiming.CurTime < last + _adminLogSpacing);
    }

    /// <summary>
    /// Check whether this grid is allowed another impact sound yet. Used to keep a ram from spawning more audio
    /// entities than a client can hold sources for.
    /// </summary>
    private bool CheckShouldPlaySound(EntityUid uid)
    {
        return !(_impactSoundAt.TryGetValue(uid, out var last) && _gameTiming.CurTime < last + _impactSoundSpacing);
    }

    /// <summary>
    /// Drops rate-limit entries that can no longer rate-limit anything. Debris grids delete themselves and system
    /// state outlives the round, so without this the dictionary only ever grows.
    /// </summary>
    private void PruneImpactLog()
    {
        var cutoff = _gameTiming.CurTime - _adminLogSpacing;

        _staleImpacts.Clear();
        foreach (var (uid, time) in _impactedAt)
        {
            if (time < cutoff || TerminatingOrDeleted(uid))
                _staleImpacts.Add(uid);
        }

        foreach (var uid in _staleImpacts)
        {
            _impactedAt.Remove(uid);
        }

        _staleImpacts.Clear();

        var soundCutoff = _gameTiming.CurTime - _impactSoundSpacing;
        foreach (var (uid, time) in _impactSoundAt)
        {
            if (time < soundCutoff || TerminatingOrDeleted(uid))
                _staleImpacts.Add(uid);
        }

        foreach (var uid in _staleImpacts)
        {
            _impactSoundAt.Remove(uid);
        }

        _staleImpacts.Clear();
    }
}
