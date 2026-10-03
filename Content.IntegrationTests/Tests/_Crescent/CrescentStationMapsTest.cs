#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Server.Maps;
using Content.Server.Station.Systems;
using Content.Server._Crescent.GreatHunt;
using Content.Shared._Crescent.Factions;
using Content.Shared.Shipyard;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Loads the Gliess Santo, Analiesse, Great Hunt and Refuge station maps the way a round does, and checks that the
/// Hall of Prays resolves its altar spawn points into exactly one Great Altar, and that the Great Hunt home-base
/// copies only sell ships from a faction-locked yard.
/// </summary>
[TestFixture]
public sealed class CrescentStationMapsTest
{
    private static readonly string[] StationMapIds =
        ["GliessSanto", "Analiesse", "HallOfPrays", "Refuge", "RefugeGreatHunt", "TribalHideoutGreatHunt"];

    [Test, TestCaseSource(nameof(StationMapIds))]
    public async Task StationMapLoads(string mapId)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true // Station initialization creates nullspace entities that outlive the loaded grid.
        });
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var stationSystem = entManager.System<StationSystem>();

        MapId testMap = default;
        EntityUid grid = default;

        await server.WaitPost(() =>
        {
            var mapProto = protoManager.Index<GameMapPrototype>(mapId);
            mapSystem.CreateMap(out testMap);

            Assert.That(mapLoader.TryLoadGrid(testMap, mapProto.MapPath, out var loaded), Is.True,
                $"Failed to load {mapProto.MapPath}.");
            Assert.That(mapProto.Stations.TryGetValue(mapId, out var config), Is.True,
                $"{mapId}'s station key must match its gameMap id.");

            grid = loaded!.Value.Owner;
            stationSystem.InitializeNewStation(config!, new[] { grid });
        });

        // Altar spawn points resolve on the update after map init.
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var altars = new List<EntityUid>();
            var altarQuery = entManager.AllEntityQueryEnumerator<GreatAltarComponent, TransformComponent>();
            while (altarQuery.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.GridUid == grid)
                    altars.Add(uid);
            }

            var markers = 0;
            var markerQuery = entManager.AllEntityQueryEnumerator<GreatAltarSpawnPointComponent, TransformComponent>();
            while (markerQuery.MoveNext(out _, out _, out var xform))
            {
                if (xform.GridUid == grid)
                    markers++;
            }

            var yards = new List<EntityUid>();
            var yardQuery = entManager.AllEntityQueryEnumerator<ShipyardConsoleComponent, TransformComponent>();
            while (yardQuery.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.GridUid == grid)
                    yards.Add(uid);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(markers, Is.Zero, $"{mapId} still has unresolved Great Altar spawn points.");
                Assert.That(altars, Has.Count.EqualTo(mapId == "HallOfPrays" ? 1 : 0),
                    $"{mapId} has the wrong number of Great Altars.");

                // The Great Hunt copies drop the civilian and scrap yards: the faction yard is the only one left.
                if (mapId.EndsWith("GreatHunt"))
                {
                    Assert.That(yards, Has.Count.EqualTo(1), $"{mapId} should have exactly one shipyard.");
                    Assert.That(yards.All(y => entManager.HasComponent<FactionMachineComponent>(y)), Is.True,
                        $"{mapId}'s shipyard must be a faction-locked one.");
                }
            }

            mapSystem.DeleteMap(testMap);
        });

        await server.WaitRunTicks(1);
        await pair.CleanReturnAsync();
    }
}
