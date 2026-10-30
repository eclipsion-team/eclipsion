using System.Numerics;
using Content.Server.StationEvents.Events;
using Content.Server.GameTicking;
using Content.Server.Anomaly;
using Content.Server._Crescent.SpaceEvents.Components;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.EntitySerialization.Systems;

namespace Content.Server._Crescent.SpaceEvents;

public sealed class AnomalyStationRule : StationEventSystem<AnomalyStationRuleComponent>
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _time = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly AnomalySystem _anomaly = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    protected override void Added(EntityUid uid, AnomalyStationRuleComponent component, GameRuleComponent gameRule, GameRuleAddedEvent args)
    {
        base.Added(uid, component, gameRule, args);

        MapId map = _gameTicker.DefaultMap;
        Vector2 randomCoords;

        do
        {
            randomCoords = _random.NextVector2Box(
                component.MinCoord, component.MinCoord,
                component.MaxCoord, component.MaxCoord
            );
        }
        while (!CheckNearGrids(randomCoords, map));

        component.Coordinates = randomCoords;

        ChatSystem.DispatchGlobalAnnouncement(
            Loc.GetString(component.Announcement,
                ("x", (int)component.Coordinates.X),
                ("y", (int)component.Coordinates.Y)),
            colorOverride: Color.DeepPink);
    }

    protected override void Started(EntityUid uid,
     AnomalyStationRuleComponent comp, GameRuleComponent gameRule,
     GameRuleStartedEvent args)
    {
        base.Started(uid, comp, gameRule, args);

        // No round map yet (lobby, or the rule spawned on its own).
        if (!_mapSystem.MapExists(_gameTicker.DefaultMap))
        {
            ForceEndSelf(uid, gameRule);
            return;
        }

        SpawnAnomalyStation(comp);
    }

    private void SpawnAnomalyStation(AnomalyStationRuleComponent comp)
    {
        var map = _gameTicker.DefaultMap;

        if (_loader.TryLoadGrid(map, new ResPath(comp.StationMapPath), out var grid,
         offset: comp.Coordinates))
        {
            if (grid is null) return;

            var amountToSpawn = 4;

            // TODO: Fuck AnomalySystem dependence
            for (var i = 0; i < amountToSpawn; i++)
                _anomaly.SpawnOnRandomGridLocation(grid.Value, comp.ArtifactSpawnerPrototype);
        }

        return;
    }

    private bool CheckNearGrids(Vector2 coords, MapId mapId)
    {
        var mapCoords = new MapCoordinates(coords, mapId);
        var nearGrids = _lookup.GetEntitiesInRange<MapGridComponent>(mapCoords, 200f);
        if (nearGrids.Count > 0)
        {
            return false;
        }

        return true;
    }
}