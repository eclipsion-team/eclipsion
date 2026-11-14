#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Gravity;
using Content.Shared._Crescent.Misc;
using Content.Shared.Conveyor;
using Content.Shared.Gravity;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class ConveyorSpawnedItemTest
{
    private static EntityUid SpawnBelt(IEntityManager entMan, EntityUid grid, int x, int y, Angle rotation)
    {
        var belt = entMan.SpawnEntity("ConveyorBelt", new EntityCoordinates(grid, x + 0.5f, y + 0.5f));
        entMan.System<SharedTransformSystem>().SetLocalRotation(belt, rotation);
        var conveyor = entMan.GetComponent<ConveyorComponent>(belt);
        conveyor.State = ConveyorState.Forward;
        conveyor.Powered = true;
        return belt;
    }

    private static async Task<(TestPair pair, TestMapData map)> Setup()
    {
        var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;

        await server.WaitPost(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            for (var x = 0; x < 6; x++)
            for (var y = 0; y < 3; y++)
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);

            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });

        return (pair, map);
    }

    [Test]
    public async Task ItemSpawnedOnRunningBeltMoves()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var entMan = server.EntMan;
        EntityUid ore = default;

        await server.WaitPost(() =>
        {
            for (var x = 1; x < 5; x++)
                SpawnBelt(entMan, map.Grid, x, 1, Angle.FromDegrees(90));

            ore = entMan.SpawnEntity("SteelOre", new EntityCoordinates(map.Grid, 1.5f, 1.5f));
        });

        await pair.RunTicksSync(90);

        await server.WaitAssertion(() =>
        {
            var pos = entMan.GetComponent<TransformComponent>(ore).LocalPosition;
            Assert.That(pos.X, Is.GreaterThan(2.5f), $"Ore spawned on a running belt did not move (at {pos}).");
            Assert.That(pos.Y, Is.EqualTo(1.5f).Within(0.1f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task JammedItemRestsAndMovesOnWhenFreed()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var entMan = server.EntMan;
        var conveyor = server.System<Content.Server.Physics.Controllers.ConveyorController>();
        EntityUid ore = default;
        EntityUid wall = default;

        await server.WaitPost(() =>
        {
            for (var x = 1; x < 4; x++)
                SpawnBelt(entMan, map.Grid, x, 1, Angle.FromDegrees(90));

            wall = entMan.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, 4.5f, 1.5f));
            ore = entMan.SpawnEntity("SteelOre", new EntityCoordinates(map.Grid, 1.5f, 1.5f));
        });

        // belt end is a wall, so the ore gets stuck against it and should be left alone for a while
        var rested = false;
        for (var i = 0; i < 30 && !rested; i++)
        {
            await pair.RunTicksSync(5);
            await server.WaitPost(() => rested = conveyor.IsResting(ore));
        }

        Assert.That(rested, Is.True, "Ore stuck against a wall was still pushed every tick.");

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(wall);
            SpawnBelt(entMan, map.Grid, 4, 1, Angle.FromDegrees(90));
        });

        await pair.RunTicksSync(150);

        await server.WaitAssertion(() =>
        {
            var pos = entMan.GetComponent<TransformComponent>(ore).LocalPosition;
            Assert.That(pos.X, Is.GreaterThan(4.3f), $"Ore didn't move on after the jam cleared (at {pos}).");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DrillOutputsOntoAdjacentBelt()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var entMan = server.EntMan;

        await server.WaitPost(() =>
        {
            // West belt runs into the drill and must be skipped; east belt runs away from it.
            SpawnBelt(entMan, map.Grid, 0, 1, Angle.FromDegrees(90));
            SpawnBelt(entMan, map.Grid, 2, 1, Angle.FromDegrees(90));
            SpawnBelt(entMan, map.Grid, 3, 1, Angle.FromDegrees(90));

            var drill = entMan.SpawnEntity("AutominerPassiveFactoryCoal", new EntityCoordinates(map.Grid, 1.5f, 1.5f));
            var comp = entMan.GetComponent<PassiveSpawningMachineComponent>(drill);
            comp.requirePower = false;
            comp.manualActivation = false;
            comp.passedTime = comp.spawnDelay;
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            var coal = new List<EntityUid>();
            var query = entMan.EntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityPrototype?.ID == "Coal")
                    coal.Add(uid);
            }

            Assert.That(coal, Has.Count.EqualTo(1));
            var pos = entMan.GetComponent<TransformComponent>(coal[0]).LocalPosition;
            Assert.That(pos.X, Is.GreaterThan(2.5f), $"Drill output did not land on the east belt (at {pos}).");
        });

        await pair.CleanReturnAsync();
    }
}
