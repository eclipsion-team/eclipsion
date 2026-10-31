using System.Numerics;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.ShipAI;
using Content.Server._Mono.NPC.HTN;
using Content.Server.Maps;
using Content.Server.NPC.HTN;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Warps;
using Content.Server.Worldgen.Components.Debris;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.DroneControl;
using Content.Shared._Crescent.Factions;
using Content.Shared.Shipyard.Components;
using Content.Shared._Crescent.Territory;
using Content.Shared.Dataset;
using Content.Server.GameTicking;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryDronePicketSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly DiplomacySystem _diplomacy = default!;
    [Dependency] private readonly RatDiplomacySystem _factionDiplomacy = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShipSteeringSystem _steering = default!;
    [Dependency] private readonly ShipTargetingSystem _targeting = default!;
    [Dependency] private readonly ShipWeaponArcSystem _arc = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;

    private const float UpdateInterval = 0.25f;

    private const int ScanEveryNTicks = 4;

    private const float LeadingAccuracy = 0.2f;

    private static readonly Angle StrafeOffset = Angle.FromDegrees(20);

    private const int SpawnAttempts = 16;

    private const float SpawnClearance = 60f;

    private float _accumulator;
    private int _tick;

    private EntityQuery<ApcPowerReceiverComponent> _powerQuery;
    private EntityQuery<TerritoryPicketDroneComponent> _picketQuery;

    private List<Entity<MapGridComponent>> _gridScratch = new();
    private readonly HashSet<EntityUid> _picketGrids = new();
    private readonly List<MapCoordinates> _players = new();

    public override void Initialize()
    {
        base.Initialize();

        _powerQuery = GetEntityQuery<ApcPowerReceiverComponent>();
        _picketQuery = GetEntityQuery<TerritoryPicketDroneComponent>();

        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
    }

    private void OnRoundStarted(RoundStartedEvent ev)
    {
        var query = EntityQueryEnumerator<TerritoryDronePicketsComponent, PersistentCaptureRegionComponent>();
        while (query.MoveNext(out var uid, out var pickets, out var region))
        {
            if (region.ValidRegion)
                SpawnPickets((uid, pickets));
        }
    }

    public int SpawnPickets(Entity<TerritoryDronePicketsComponent> standard)
    {
        var xform = Transform(standard);
        if (xform.GridUid is not { } territoryGrid ||
            !TryComp<PersistentPoiGridComponent>(territoryGrid, out var territory) ||
            standard.Comp.Drones.Count == 0)
            return 0;

        var center = _transform.GetMapCoordinates(standard, xform);
        if (center.MapId == MapId.Nullspace)
            return 0;

        // unclaimed ground gets a picket hostile to all, held ground its holder's, sized by level
        standard.Comp.Faction = territory.Owner;
        var count = territory.Owner == null
            ? _random.Next(standard.Comp.MinDrones, standard.Comp.MaxDrones + 1)
            : _levels.GetLevel(territory.Level)?.GarrisonDrones ?? 0;
        var spawned = 0;
        for (var slot = 0; slot < count; slot++)
        {
            var hull = _random.Pick(standard.Comp.Drones);
            if (!_proto.TryIndex(hull, out var gameMap))
                continue;

            var position = PickSpawnSpot(center, standard.Comp.PatrolRange);
            if (!_mapLoader.TryLoadGrid(center.MapId, gameMap.MapPath, out var grid, offset: position))
            {
                Log.Error($"Could not put up a {hull} for the picket of {ToPrettyString(standard)}.");
                continue;
            }

            if (!TryCommandeer(standard, grid.Value.Owner, gameMap, slot))
            {
                Del(grid.Value.Owner);
                continue;
            }

            spawned++;
        }

        return spawned;
    }

    private Vector2 PickSpawnSpot(MapCoordinates center, float ring)
    {
        var position = center.Position;
        for (var attempt = 0; attempt < SpawnAttempts; attempt++)
        {
            var angle = _random.NextFloat(MathF.Tau);
            var radius = ring * _random.NextFloat(0.8f, 1.2f);
            position = center.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

            _gridScratch.Clear();
            var box = Box2.CenteredAround(position, new Vector2(SpawnClearance * 2f));
            _map.FindGridsIntersecting(center.MapId, box, ref _gridScratch, approx: true, includeMap: false);
            if (_gridScratch.Count == 0)
                return position;
        }

        return position;
    }

    private bool TryCommandeer(Entity<TerritoryDronePicketsComponent> standard, EntityUid grid, GameMapPrototype gameMap, int slot)
    {
        EntityUid? server = null;
        var servers = EntityQueryEnumerator<DroneControlComponent, TransformComponent>();
        while (servers.MoveNext(out var uid, out _, out var serverXform))
        {
            if (serverXform.GridUid == grid)
            {
                server = uid;
                break;
            }
        }

        if (server is not { } controller)
        {
            Log.Error($"{gameMap.ID} has no drone control server, so it cannot fly in a territory picket.");
            return false;
        }

        RemComp<AutoDroneComponent>(controller);
        RemComp<DroneControlComponent>(controller);
        RemComp<HTNComponent>(controller);

        var picket = EnsureComp<TerritoryPicketDroneComponent>(controller);
        picket.Standard = standard;
        picket.Slot = slot;
        standard.Comp.Pickets.Add(controller);

        _meta.SetEntityName(grid, DroneName(standard.Comp, gameMap));
        if (standard.Comp.Faction is { } faction)
        {
            _shuttle.SetIFFFaction(grid, faction);
            _shuttle.SetIFFColor(grid, FactionColor(standard, faction));
        }
        else
        {
            _shuttle.SetIFFColor(grid, standard.Comp.IffColor);
        }

        _shuttle.AddIFFFlag(grid, IFFFlags.HideOnMap);

        // no ghost warps to a picket
        var warps = new List<EntityUid>();
        var warpQuery = EntityQueryEnumerator<WarpPointComponent, TransformComponent>();
        while (warpQuery.MoveNext(out var warp, out _, out var warpXform))
        {
            if (warpXform.GridUid == grid)
                warps.Add(warp);
        }

        foreach (var warp in warps)
        {
            RemComp<WarpPointComponent>(warp);
        }

        return true;
    }

    private Color FactionColor(EntityUid standard, string faction)
    {
        if (TryComp<PersistentCaptureRegionComponent>(standard, out var region) &&
            region.FactionColors.TryGetValue(faction, out var color))
        {
            return color;
        }

        return _proto.TryIndex<FactionPrototype>(faction, out var proto) ? proto.FactionButtonColor : Color.White;
    }

    private string DroneName(TerritoryDronePicketsComponent pickets, GameMapPrototype gameMap)
    {
        var prefix = pickets.Faction is { } faction
            ? FactionDisplay.Abbreviation(faction)
            : _proto.TryIndex(pickets.NamePrefixes, out var dataset) && dataset.Values.Count > 0
                ? _random.Pick(dataset.Values)
                : "Turnwracked";

        // "DSM Valor" -> "Valor"
        var words = gameMap.MapName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hull = words.Length > 1 ? string.Join(' ', words[1..]) : gameMap.MapName;

        return $"{prefix} {hull} {_random.Next(256):X2}-{_random.Next(256):X2}";
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < UpdateInterval)
            return;

        _accumulator = 0f;
        var scan = _tick++ % ScanEveryNTicks == 0;
        var gathered = false;

        var query = EntityQueryEnumerator<TerritoryDronePicketsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var pickets, out var xform))
        {
            if (pickets.Pickets.Count == 0)
                continue;

            pickets.Pickets.RemoveAll(drone => TerminatingOrDeleted(drone) || Transform(drone).GridUid == null);
            if (pickets.Pickets.Count == 0)
            {
                pickets.Target = null;
                continue;
            }

            var center = _transform.GetMapCoordinates(uid, xform);
            if (center.MapId == MapId.Nullspace || xform.GridUid is not { } territoryGrid)
                continue;

            if (scan)
            {
                if (!gathered)
                {
                    GatherPlayers();
                    gathered = true;
                }

                var dormant = !IsWatched(center, pickets.WakeRange);
                if (dormant && !pickets.Dormant)
                    Park(pickets);

                pickets.Dormant = dormant;
            }

            if (pickets.Dormant)
                continue;

            if (scan)
                pickets.Target = PickTarget(pickets, center, territoryGrid);

            foreach (var drone in pickets.Pickets)
            {
                Fly(drone, pickets, uid, center, territoryGrid);
            }
        }
    }

    private void GatherPlayers()
    {
        _players.Clear();

        var actors = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (actors.MoveNext(out var uid, out _, out _, out var xform))
        {
            _players.Add(_transform.GetMapCoordinates(uid, xform));
        }
    }

    private bool IsWatched(MapCoordinates position, float range)
    {
        var rangeSquared = range * range;
        foreach (var player in _players)
        {
            if (player.MapId == position.MapId && (player.Position - position.Position).LengthSquared() <= rangeSquared)
                return true;
        }

        return false;
    }

    // nobody is near enough to see them stop, and a hull left drifting never lets physics sleep
    private void Park(TerritoryDronePicketsComponent pickets)
    {
        pickets.Target = null;

        foreach (var drone in pickets.Pickets)
        {
            _steering.Stop(drone);
            _targeting.Stop(drone);

            if (Transform(drone).GridUid is not { } grid || !TryComp<PhysicsComponent>(grid, out var body))
                continue;

            _physics.SetLinearVelocity(grid, Vector2.Zero, body: body);
            _physics.SetAngularVelocity(grid, 0f, body: body);
        }
    }

    private EntityUid? PickTarget(TerritoryDronePicketsComponent pickets, MapCoordinates center, EntityUid territoryGrid)
    {
        if (pickets.Target is { } current && !TerminatingOrDeleted(current) && IsIntruder(current, territoryGrid, pickets.Faction))
        {
            var at = _transform.GetMapCoordinates(current);
            if (at.MapId == center.MapId && (at.Position - center.Position).LengthSquared() <= pickets.Leash * pickets.Leash)
                return current;
        }

        _picketGrids.Clear();
        foreach (var drone in pickets.Pickets)
        {
            if (Transform(drone).GridUid is { } droneGrid)
                _picketGrids.Add(droneGrid);
        }

        EntityUid? best = null;
        var bestDistance = pickets.Range * pickets.Range;

        _gridScratch.Clear();
        var box = Box2.CenteredAround(center.Position, new Vector2(pickets.Range * 2f));
        _map.FindGridsIntersecting(center.MapId, box, ref _gridScratch, approx: true, includeMap: false);
        foreach (var grid in _gridScratch)
        {
            if (_picketGrids.Contains(grid.Owner) || !IsIntruder(grid.Owner, territoryGrid, pickets.Faction))
                continue;

            var distance = (_transform.GetWorldPosition(grid.Owner) - center.Position).LengthSquared();
            if (distance >= bestDistance)
                continue;

            best = grid.Owner;
            bestDistance = distance;
        }

        // somebody out in a suit, on no hull at all
        var players = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (players.MoveNext(out var player, out _, out var mob, out var playerXform))
        {
            if (playerXform.GridUid != null || playerXform.MapID != center.MapId || !_mobState.IsAlive(player, mob) ||
                IsFriend(pickets.Faction, player))
            {
                continue;
            }

            var distance = (_transform.GetWorldPosition(playerXform) - center.Position).LengthSquared();
            if (distance >= bestDistance)
                continue;

            best = player;
            bestDistance = distance;
        }

        return best;
    }

    private bool IsIntruder(EntityUid target, EntityUid territoryGrid, string? faction)
    {
        if (!HasComp<MapGridComponent>(target))
            return HasComp<ActorComponent>(target) && _mobState.IsAlive(target) && !IsFriend(faction, target);

        if (target == territoryGrid ||
            HasComp<PersistentPoiGridComponent>(target) ||
            HasComp<OwnedDebrisComponent>(target) ||
            HasComp<PreventPilotComponent>(target))
            return false;

        // asteroids and dead hulls have no thrust, even with a miner standing on them
        if (!TryComp<ShuttleComponent>(target, out var shuttle) ||
            !shuttle.Enabled ||
            shuttle.ThrustDirections == DirectionFlag.None && shuttle.AngularThrust <= 0f)
            return false;

        // every bare grid's iff faction is "Neutral", which isn't a faction
        if (_diplomacy.GetGridFaction(target) is { } gridFaction)
            return !IsFriendlyFaction(faction, gridFaction);

        if (faction == null && HasComp<ShuttleDeedComponent>(target))
            return true;

        var players = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (players.MoveNext(out var player, out _, out _, out var playerXform))
        {
            if (playerXform.GridUid == target && !IsFriend(faction, player))
                return true;
        }

        return false;
    }

    private bool IsFriend(string? faction, EntityUid person)
    {
        return IsFriendlyFaction(faction, PersistentTerritoryFactions.GetTeam(EntityManager, person));
    }

    // a neutral picket has no friends
    private bool IsFriendlyFaction(string? faction, string? other)
    {
        return faction != null && other != null &&
               _factionDiplomacy.GetRelation(faction, other) == FactionRelation.Alliance;
    }

    private void Fly(EntityUid drone, TerritoryDronePicketsComponent pickets, EntityUid standard, MapCoordinates center,
        EntityUid territoryGrid)
    {
        if (!_picketQuery.TryComp(drone, out var picket))
            return;

        // A drone with its power knocked out drifts
        if (_powerQuery.TryComp(drone, out var receiver) && !_power.IsPowered(drone, receiver))
        {
            _steering.Stop(drone);
            _targeting.Stop(drone);
            return;
        }

        var fromHome = Vector2.Distance(_transform.GetWorldPosition(drone), center.Position);
        if (fromHome > pickets.ReturnRange)
            picket.Returning = true;
        else if (picket.Returning && fromHome <= pickets.Range)
            picket.Returning = false;

        if (!picket.Returning && pickets.Target is { } target && !TerminatingOrDeleted(target))
        {
            Attack(drone, picket, pickets, target, territoryGrid);
            return;
        }

        // nothing in range, or strayed too far: back to circling the territory
        var steer = _steering.Steer(drone, new EntityCoordinates(standard, Vector2.Zero));
        _targeting.Stop(drone);
        if (steer == null)
            return;

        steer.Mode = picket.Slot % 2 == 0 ? ShipSteeringMode.Orbit : ShipSteeringMode.OrbitCW;
        steer.OrbitOffset = Angle.FromDegrees(30);
        steer.Range = pickets.PatrolRange + picket.Slot * pickets.OrbitSpread;
        steer.RangeTolerance = 50f;
        steer.InRangeMaxSpeed = 8f;
        steer.MaxRotateRate = null;
        steer.NoFinish = true;
        steer.LeadingEnabled = false;
        steer.AlwaysFaceTarget = false;
        steer.AvoidCollisions = true;
        steer.AvoidProjectiles = false;
        steer.AvoidTargetGrid = true;
        steer.TargetRotation = 0f;
    }

    private void Attack(EntityUid drone, TerritoryPicketDroneComponent picket, TerritoryDronePicketsComponent pickets,
        EntityUid target, EntityUid territoryGrid)
    {
        // mostly hold range, now and then slide left or right instead of circling all the time
        picket.StrafeLeft -= UpdateInterval;
        if (picket.StrafeLeft <= 0f)
        {
            picket.Strafe = picket.Strafe != 0 ? 0 : _random.Prob(0.5f) ? 1 : -1;
            picket.StrafeLeft = picket.Strafe == 0 ? _random.NextFloat(2f, 5f) : _random.NextFloat(1.5f, 3f);
        }

        var coords = new EntityCoordinates(target, Vector2.Zero);
        var steer = _steering.Steer(drone, coords);
        if (steer != null)
        {
            steer.Mode = picket.Strafe switch
            {
                1 => ShipSteeringMode.Orbit,
                -1 => ShipSteeringMode.OrbitCW,
                _ => ShipSteeringMode.GoToRange,
            };
            steer.OrbitOffset = StrafeOffset;
            steer.Range = pickets.OrbitRange + picket.Slot * pickets.OrbitSpread;
            steer.RangeTolerance = 50f;
            steer.InRangeMaxSpeed = null;
            steer.MaxRotateRate = null;
            steer.NoFinish = true;
            steer.LeadingEnabled = true;
            steer.AlwaysFaceTarget = true;
            steer.AvoidCollisions = true;
            steer.AvoidProjectiles = true;
            steer.AvoidTargetGrid = false;
            if (Transform(drone).GridUid is { } droneGrid)
                steer.TargetRotation = _arc.GetFiringOffset(droneGrid);
        }

        if (TerritoryInLineOfFire(drone, target, territoryGrid))
        {
            _targeting.Stop(drone);
            return;
        }

        // aim somewhere around the target, not dead on it
        var scatter = HasComp<MapGridComponent>(target) ? pickets.AimScatter : pickets.AimScatter * 0.25f;
        var targeting = _targeting.Target(drone, new EntityCoordinates(target, _random.NextVector2(scatter)));
        if (targeting != null)
            targeting.LeadingAccuracy = LeadingAccuracy;
    }

    private bool TerritoryInLineOfFire(EntityUid drone, EntityUid target, EntityUid territoryGrid)
    {
        var start = _transform.GetWorldPosition(drone);
        var end = _transform.GetWorldPosition(target);
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared < 0.01f)
            return false;

        var bounds = _physics.GetWorldAABB(territoryGrid);
        var projection = Math.Clamp(Vector2.Dot(bounds.Center - start, segment) / lengthSquared, 0f, 1f);
        var closest = start + segment * projection;
        return Vector2.DistanceSquared(bounds.Center, closest) <= bounds.Size.LengthSquared() / 4f;
    }
}
