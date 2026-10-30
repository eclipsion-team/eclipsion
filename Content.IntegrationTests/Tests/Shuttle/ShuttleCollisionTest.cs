#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Projectiles;
using Content.Shared.Tiles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Shuttle;

[TestFixture]
[TestOf(typeof(ShuttleSystem))]
public sealed class ShuttleCollisionTest
{
    private const string EmitterPrototype = "ShieldEmitter";
    private const string ShipRound = "BulletRailgun15mm";

    // offset puts the grid's origin in empty space, like most mapped ships, hollow does the same to the centre
    [Test]
    [TestCase(0f, 0f, 0f, 0, false)]
    [TestCase(400f, -250f, 0f, 0, false)]
    [TestCase(400f, -250f, 0.7f, 0, false)]
    [TestCase(400f, -250f, 0f, 5, false)]
    [TestCase(400f, -250f, 0.7f, 5, false)]
    [TestCase(400f, -250f, 0.7f, 5, true)]
    public async Task ShieldWrapsItsShip(float x, float y, float rotation, int offset, bool hollow)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var maps = entMan.System<SharedMapSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var tileId = Plating(pair);

        EntityUid ship = default;
        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var grid = maps.CreateGridEntity(map.MapId);
            ship = grid.Owner;
            var tiles = Block(offset, offset, 16, 8, tileId);
            if (hollow)
                tiles.RemoveAll(t => t.Item1.X > offset + 1 && t.Item1.X < offset + 14 && t.Item1.Y > offset + 1 && t.Item1.Y < offset + 6);
            maps.SetTiles(ship, grid.Comp, tiles);
            xforms.SetWorldPositionRotation(ship, new Vector2(x, y), new Angle(rotation));
            SpawnPoweredEmitter(entMan, ship, new Vector2(offset + 0.5f, offset + 0.5f));
        });

        await pair.RunSeconds(2.5f);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent(ship, out ShipShieldedComponent? shielded), "no shield came up");
            AssertShieldOn(entMan, xforms, ship, shielded!.Shield);
        });

        await server.WaitPost(() =>
        {
            Launch(entMan, entMan.System<SharedPhysicsSystem>(), ship, new Vector2(6f, -3f));
            entMan.System<SharedPhysicsSystem>().SetAngularVelocity(ship, 0.5f);
        });

        await pair.RunSeconds(1.5f);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent(ship, out ShipShieldedComponent? shielded), "the shield dropped in flight");
            AssertShieldOn(entMan, xforms, ship, shielded!.Shield);
        });

        await pair.CleanReturnAsync();
    }

    // the shield is a loose child of the grid, so a split can carry it off with the debris
    [Test]
    public async Task ShieldStaysWithEmitterWhenHullSplits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var maps = entMan.System<SharedMapSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var tileId = Plating(pair);

        EntityUid ship = default;
        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var grid = maps.CreateGridEntity(map.MapId);
            ship = grid.Owner;
            // wide hull with a tall mast, so the middle of the bounds is on the mast
            var tiles = Block(0, 0, 20, 4, tileId);
            tiles.AddRange(Block(9, 4, 2, 16, tileId));
            maps.SetTiles(ship, grid.Comp, tiles);
            xforms.SetWorldPosition(ship, new Vector2(150f, 80f));
            SpawnPoweredEmitter(entMan, ship, new Vector2(2.5f, 1.5f));
        });

        await pair.RunSeconds(2.5f);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ShipShieldedComponent>(ship), "no shield came up");
        });

        await server.WaitPost(() =>
        {
            var grid = entMan.GetComponent<MapGridComponent>(ship);
            maps.SetTile(ship, grid, new Vector2i(9, 4), Tile.Empty);
            maps.SetTile(ship, grid, new Vector2i(10, 4), Tile.Empty);
        });

        await pair.RunSeconds(2f);

        await server.WaitAssertion(() =>
        {
            Assert.That(CountGrids(entMan, map.MapId), Is.EqualTo(2), "the mast never broke off");
            Assert.That(entMan.TryGetComponent(ship, out ShipShieldedComponent? shielded), "the ship lost its shield");
            AssertShieldOn(entMan, xforms, ship, shielded!.Shield);
            AssertShieldsConsistent(entMan);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase(15f, false)]
    [TestCase(30f, false)]
    [TestCase(60f, false)]
    [TestCase(60f, true)]
    public async Task ShipRamsStation(float speed, bool protectedStation)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var maps = entMan.System<SharedMapSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileId = Plating(pair);

        EntityUid rammer = default;
        EntityUid station = default;
        var stationPos = new Vector2(30f, -9f);

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var rammerGrid = maps.CreateGridEntity(map.MapId);
            var stationGrid = maps.CreateGridEntity(map.MapId);
            rammer = rammerGrid.Owner;
            station = stationGrid.Owner;
            maps.SetTiles(rammer, rammerGrid.Comp, Block(0, 0, 12, 6, tileId));
            maps.SetTiles(station, stationGrid.Comp, Block(0, 0, 24, 24, tileId));
            xforms.SetWorldPosition(station, stationPos);
            // same as a pinned map element
            entMan.System<ShuttleSystem>().Disable(station);
            if (protectedStation)
                entMan.EnsureComponent<ProtectedGridComponent>(station);

            Launch(entMan, physics, rammer, new Vector2(speed, 0f));
        });

        var watch = await RunAndWatch(pair, map.MapId, 90);

        await server.WaitAssertion(() =>
        {
            var stationBody = entMan.GetComponent<PhysicsComponent>(station);
            Assert.That(stationBody.BodyType, Is.EqualTo(BodyType.Static));
            Assert.That((xforms.GetWorldPosition(station) - stationPos).Length(), Is.LessThan(0.01f), "the station moved");
            Assert.That(CountTiles(entMan, maps, rammer) + CountTiles(entMan, maps, station), Is.LessThan(12 * 6 + 24 * 24),
                "nothing broke, so there was no ram");
            AssertNoEnergyGained(watch);
            if (protectedStation)
                Assert.That(CountTiles(entMan, maps, station), Is.EqualTo(24 * 24), "the ram broke a protected station's floor");

            if (entMan.EntityExists(rammer))
            {
                var rammerX = xforms.GetWorldPosition(rammer).X;
                Assert.That(rammerX, Is.LessThan(stationPos.X + 24f), "the ship went straight through the station");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase(10f)]
    [TestCase(20f)]
    [TestCase(40f)]
    public async Task ShipsRamHeadOn(float speed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var maps = entMan.System<SharedMapSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileId = Plating(pair);

        EntityUid left = default;
        EntityUid right = default;

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var a = maps.CreateGridEntity(map.MapId);
            var b = maps.CreateGridEntity(map.MapId);
            left = a.Owner;
            right = b.Owner;
            maps.SetTiles(left, a.Comp, Block(0, 0, 12, 6, tileId));
            maps.SetTiles(right, b.Comp, Block(0, 0, 12, 6, tileId));
            xforms.SetWorldPosition(right, new Vector2(40f, 0f));

            Launch(entMan, physics, left, new Vector2(speed, 0f));
            Launch(entMan, physics, right, new Vector2(-speed, 0f));
        });

        var watch = await RunAndWatch(pair, map.MapId, 90);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.EntityExists(left) && entMan.EntityExists(right), "a ship vanished");
            if (speed >= 15f)
            {
                Assert.That(CountTiles(entMan, maps, left), Is.LessThan(12 * 6), "the left ship took no damage");
                Assert.That(CountTiles(entMan, maps, right), Is.LessThan(12 * 6), "the right ship took no damage");
            }
            AssertNoEnergyGained(watch);
            Assert.That(xforms.GetWorldPosition(left).X, Is.LessThan(xforms.GetWorldPosition(right).X),
                "the ships passed through each other");

            // equal ships, equal speeds: whatever is left should not be flying off one way
            var leftBody = entMan.GetComponent<PhysicsComponent>(left);
            var rightBody = entMan.GetComponent<PhysicsComponent>(right);
            var momentum = leftBody.LinearVelocity * leftBody.Mass + rightBody.LinearVelocity * rightBody.Mass;
            var before = MathF.Max(leftBody.Mass, rightBody.Mass) * speed;
            Assert.That(MathF.Abs(momentum.X), Is.LessThan(before * 0.35f), $"net momentum {momentum} out of {before}");
        });

        await pair.CleanReturnAsync();
    }

    // small fast ship into a big one at rest, straight on and clipping a corner
    [Test]
    [TestCase(-8f, 50f)]
    [TestCase(2f, 50f)]
    [TestCase(2f, 25f)]
    public async Task SmallShipRamsBigShip(float targetY, float speed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var maps = entMan.System<SharedMapSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileId = Plating(pair);

        EntityUid small = default;
        EntityUid big = default;

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var a = maps.CreateGridEntity(map.MapId);
            var b = maps.CreateGridEntity(map.MapId);
            small = a.Owner;
            big = b.Owner;
            maps.SetTiles(small, a.Comp, Block(0, 0, 5, 3, tileId));
            maps.SetTiles(big, b.Comp, Block(0, 0, 20, 20, tileId));
            xforms.SetWorldPosition(big, new Vector2(30f, targetY));
            Launch(entMan, physics, small, new Vector2(speed, 0f));
            Launch(entMan, physics, big, Vector2.Zero);
        });

        var momentumIn = 0f;
        await server.WaitPost(() => momentumIn = entMan.GetComponent<PhysicsComponent>(small).Mass * speed);

        var watch = await RunAndWatch(pair, map.MapId, 90);

        await server.WaitAssertion(() =>
        {
            var bigBody = entMan.GetComponent<PhysicsComponent>(big);
            Assert.That(CountTiles(entMan, maps, small) + CountTiles(entMan, maps, big), Is.LessThan(5 * 3 + 20 * 20),
                "nothing broke, so there was no ram");
            AssertNoEnergyGained(watch);
            Assert.That(bigBody.Mass * bigBody.LinearVelocity.Length(), Is.LessThanOrEqualTo(momentumIn * 1.05f),
                "the big ship got shoved harder than the small one hit it");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShieldedShipsRam()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var maps = entMan.System<SharedMapSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileId = Plating(pair);

        EntityUid rammer = default;
        EntityUid target = default;
        EntityUid rammerEmitter = default;
        EntityUid targetEmitter = default;

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            var a = maps.CreateGridEntity(map.MapId);
            var b = maps.CreateGridEntity(map.MapId);
            rammer = a.Owner;
            target = b.Owner;
            maps.SetTiles(rammer, a.Comp, Block(0, 0, 12, 6, tileId));
            maps.SetTiles(target, b.Comp, Block(0, 0, 20, 20, tileId));
            xforms.SetWorldPosition(rammer, new Vector2(200f, 100f));
            xforms.SetWorldPosition(target, new Vector2(230f, 93f));
            rammerEmitter = SpawnPoweredEmitter(entMan, rammer, new Vector2(1.5f, 1.5f));
            targetEmitter = SpawnPoweredEmitter(entMan, target, new Vector2(18.5f, 18.5f));
        });

        await pair.RunSeconds(2.5f);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ShipShieldedComponent>(rammer), "the rammer has no shield");
            Assert.That(entMan.HasComponent<ShipShieldedComponent>(target), "the target has no shield");
            AssertShieldsConsistent(entMan);
        });

        await server.WaitPost(() => Launch(entMan, physics, rammer, new Vector2(30f, 0f)));

        for (var i = 0; i < 6; i++)
        {
            await pair.RunTicksSync(15);
            await server.WaitAssertion(() => AssertShieldsConsistent(entMan));
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(CountTiles(entMan, maps, rammer) + CountTiles(entMan, maps, target), Is.LessThan(12 * 6 + 20 * 20),
                "nothing broke, so there was no ram");

            foreach (var (grid, emitter) in new[] { (rammer, rammerEmitter), (target, targetEmitter) })
            {
                if (!entMan.EntityExists(emitter))
                    continue;

                var emitterGrid = entMan.GetComponent<TransformComponent>(emitter).GridUid;
                Assert.That(emitterGrid, Is.Not.Null);
                Assert.That(entMan.TryGetComponent(emitterGrid!.Value, out ShipShieldedComponent? shielded),
                    $"{entMan.ToPrettyString(grid)} lost its shield in the ram");
                AssertShieldOn(entMan, xforms, emitterGrid.Value, shielded!.Shield);
            }

        });

        // rounds fired from inside both bubbles: the target's shield eats them, the rammer's own lets them out
        var targetDamage = 0f;
        var rammerDamage = 0f;
        await server.WaitPost(() =>
        {
            Stop(entMan, physics, rammer);
            targetDamage = entMan.GetComponent<ShipShieldEmitterComponent>(targetEmitter).Damage;
            rammerDamage = entMan.GetComponent<ShipShieldEmitterComponent>(rammerEmitter).Damage;

            var origin = xforms.GetWorldPosition(rammerEmitter);
            var towards = Vector2.Normalize(xforms.GetWorldPosition(targetEmitter) - origin);
            for (var b = 0; b < 4; b++)
            {
                var round = entMan.SpawnEntity(ShipRound, new MapCoordinates(origin + towards * (1f + b * 0.5f), map.MapId));
                var projectile = entMan.GetComponent<ProjectileComponent>(round);
                projectile.Weapon = rammerEmitter;
                projectile.Shooter = rammerEmitter;
                physics.SetLinearVelocity(round, towards * 60f);
            }
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<ShipShieldEmitterComponent>(targetEmitter).Damage, Is.GreaterThan(targetDamage),
                "the target's shield let the rounds through");
            Assert.That(entMan.GetComponent<ShipShieldEmitterComponent>(rammerEmitter).Damage, Is.EqualTo(rammerDamage),
                "the rammer's own shield ate its rounds");
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertShieldOn(IEntityManager entMan, SharedTransformSystem xforms, EntityUid grid, EntityUid shield)
    {
        Assert.That(entMan.EntityExists(shield), "the shield entity is gone");
        Assert.That(entMan.GetComponent<TransformComponent>(shield).ParentUid, Is.EqualTo(grid),
            $"the shield of {entMan.ToPrettyString(grid)} is parented to {entMan.ToPrettyString(entMan.GetComponent<TransformComponent>(shield).ParentUid)}");

        var gridComp = entMan.GetComponent<MapGridComponent>(grid);
        var gridCentre = Vector2.Transform(gridComp.LocalAABB.Center, xforms.GetWorldMatrix(grid));
        var shieldCentre = xforms.GetWorldPosition(shield);
        var reach = MathF.Max(gridComp.LocalAABB.Width, gridComp.LocalAABB.Height) / 2f;
        Assert.That((shieldCentre - gridCentre).Length(), Is.LessThan(reach),
            $"shield centre {shieldCentre} is nowhere near {entMan.ToPrettyString(grid)} at {gridCentre}");
    }

    // every shield points at a live ship that points back at it, and no ship has two
    private static void AssertShieldsConsistent(IEntityManager entMan)
    {
        var owners = new Dictionary<EntityUid, EntityUid>();
        var query = entMan.AllEntityQueryEnumerator<ShipShieldComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var shield, out var xform))
        {
            if (entMan.IsQueuedForDeletion(uid))
                continue;

            Assert.That(owners.TryAdd(shield.Shielded, uid), Is.True,
                $"{entMan.ToPrettyString(shield.Shielded)} has two shields");
            Assert.That(entMan.TryGetComponent(shield.Shielded, out ShipShieldedComponent? shielded) && shielded.Shield == uid,
                $"orphan shield {entMan.ToPrettyString(uid)}");
            Assert.That(xform.ParentUid, Is.EqualTo(shield.Shielded),
                $"shield of {entMan.ToPrettyString(shield.Shielded)} is riding {entMan.ToPrettyString(xform.ParentUid)}");
        }
    }

    private static void Launch(IEntityManager entMan, SharedPhysicsSystem physics, EntityUid grid, Vector2 velocity)
    {
        var body = entMan.GetComponent<PhysicsComponent>(grid);
        physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        physics.SetLinearDamping(grid, body, 0f);
        physics.SetAngularDamping(grid, body, 0f);
        physics.SetLinearVelocity(grid, velocity, body: body);
        physics.WakeBody(grid, body: body);
    }

    private static void Stop(IEntityManager entMan, SharedPhysicsSystem physics, EntityUid grid)
    {
        if (!entMan.TryGetComponent(grid, out PhysicsComponent? body))
            return;

        physics.SetLinearVelocity(grid, Vector2.Zero, body: body);
        physics.SetAngularVelocity(grid, 0f, body: body);
    }

    private static EntityUid SpawnPoweredEmitter(IEntityManager entMan, EntityUid grid, Vector2 localPos)
    {
        var emitter = entMan.SpawnEntity(EmitterPrototype, new EntityCoordinates(grid, localPos));
        entMan.GetComponent<ApcPowerReceiverComponent>(emitter).NeedsPower = false;
        return emitter;
    }

    private static int Plating(TestPair pair)
    {
        return pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;
    }

    private static List<(Vector2i, Tile)> Block(int x0, int y0, int width, int height, int tileId)
    {
        var tiles = new List<(Vector2i, Tile)>(width * height);
        for (var x = x0; x < x0 + width; x++)
        {
            for (var y = y0; y < y0 + height; y++)
            {
                tiles.Add((new Vector2i(x, y), new Tile(tileId)));
            }
        }

        return tiles;
    }

    private static int CountTiles(IEntityManager entMan, SharedMapSystem maps, EntityUid grid)
    {
        if (!entMan.TryGetComponent(grid, out MapGridComponent? comp))
            return 0;

        var count = 0;
        var enumerator = maps.GetAllTiles(grid, comp);
        while (enumerator.MoveNext(out _))
        {
            count++;
        }

        return count;
    }

    private readonly record struct Watch(float StartSpeed, float MaxSpeed, float StartEnergy, float MaxEnergy);

    // speed and energy of every grid on the map, debris included
    private static async Task<Watch> RunAndWatch(TestPair pair, MapId mapId, int ticks)
    {
        var entMan = pair.Server.EntMan;
        var startSpeed = 0f;
        var startEnergy = 0f;
        await pair.Server.WaitPost(() =>
        {
            startSpeed = MaxGridSpeed(entMan, mapId);
            startEnergy = KineticEnergy(entMan, mapId);
        });

        var maxSpeed = startSpeed;
        var maxEnergy = startEnergy;
        for (var i = 0; i < ticks; i++)
        {
            await pair.RunTicksSync(1);
            await pair.Server.WaitPost(() =>
            {
                maxSpeed = MathF.Max(maxSpeed, MaxGridSpeed(entMan, mapId));
                maxEnergy = MathF.Max(maxEnergy, KineticEnergy(entMan, mapId));
            });
        }

        return new Watch(startSpeed, maxSpeed, startEnergy, maxEnergy);
    }

    private static void AssertNoEnergyGained(Watch watch)
    {
        Assert.That(watch.MaxSpeed, Is.LessThanOrEqualTo(watch.StartSpeed * 1.02f + 0.1f), "something came out of the ram faster than anything went in");
        Assert.That(watch.MaxEnergy, Is.LessThanOrEqualTo(watch.StartEnergy * 1.02f), "the ram made kinetic energy out of nothing");
    }

    private static float MaxGridSpeed(IEntityManager entMan, MapId mapId)
    {
        var max = 0f;
        var query = entMan.AllEntityQueryEnumerator<MapGridComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var body, out var xform))
        {
            if (xform.MapID == mapId)
                max = MathF.Max(max, body.LinearVelocity.Length());
        }

        return max;
    }

    private static float KineticEnergy(IEntityManager entMan, MapId mapId)
    {
        var energy = 0f;
        var query = entMan.AllEntityQueryEnumerator<MapGridComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var body, out var xform))
        {
            if (xform.MapID != mapId || body.BodyType != BodyType.Dynamic)
                continue;

            energy += 0.5f * body.Mass * body.LinearVelocity.LengthSquared()
                      + 0.5f * body.Inertia * body.AngularVelocity * body.AngularVelocity;
        }

        return energy;
    }

    private static int CountGrids(IEntityManager entMan, MapId mapId)
    {
        var count = 0;
        var query = entMan.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.MapID == mapId)
                count++;
        }

        return count;
    }
}
