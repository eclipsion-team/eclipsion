using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent.ShipShields;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Shuttle;

/// <summary>
///     Players' clients froze or died mid-fight. Once the client's contact graph is corrupted every physics tick
///     throws and the client stops simulating, so any error logged on either side fails these tests (the pool
///     fails on logged errors). Each test also checks that the thing it exercises really happened - a ram that
///     broke tiles, a shield that really came up - so a silent no-op cannot pass as a result.
/// </summary>
[TestFixture]
[TestOf(typeof(ShuttleSystem))]
public sealed class ShuttleRamClientStabilityTest
{
    // Emitters own their shields: a shield without a live emitter source is removed on the next update, so
    // shields here always come from a real, powered emitter going through ShipShieldsSystem's normal path.
    private const string EmitterPrototype = "ShieldEmitter";

    [Test]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task RamDoesNotBreakClientPhysics(bool shieldRammer, bool shieldTarget)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.EntMan;
        var mapSystem = entMan.System<SharedMapSystem>();
        var xformSystem = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileId = server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;

        EntityUid rammer = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);

            var rammerGrid = mapSystem.CreateGridEntity(map.MapId);
            var targetGrid = mapSystem.CreateGridEntity(map.MapId);
            rammer = rammerGrid.Owner;
            target = targetGrid.Owner;

            mapSystem.SetTiles(rammer, rammerGrid.Comp, Block(12, 6, tileId));
            mapSystem.SetTiles(target, targetGrid.Comp, Block(20, 20, tileId));
            xformSystem.SetLocalPosition(target, new Vector2(30f, -7f));

            if (shieldRammer)
                SpawnPoweredEmitter(entMan, rammer, new Vector2(4f, 3f));
            if (shieldTarget)
                SpawnPoweredEmitter(entMan, target, new Vector2(4f, 4f));
        });

        // Power recalculates next tick and emitters update once a second.
        await pair.RunSeconds(2.5f);

        var targetTilesBefore = 0;
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ShipShieldedComponent>(rammer), Is.EqualTo(shieldRammer),
                "The rammer's shield state is not what this case asked for, so the case would test the wrong thing.");
            Assert.That(entMan.HasComponent<ShipShieldedComponent>(target), Is.EqualTo(shieldTarget),
                "The target's shield state is not what this case asked for, so the case would test the wrong thing.");
            targetTilesBefore = CountTiles(entMan, mapSystem, target);
        });

        // Keep driving it in: a grind that keeps making and breaking contacts is what hurt players, not one bump.
        for (var i = 0; i < 40; i++)
        {
            await server.WaitPost(() =>
            {
                if (!entMan.TryGetComponent(rammer, out PhysicsComponent? body))
                    return;

                physics.SetBodyType(rammer, BodyType.Dynamic, body: body);
                physics.SetLinearDamping(rammer, body, 0f);
                physics.SetLinearVelocity(rammer, new Vector2(40f, 0f), body: body);
                physics.WakeBody(rammer, body: body);

                // Rounds crossing the target's shield ring mid-ram, as in a real fight. A fast body's broadphase box
                // is stretched by its velocity, so it spans several ring segments at once.
                for (var b = 0; b < 8; b++)
                {
                    var bullet = entMan.SpawnEntity("BulletRifle", new MapCoordinates(new Vector2(18f, -6f + b * 2.5f), map.MapId));
                    physics.SetLinearVelocity(bullet, new Vector2(150f, 0f));
                }
            });

            await pair.RunTicksSync(5);
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.EntityExists(target), "Target grid vanished; cannot tell whether the ram happened.");
            Assert.That(CountTiles(entMan, mapSystem, target), Is.LessThan(targetTilesBefore),
                "No tiles broke, so the grids never actually rammed and this test proved nothing.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     A shield is deleted and rebuilt whenever its emitter loses power or overloads, which happens mid-fight.
    ///     Rebuilding creates the outline fixture from scratch and touches every one of its segment proxies at once,
    ///     so anything standing on the ring - here a mob on each sampled vertex, overlapping the two segments that
    ///     meet there - is where a multi-proxy fixture would queue the same contact twice and corrupt the contact
    ///     graph, on the server and on every client (shields are sent to everyone).
    /// </summary>
    [Test]
    public async Task ShieldRebuiltOverCrewDoesNotBreakClientPhysics()
    {
        const int cycles = 5;

        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.EntMan;
        var mapSystem = entMan.System<SharedMapSystem>();
        var xformSystem = entMan.System<SharedTransformSystem>();
        var fixtures = entMan.System<FixtureSystem>();
        var tileId = server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;

        EntityUid ship = default;
        EntityUid emitter = default;

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var grid = mapSystem.CreateGridEntity(map.MapId);
            ship = grid.Owner;
            mapSystem.SetTiles(ship, grid.Comp, Block(20, 12, tileId));
            emitter = SpawnPoweredEmitter(entMan, ship, new Vector2(4f, 4f));
        });

        await pair.RunSeconds(2.5f);

        var shieldsSeen = new HashSet<EntityUid>();
        var mobs = 0;

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent(ship, out ShipShieldedComponent? shielded),
                "The powered emitter never raised a shield, so there is nothing to rebuild.");

            var shield = shielded!.Shield;
            shieldsSeen.Add(shield);

            var fixture = fixtures.GetFixtureOrNull(shield, "shield");
            Assert.That(fixture?.Shape, Is.InstanceOf<ChainShape>(),
                "The shield outline is no longer a chain; rewrite this test for the new shape.");

            var chain = (ChainShape) fixture!.Shape;
            var matrix = xformSystem.GetWorldMatrix(shield);
            var step = Math.Max(1, chain.Count / 12);

            for (var i = 0; i < chain.Count; i += step)
            {
                var world = Vector2.Transform(chain.Vertices[i], matrix);
                entMan.SpawnEntity("MobHuman", new MapCoordinates(world, map.MapId));
                mobs++;
            }
        });

        await pair.RunTicksSync(5);

        var drops = 0;

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            // Overload it the way a fight does. Twice the limit so the heal that runs first in the same emitter update
            // cannot bring it back under; the emitter then drops its shield in that update.
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<ShipShieldEmitterComponent>(emitter);
                comp.Damage = comp.DamageLimit * 2f;
            });
            await pair.RunSeconds(2f);

            await server.WaitAssertion(() =>
            {
                if (!entMan.HasComponent<ShipShieldedComponent>(ship))
                    drops++;
            });

            // Repaired and punishment served: the emitter builds a brand new shield entity over the crew.
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<ShipShieldEmitterComponent>(emitter);
                comp.Damage = 0f;
                comp.OverloadAccumulator = 0f;
            });
            await pair.RunSeconds(2f);

            await server.WaitAssertion(() =>
            {
                if (entMan.TryGetComponent(ship, out ShipShieldedComponent? shielded))
                    shieldsSeen.Add(shielded.Shield);
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(mobs, Is.GreaterThan(0), "No mobs were placed on the ring, so nothing was tested.");
            Assert.That(drops, Is.EqualTo(cycles),
                "The shield did not drop every cycle, so the rebuild path was not exercised.");
            Assert.That(shieldsSeen, Has.Count.EqualTo(cycles + 1),
                "The shield was not really rebuilt every cycle, so the rebuild path was not exercised.");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnPoweredEmitter(IEntityManager entMan, EntityUid grid, Vector2 localPos)
    {
        var emitter = entMan.SpawnEntity(EmitterPrototype, new EntityCoordinates(grid, localPos));
        // No APC in a test map: this makes the receiver count as powered, the same state a working grid gives it.
        entMan.GetComponent<ApcPowerReceiverComponent>(emitter).NeedsPower = false;
        return emitter;
    }

    private static List<(Vector2i, Tile)> Block(int width, int height, int tileId)
    {
        var tiles = new List<(Vector2i, Tile)>(width * height);
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                tiles.Add((new Vector2i(x, y), new Tile(tileId)));
            }
        }

        return tiles;
    }

    private static int CountTiles(IEntityManager entMan, SharedMapSystem mapSystem, EntityUid grid)
    {
        if (!entMan.TryGetComponent(grid, out MapGridComponent? comp))
            return 0;

        var count = 0;
        var enumerator = mapSystem.GetAllTilesEnumerator(grid, comp);
        while (enumerator.MoveNext(out _))
        {
            count++;
        }

        return count;
    }
}
