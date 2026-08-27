using Content.Shared._Crescent.RoundEnd;
using Content.Server._Crescent.ShipShields;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.RoundEnd;

/// <summary>
/// Slowly corrupts a conquered station without deleting the grid. The caps are intentionally conservative: the
/// infestation is an environmental consequence and a cleanup threat, not an unlimited NPC generator.
/// </summary>
public sealed class StationInfestationSystem : EntitySystem
{
    private static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(45);

    private static readonly string[] FleshMobs =
    {
        "MobFleshJared",
        "MobFleshGolem",
        "MobFleshClamp",
        "MobFleshLover",
    };

    private const string FleshTile = "FloorFlesh";
    private const int FleshTilesPerPulse = 2;
    private const int MaxInfestedTiles = 24;
    private const int MaxRemovedTiles = 8;
    private const int MaxLivingMobs = 5;
    private const int MaxTotalMobs = 12;
    private const int MinimumTilesForErosion = 20;
    private const float MobSpawnChance = 0.35f;

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinitions = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly ShipShieldsSystem _shipShields = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<FactionStationFellEvent>(OnStationFell);
      //  SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentStartup>(OnShieldEmitterStartup);
    }

   // private void OnShieldEmitterStartup(Entity<ShipShieldEmitterComponent> ent, ref ComponentStartup args)
   // {
   //     var grid = Transform(ent).GridUid;
   //     if (grid != null && HasComp<StationInfestationComponent>(grid.Value))
   //         _shipShields.SetForcedDisabled(ent, true, ent.Comp);
   // }

    private void OnStationFell(ref FactionStationFellEvent ev)
    {
        if (HasComp<StationInfestationComponent>(ev.Station) ||
            !TryComp<MapGridComponent>(ev.Station, out var grid))
            return;

        var infestation = AddComp<StationInfestationComponent>(ev.Station);
        infestation.NextPulse = _timing.CurTime;
        infestation.CandidateTiles = _map.GetAllTiles(ev.Station, grid)
            .Select(tile => tile.GridIndices)
            .ToList();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<StationInfestationComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var infestation, out var grid))
        {
            if (_timing.CurTime < infestation.NextPulse)
                continue;

            infestation.NextPulse = _timing.CurTime + PulseInterval;
            Pulse(uid, infestation, grid);
        }
    }

    private void Pulse(EntityUid uid, StationInfestationComponent infestation, MapGridComponent grid)
    {
        SpreadFlesh(uid, infestation, grid);

        // Begin opening small holes only after the visual infestation has had time to establish itself, then at
        // most once every other pulse. Even a long-lived infestation removes no more than eight tiles.
        if (infestation.Pulses > 0 && infestation.Pulses % 2 == 0)
            ErodeTile(uid, infestation, grid);

        SpawnFleshMob(uid, infestation, grid);
        infestation.Pulses++;
    }

    private void SpreadFlesh(EntityUid uid, StationInfestationComponent infestation, MapGridComponent grid)
    {
        if (infestation.InfestedTiles.Count >= MaxInfestedTiles)
            return;

        var flesh = _tileDefinitions[FleshTile];
        for (var i = 0; i < FleshTilesPerPulse && infestation.InfestedTiles.Count < MaxInfestedTiles; i++)
        {
            if (!TryPickIntactTile(uid, infestation, grid, out var indices) ||
                !infestation.InfestedTiles.Add(indices))
                continue;

            _map.SetTile(uid, grid, indices, new Tile(flesh.TileId));
        }
    }

    private void ErodeTile(EntityUid uid, StationInfestationComponent infestation, MapGridComponent grid)
    {
        if (infestation.CandidateTiles.Count < MinimumTilesForErosion ||
            infestation.RemovedTiles.Count >= MaxRemovedTiles ||
            !TryPickIntactTile(uid, infestation, grid, out var indices, requireClear: true))
            return;

        infestation.RemovedTiles.Add(indices);
        _map.SetTile(uid, grid, indices, Tile.Empty);
    }

    private void SpawnFleshMob(EntityUid uid, StationInfestationComponent infestation, MapGridComponent grid)
    {
        infestation.SpawnedMobs.RemoveWhere(mob =>
            TerminatingOrDeleted(mob) ||
            !TryComp<MobStateComponent>(mob, out var state) ||
            state.CurrentState != MobState.Alive);

        // The first pulse always establishes one threat. Later pulses are chance-based and never allow more than
        // five living creatures from this infestation at once, or twelve over the station's entire lifetime.
        if (infestation.SpawnedMobs.Count >= MaxLivingMobs ||
            infestation.TotalMobsSpawned >= MaxTotalMobs ||
            (infestation.Pulses > 0 && !_random.Prob(MobSpawnChance)) ||
            !TryPickIntactTile(uid, infestation, grid, out var indices, requireClear: true))
            return;

        var coords = _map.GridTileToLocal(uid, grid, indices);
        infestation.SpawnedMobs.Add(Spawn(_random.Pick(FleshMobs), coords));
        infestation.TotalMobsSpawned++;
    }

    private bool TryPickIntactTile(EntityUid uid, StationInfestationComponent infestation, MapGridComponent grid,
        out Vector2i indices, bool requireClear = false)
    {
        indices = default;
        if (infestation.CandidateTiles.Count == 0)
            return false;

        // Avoid scanning a potentially very large station for every pulse. Random retries are enough because only
        // a few dozen candidates can ever be changed by this system.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = _random.Pick(infestation.CandidateTiles);
            if (infestation.RemovedTiles.Contains(candidate) ||
                !_map.TryGetTileRef(uid, grid, candidate, out var tile) ||
                tile.Tile.IsEmpty ||
                (requireClear && _turf.IsTileBlocked(tile, CollisionGroup.MobMask)))
                continue;

            indices = candidate;
            return true;
        }

        return false;
    }
}
