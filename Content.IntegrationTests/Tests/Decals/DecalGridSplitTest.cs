using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Decals;
using Content.Shared.Decals;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Decals;

// decals have to follow their tiles when a grid splits
[TestFixture]
public sealed class DecalGridSplitTest
{
    [Test]
    public async Task DecalsFollowSplitOffPiece()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSys = server.System<SharedMapSystem>();
        var xformSys = server.System<SharedTransformSystem>();
        var decals = server.System<DecalSystem>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

        EntityUid gridUid = default;
        Vector2 decalWorld = default;

        await server.WaitPost(() =>
        {
            var grid = mapSys.CreateGridEntity(map.MapId);
            gridUid = grid.Owner;
            xformSys.SetWorldPosition(gridUid, new Vector2(50f, 50f));

            var plating = new Tile(tileDefs["Plating"].TileId);
            for (var x = 0; x < 15; x++)
            {
                mapSys.SetTile(gridUid, grid.Comp, new Vector2i(x, 0), plating);
            }

            Assert.That(decals.TryAddDecal("Arrows", new EntityCoordinates(gridUid, new Vector2(2f, 0f)), out _));
            decalWorld = Vector2.Transform(new Vector2(2f, 0f), xformSys.GetWorldMatrix(gridUid));
        });

        await server.WaitRunTicks(5);

        // cut it in two, tiles 0-4 are the smaller piece and get split off
        await server.WaitPost(() =>
        {
            mapSys.SetTile(gridUid, entMan.GetComponent<MapGridComponent>(gridUid), new Vector2i(5, 0), Tile.Empty);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var pieces = new List<EntityUid>();
            var query = entMan.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.MapID == map.MapId && uid != gridUid && uid != map.Grid.Owner)
                    pieces.Add(uid);
            }

            Assert.That(pieces, Has.Count.EqualTo(1), "grid never split");
            var piece = pieces[0];

            Assert.That(CountDecals(entMan, gridUid), Is.EqualTo(0), "decal stayed on the old grid");

            var moved = entMan.GetComponent<DecalGridComponent>(piece).ChunkCollection.ChunkCollection.Values
                .SelectMany(c => c.Decals.Values)
                .ToList();
            Assert.That(moved, Has.Count.EqualTo(1));

            var world = Vector2.Transform(moved[0].Coordinates, xformSys.GetWorldMatrix(piece));
            Assert.That((world - decalWorld).Length(), Is.LessThan(0.01f), "decal moved to the wrong spot");
        });

        await pair.CleanReturnAsync();
    }

    private static int CountDecals(IEntityManager entMan, EntityUid grid)
    {
        if (!entMan.TryGetComponent<DecalGridComponent>(grid, out var comp))
            return 0;

        return comp.ChunkCollection.ChunkCollection.Values.Sum(c => c.Decals.Count);
    }
}
