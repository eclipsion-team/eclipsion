using System.Numerics;
using Content.Server.PointCannons;
using Content.Shared._Crescent;
using Content.Shared.Damage;
using Content.Shared.Physics;
using Content.Shared.PointCannons;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class ShipWeaponTargetingTest
{
    [TestCase(0)]
    [TestCase(37)]
    public async Task WallsThenFloorThenNextWall(int rotation)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var transforms = server.System<SharedTransformSystem>();
        EntityUid front = default;
        EntityUid rear = default;

        await server.WaitPost(() =>
        {
            // A second row keeps the grid connected while the firing lane is removed.
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 2; y++)
                maps.SetTile(map.Grid, new Vector2i(x, y), map.Tile.Tile);

            front = em.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            rear = em.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            transforms.SetWorldRotation(map.Grid, Angle.FromDegrees(rotation));
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            void Shoot(bool targetTiles, int damage)
            {
                var start = transforms.ToMapCoordinates(new EntityCoordinates(map.Grid, -2f, 0.5f));
                var bullet = em.SpawnEntity("BulletMachineGunVulcan", start);
                var projectile = em.GetComponent<ProjectileComponent>(bullet);
                projectile.Damage = new DamageSpecifier { DamageDict = { ["Structural"] = damage } };
                var phase = em.EnsureComponent<ProjectilePhasePreventComponent>(bullet);
                phase.TargetTiles = targetTiles;
                phase.relevantBitmasks = (int) (CollisionGroup.Impassable | CollisionGroup.BulletImpassable);
                transforms.SetWorldPosition(bullet,
                    transforms.ToMapCoordinates(new EntityCoordinates(map.Grid, 3f, 0.5f)).Position);
                server.System<ProjectilePhasePreventerSystem>().Update(1f / 60f);
            }

            var original = maps.GetTileRef(map.Grid, Vector2i.Zero).Tile;
            Shoot(true, 30);
            Assert.That(em.GetComponent<DamageableComponent>(front).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(maps.GetTileRef(map.Grid, Vector2i.Zero).Tile, Is.EqualTo(original),
                "The covering wall must be hit before its floor.");
            Assert.That(em.GetComponent<DamageableComponent>(rear).TotalDamage.Float(), Is.Zero);

            em.DeleteEntity(front);
            Shoot(false, 30);
            var rearDamage = em.GetComponent<DamageableComponent>(rear).TotalDamage;
            Assert.That(rearDamage.Float(), Is.GreaterThan(0), "Wall mode should pass over exposed flooring.");
            Assert.That(maps.GetTileRef(map.Grid, Vector2i.Zero).Tile, Is.EqualTo(original));

            Shoot(true, 55);
            Assert.That(maps.GetTileRef(map.Grid, Vector2i.Zero).Tile, Is.EqualTo(original),
                "One standard Vulcan hit should damage plating without breaking it.");
            var tiles = server.ResolveDependency<ITileDefinitionManager>();
            maps.SetTile(map.Grid, Vector2i.Zero, new Tile(tiles["Lattice"].TileId));
            maps.SetTile(map.Grid, Vector2i.Zero, original);
            Shoot(true, 55);
            Assert.That(maps.GetTileRef(map.Grid, Vector2i.Zero).Tile, Is.EqualTo(original),
                "Replacing a damaged floor must reset the old layer's accumulated damage.");
            Shoot(true, 55);
            var lattice = maps.GetTileRef(map.Grid, Vector2i.Zero).Tile;
            Assert.That(lattice, Is.Not.EqualTo(original), "Two standard hits must always break plating.");
            Assert.That(lattice.IsEmpty, Is.False, "The direct hit must leave the next floor layer.");
            Assert.That(em.GetComponent<DamageableComponent>(rear).TotalDamage, Is.EqualTo(rearDamage));

            Shoot(true, 55);
            Assert.That(em.GetComponent<DamageableComponent>(rear).TotalDamage, Is.EqualTo(rearDamage),
                "The next wall must remain untouched until the exposed floor is gone.");

            Assert.That(maps.GetTileRef(map.Grid, Vector2i.Zero).Tile.IsEmpty, Is.True);
            Shoot(true, 30);
            Assert.That(em.GetComponent<DamageableComponent>(rear).TotalDamage, Is.GreaterThan(rearDamage));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ModeIsCapturedAtLaunch()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var em = server.EntMan;
            var console = em.SpawnEntity(null, map.GridCoords);
            var selection = em.AddComponent<ShipWeaponTargetingComponent>(console);
            var gun = em.SpawnEntity(null, map.GridCoords);
            em.AddComponent<GunComponent>(gun);
            var targeting = server.System<ShipWeaponTargetingSystem>();
            targeting.SetConsole(gun, console);
            Assert.That(targeting.GetMode(console), Is.EqualTo(ShipWeaponTargetingMode.Walls));

            EntityUid Shoot()
            {
                var bullet = em.SpawnEntity("BulletMachineGunVulcan", map.GridCoords);
                em.EventBus.RaiseLocalEvent(gun, new AmmoShotEvent { FiredProjectiles = new() { bullet } });
                return bullet;
            }

            var wallRound = Shoot();
            selection.Mode = ShipWeaponTargetingMode.TilesAndWalls;
            var tileRound = Shoot();
            selection.Mode = ShipWeaponTargetingMode.Walls;
            Assert.That(em.TryGetComponent<ProjectilePhasePreventComponent>(wallRound, out var wallPhase) && wallPhase.TargetTiles, Is.False);
            Assert.That(em.GetComponent<ProjectilePhasePreventComponent>(tileRound).TargetTiles, Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
