using System.Numerics;
using Content.Server._Crescent.Territory;
using Content.Server.Power.EntitySystems;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Timing;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class PersistentPoiReloadTest
{
    [Test]
    public async Task ReloadedTerritoryKeepsGravityAndUse()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var saves = server.System<PersistentPoiSaveSystem>();
        var useDelay = server.System<UseDelaySystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var key = $"reload-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid? loaded = null;
        MapId pausedMap = default;

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            grid = gridEnt.Owner;
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 4; x++)
            {
                mapSystem.SetTile(gridEnt, new Vector2i(x, 0), new Tile(plating.TileId));
            }

            entMan.EnsureComponent<GravityComponent>(grid);

            var gen = entMan.SpawnEntity("GravityGeneratorMini", new EntityCoordinates(grid, 0.5f, 0.5f));
            server.System<PowerReceiverSystem>().SetNeedsPower(gen, false);

            var button = entMan.SpawnEntity("SignalButton", new EntityCoordinates(grid, 2.5f, 0.5f));
            saves.MarkPersistent(grid, key);
            var delay = entMan.GetComponent<UseDelayComponent>(button);
            Assert.That(useDelay.TryGetDelayInfo((button, delay), out var info), Is.True);
            // as if pressed late in a round on a server that has since restarted
            info!.EndTime = timing.CurTime + TimeSpan.FromHours(6);
        });

        await server.WaitRunTicks(60);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GravityComponent>(grid).Enabled, Is.True, "gravity before the save");
            Assert.That(saves.TrySave((grid, entMan.GetComponent<PersistentPoiSaveComponent>(grid))), Is.True);
            entMan.DeleteEntity(grid);
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            mapSystem.CreateMap(out pausedMap, runMapInit: false);
            Assert.That(saves.TryLoad(pausedMap, key, 1, new ResPath("/Maps/none.yml"), Vector2.Zero, out loaded),
                Is.True);
            mapSystem.InitializeMap(pausedMap);
        });

        await server.WaitRunTicks(60);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GravityComponent>(loaded!.Value).Enabled, Is.True, "gravity after the load");

            var buttons = 0;
            var query = entMan.AllEntityQueryEnumerator<UseDelayComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var delay, out var xform))
            {
                if (xform.GridUid != loaded)
                    continue;

                buttons++;
                Assert.That(useDelay.IsDelayed((uid, delay)), Is.False, "use delay after the load");
            }

            Assert.That(buttons, Is.EqualTo(1));

            mapSystem.DeleteMap(pausedMap);
            saves.Reset(key);
        });

        await pair.CleanReturnAsync();
    }
}
