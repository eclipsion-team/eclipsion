using System.Linq;
using System.Numerics;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Tools;
using Content.Shared._Crescent.HeatSeeking;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Worldgen.Systems.Debris;

/// <summary>
///     This handles placing debris within the world evenly with rng, primarily for structures like asteroid fields.
/// </summary>
public sealed class DebrisFeaturePlacerSystem : BaseWorldSystem
{
    [Dependency] private readonly NoiseIndexSystem _noiseIndex = default!;
    [Dependency] private readonly PoissonDiskSampler _sampler = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly SharedMapSystem _mapManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly Robust.Shared.Prototypes.IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly Robust.Shared.EntitySerialization.Systems.MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly Robust.Shared.Timing.IGameTiming _timing = default!;

    private ISawmill _sawmill = default!;

    private const double SpawnBudgetMs = 5;
    private readonly Queue<(EntityUid Placer, EntityUid Chunk, Vector2 Point)> _pendingDebris = new();

    /// <inheritdoc />
    public override void Initialize()
    {
        _sawmill = _logManager.GetSawmill("world.debris.feature_placer");
        // Debug: enumerate all loaded gameMap prototypes so we can confirm GargutWreckFull is registered
        try
        {
            var maps = _prototypeManager.EnumeratePrototypes<Content.Server.Maps.GameMapPrototype>().ToList();
            _sawmill.Debug($"[DebrisPlacer] Loaded GameMapPrototype count: {maps.Count}. IDs: {string.Join(", ", maps.Select(m => m.ID))}");
        }
        catch (Exception ex)
        {
            _sawmill.Error($"[DebrisPlacer] Exception enumerating GameMap prototypes: {ex}");
        }
        SubscribeLocalEvent<DebrisFeaturePlacerControllerComponent, WorldChunkLoadedEvent>(OnChunkLoaded);
        SubscribeLocalEvent<DebrisFeaturePlacerControllerComponent, WorldChunkUnloadedEvent>(OnChunkUnloaded);
        SubscribeLocalEvent<OwnedDebrisComponent, ComponentShutdown>(OnDebrisShutdown);
        SubscribeLocalEvent<OwnedDebrisComponent, MoveEvent>(OnDebrisMove);
        SubscribeLocalEvent<SimpleDebrisSelectorComponent, TryGetPlaceableDebrisFeatureEvent>(
            OnTryGetPlacableDebrisEvent);
    }

    /// <summary>
    ///     Handles debris moving, and making sure it stays parented to a chunk for loading purposes.
    /// </summary>
    private void OnDebrisMove(EntityUid uid, OwnedDebrisComponent component, ref MoveEvent args)
    {
        if (!TryComp<WorldChunkComponent>(component.OwningController, out var ownerChunk))
            return; // Redundant logic, prolly needs it's own handler for your custom system.

        if (!TryComp<DebrisFeaturePlacerControllerComponent>(component.OwningController, out var placer))
            return;

        var xform = args.Component;
        var ownerXform = Transform(component.OwningController);
        if (xform.MapUid is null || ownerXform.MapUid is null)
            return; // not our problem

        if (xform.MapUid != ownerXform.MapUid)
        {
            _sawmill.Error($"Somehow debris {uid} left it's expected map! Unparenting it to avoid issues.");
            RemCompDeferred<OwnedDebrisComponent>(uid);
            placer.OwnedDebris.Remove(component.LastKey);
            return;
        }

        // Re-index only when the debris crosses into another chunk.
        var newCoords = GetChunkCoords(uid, xform);
        if (newCoords == ownerChunk.Coordinates)
            return;

        placer.OwnedDebris.Remove(component.LastKey);
        var newChunk = GetOrCreateChunk(newCoords, xform.MapUid!.Value);
        if (newChunk is null || !TryComp<DebrisFeaturePlacerControllerComponent>(newChunk, out var newPlacer))
        {
            // Whelp.
            RemCompDeferred<OwnedDebrisComponent>(uid);
            return;
        }

        // Keep the component and its new owner on the same chunk key.
        var newKey = _xformSys.GetWorldPosition(xform);
        newPlacer.OwnedDebris[newKey] = uid;
        component.LastKey = newKey;
        component.OwningController = newChunk.Value;
    }

    /// <summary>
    ///     Handles debris shutdown/detach.
    /// </summary>
    private void OnDebrisShutdown(EntityUid uid, OwnedDebrisComponent component, ComponentShutdown args)
    {
        if (!TryComp<DebrisFeaturePlacerControllerComponent>(component.OwningController, out var placer))
            return;

        placer.OwnedDebris[component.LastKey] = null;
        if (Terminating(uid))
            placer.OwnedDebris.Remove(component.LastKey);
    }

    /// <summary>
    /// </summary>
    private void OnChunkUnloaded(EntityUid uid, DebrisFeaturePlacerControllerComponent component,
        ref WorldChunkUnloadedEvent args)
    {
        if (component.Regenerate)
            component.DoSpawns = true;
    }

    /// <summary>
    ///     Handles providing a debris type to place for SimpleDebrisSelectorComponent.
    ///     This randomly picks a debris type from the EntitySpawnCollectionCache.
    /// </summary>
    private void OnTryGetPlacableDebrisEvent(EntityUid uid, SimpleDebrisSelectorComponent component,
        ref TryGetPlaceableDebrisFeatureEvent args)
    {
        if (args.DebrisProto is not null)
            return;

        var l = new List<string?>(1);
        component.CachedDebrisTable.GetSpawns(_random, ref l);

        switch (l.Count)
        {
            case 0:
                return;
            case > 1:
                _sawmill.Warning($"Got more than one possible debris type from {uid}. List: {string.Join(", ", l)}");
                break;
        }

        args.DebrisProto = l[0];
    }

    /// <summary>
    ///     Handles loading in debris. This does the following:
    ///     - Checks if the debris is currently supposed to do spawns, if it isn't, aborts immediately.
    ///     - Evaluates the density value to be used for placement, if it's zero, aborts.
    ///     - Generates the points to generate debris at, if and only if they've not been selected already by a prior load.
    ///     - Does the following in a loop over all generated points:
    ///         - Raises an event to check if something else wants to intercept debris placement, if the event is handled,
    ///           continues to the next point without generating anything.
    ///         - Raises an event to get the debris type that should be used for generation.
    ///         - Spawns the given debris at the point, adding it to the placer's index.
    /// </summary>
    private void OnChunkLoaded(EntityUid uid, DebrisFeaturePlacerControllerComponent component,
        ref WorldChunkLoadedEvent args)
    {
        if (component.DoSpawns == false)
            return;

        component.DoSpawns = false; // Don't repeat yourself if this crashes.

        var chunk = Comp<WorldChunkComponent>(args.Chunk);
        var densityChannel = component.DensityNoiseChannel;
        var density = _noiseIndex.Evaluate(uid, densityChannel, chunk.Coordinates + new Vector2(0.5f, 0.5f));
        if (density == 0)
            return;

        List<Vector2>? points = null;

        // If we've been loaded before, reuse the same coordinates.
        if (component.OwnedDebris.Count != 0)
        {
            //TODO: Remove LINQ.
            points = component.OwnedDebris
                .Where(x => !Deleted(x.Value))
                .Select(static x => x.Key)
                .ToList();
        }

        points ??= GeneratePointsInChunk(args.Chunk, density, chunk.Coordinates, chunk.Map);

        // spawned a few per tick in Update, a whole chunk at once lags
        foreach (var point in points)
        {
            _pendingDebris.Enqueue((uid, args.Chunk, point));
        }
    }

    public override void Update(float frameTime)
    {
        if (_pendingDebris.Count == 0)
            return;

        var start = _timing.RealTime;
        var done = 0;
        var tried = 0;
        while (_pendingDebris.Count > 0)
        {
            // points that spawn nothing still cost a noise lookup and a grid query, so they count too after a while
            if ((done > 0 || tried >= 200) && (_timing.RealTime - start).TotalMilliseconds > SpawnBudgetMs)
                break;

            tried++;

            var (uid, chunkUid, point) = _pendingDebris.Dequeue();
            if (!TryComp<DebrisFeaturePlacerControllerComponent>(uid, out var component) ||
                !TryComp<WorldChunkComponent>(chunkUid, out var chunk))
                continue;

            if (PlaceDebris(uid, component, chunkUid, chunk, point))
                done++;
        }
    }

    private bool PlaceDebris(EntityUid uid, DebrisFeaturePlacerControllerComponent component, EntityUid chunkUid,
        WorldChunkComponent chunk, Vector2 point)
    {
        if (component.OwnedDebris.TryGetValue(point, out var existing))
        {
            DebugTools.Assert(Exists(existing));
            return false;
        }

        var pointDensity = _noiseIndex.Evaluate(uid, component.DensityNoiseChannel, WorldGen.WorldToChunkCoords(point));
        if (pointDensity == 0 && component.DensityClip || _random.Prob(component.RandomCancellationChance))
            return false;

        if (!TryComp<MapComponent>(chunk.Map, out var mapComp))
            return false;

        var coords = new EntityCoordinates(chunk.Map, point);
        var safetyBounds = Box2.UnitCentered.Enlarged(component.SafetyZoneRadius);

        // checked when spawning, a ship might have moved here since the chunk loaded
        var intersectingGrids = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(mapComp.MapId, safetyBounds.Translated(point), ref intersectingGrids, false, false);
        if (intersectingGrids.Count > 0)
            return false; // Oops, gonna collide.

        var preEv = new PrePlaceDebrisFeatureEvent(coords, chunkUid);
        RaiseLocalEvent(uid, ref preEv);
        if (uid != chunkUid)
            RaiseLocalEvent(chunkUid, ref preEv);

        if (preEv.Handled)
            return false;

        var debrisFeatureEv = new TryGetPlaceableDebrisFeatureEvent(coords, chunkUid);
        RaiseLocalEvent(uid, ref debrisFeatureEv);

        if (debrisFeatureEv.DebrisProto == null)
        {
            // Try on the chunk...?
            if (uid != chunkUid)
                RaiseLocalEvent(chunkUid, ref debrisFeatureEv);

            // Not a failure: noise-driven selectors deliberately return nothing on low-noise points
            // (e.g. WreckGraveyard clips 0-0.4 to zero) to keep wrecks sparse.
            if (debrisFeatureEv.DebrisProto == null)
                return false;
        }

        // If the selected prototype is a gameMap, load it as a new grid instead of spawning as an entity.
        if (_prototypeManager.TryIndex<Content.Server.Maps.GameMapPrototype>(debrisFeatureEv.DebrisProto, out var gameMapProto))
        {
            var mapPath = gameMapProto.MapPath;

            // wreck files contain grids, not maps. offset puts it at the point instead of 0,0
            if (!_mapLoader.TryLoadGrid(mapComp.MapId, mapPath, out var gridUid, offset: coords.Position) || gridUid == null)
            {
                _sawmill.Error($"Failed to place debris {debrisFeatureEv.DebrisProto} at chunk {chunkUid} at {point}");
                return true;
            }

            var grid = gridUid.Value;
            // GameMap station `components` are not applied by TryLoadGrid; match round-start setup for important markers.
            EnsureComp<IgnoreHeatSeekingTargetComponent>(grid);
            component.OwnedDebris.Add(point, grid);

            var owned = EnsureComp<OwnedDebrisComponent>(grid);
            owned.OwningController = uid;
            owned.LastKey = point;
            return true;
        }

        var ent = Spawn(debrisFeatureEv.DebrisProto, coords);
        component.OwnedDebris.Add(point, ent);

        var ownedEnt = EnsureComp<OwnedDebrisComponent>(ent);
        ownedEnt.OwningController = uid;
        ownedEnt.LastKey = point;
        return true;
    }

    /// <summary>
    ///     Generates the points to put into a chunk using a poisson disk sampler.
    /// </summary>
    private List<Vector2> GeneratePointsInChunk(EntityUid chunk, float density, Vector2 coords, EntityUid map)
    {
        var offs = (int) ((WorldGen.ChunkSize - WorldGen.ChunkSize / 8.0f) / 2.0f);
        var topLeft = new Vector2(-offs, -offs);
        var lowerRight = new Vector2(offs, offs);
        var enumerator = _sampler.SampleRectangle(topLeft, lowerRight, density);
        var debrisPoints = new List<Vector2>();

        var realCenter = WorldGen.ChunkToWorldCoordsCentered(coords.Floored());

        while (enumerator.MoveNext(out var debrisPoint))
        {
            debrisPoints.Add(realCenter + debrisPoint.Value);
        }

        return debrisPoints;
    }
}

/// <summary>
///     Fired directed on the debris feature placer controller and the chunk, ahead of placing a debris piece.
/// </summary>
[ByRefEvent]
[PublicAPI]
public record struct PrePlaceDebrisFeatureEvent(EntityCoordinates Coords, EntityUid Chunk, bool Handled = false);

/// <summary>
///     Fired directed on the debris feature placer controller and the chunk, to select which debris piece to place.
/// </summary>
[ByRefEvent]
[PublicAPI]
public record struct TryGetPlaceableDebrisFeatureEvent(EntityCoordinates Coords, EntityUid Chunk,
    string? DebrisProto = null);

