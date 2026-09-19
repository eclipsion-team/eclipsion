using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server._Crescent.DroneControl;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Shuttle;

[TestFixture]
public sealed class DroneSpawnProtectionTest
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task ProtectionPreventsDamageToBothHullsUntilExpiry(bool protectTarget, bool expired)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var tileId = server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;
        EntityUid rammer = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            entities.DeleteEntity(map.Grid);
            var rammerGrid = maps.CreateGridEntity(map.MapId);
            var targetGrid = maps.CreateGridEntity(map.MapId);
            rammer = rammerGrid.Owner;
            target = targetGrid.Owner;
            maps.SetTiles(rammer, rammerGrid.Comp, Block(12, 6, tileId));
            maps.SetTiles(target, targetGrid.Comp, Block(20, 20, tileId));
            transforms.SetLocalPosition(target, new Vector2(30f, -7f));

            entities.AddComponent<DroneSpawnProtectionComponent>(protectTarget ? target : rammer).ExpiresAt =
                timing.CurTime + (expired ? TimeSpan.Zero : TimeSpan.FromMinutes(2));

            var body = entities.GetComponent<PhysicsComponent>(rammer);
            physics.SetBodyType(rammer, BodyType.Dynamic, body: body);
            physics.SetLinearDamping(rammer, body, 0f);
            physics.SetLinearVelocity(rammer, new Vector2(40f, 0f), body: body);
            physics.WakeBody(rammer, body: body);
        });

        await pair.RunSeconds(2f);

        await server.WaitAssertion(() =>
        {
            var rammerTiles = CountTiles(rammer);
            var targetTiles = CountTiles(target);
            if (expired)
            {
                Assert.That(rammerTiles + targetTiles, Is.LessThan(12 * 6 + 20 * 20),
                    "Impact damage must resume once spawn protection expires.");
            }
            else
            {
                Assert.That(rammerTiles, Is.EqualTo(12 * 6), "The rammer took impact damage.");
                Assert.That(targetTiles, Is.EqualTo(20 * 20), "The target took impact damage.");
                Assert.That(entities.GetComponent<TransformComponent>(rammer).LocalPosition.X, Is.LessThan(30f),
                    "Physical contact must still stop the drone.");
            }
        });

        await pair.CleanReturnAsync();
        return;

        int CountTiles(EntityUid grid)
        {
            if (!entities.TryGetComponent(grid, out MapGridComponent? comp))
                return 0;

            var count = 0;
            var enumerator = maps.GetAllTilesEnumerator(grid, comp);
            while (enumerator.MoveNext(out _))
                count++;
            return count;
        }
    }

    private static List<(Vector2i, Tile)> Block(int width, int height, int tileId)
    {
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            tiles.Add((new Vector2i(x, y), new Tile(tileId)));
        return tiles;
    }
}
