using System.Collections.Generic;
using Content.Server.Explosion.EntitySystems;
using Content.Shared.Explosion;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Explosion;

// weak blasts stop at plating, a big one (AME) goes down to space
[TestFixture]
public sealed class ExplosionFloorLayerTest
{
    [Test]
    public async Task StrongBlastGoesPastPlating()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var explosions = server.System<ExplosionSystem>();
        var mapSys = server.System<SharedMapSystem>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var type = protos.Index<ExplosionPrototype>("Default");
            var floor = new Tile(tileDefs["FloorSteel"].TileId);
            var indices = new Vector2i(0, 0);
            mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, indices, floor);
            var tileRef = mapSys.GetTileRef(map.Grid.Owner, map.Grid.Comp, indices);

            // these numbers are already divided by 15, same as the explosion processing does
            var weak = new List<(Vector2i, Tile)>();
            explosions.DamageFloorTile(tileRef, 40f, int.MaxValue, true, weak, type);
            Assert.That(weak, Has.Count.EqualTo(1));
            Assert.That(weak[0].Item2.TypeId, Is.EqualTo(tileDefs["Plating"].TileId), "weak blast should stop at plating");

            var strong = new List<(Vector2i, Tile)>();
            explosions.DamageFloorTile(tileRef, 130f, int.MaxValue, true, strong, type);
            Assert.That(strong, Has.Count.EqualTo(1));
            Assert.That(strong[0].Item2.IsEmpty || strong[0].Item2.IsSpace(tileDefs), "strong blast should clear the tile");
        });

        await pair.CleanReturnAsync();
    }
}
