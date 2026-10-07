using System.Numerics;
using Content.Server.PointCannons;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared.Damage;
using Content.Shared.PointCannons;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Fires real hardpoint-mounted guns from a targeting console through live physics ticks, rather than raising
/// AmmoShotEvent and calling the sweep by hand, so the whole console-to-tile path is covered.
/// </summary>
[TestFixture]
public sealed class ShipWeaponTargetingEndToEndTest
{
    private static readonly object[] Guns =
    {
        new object[] { "WeaponTurretPDT", "AAAHardpointSmallBallistic" },
        new object[] { "WeaponTurretLaser", "AAAHardpointSmallEnergy" },
        new object[] { "WeaponTurretCurse", "AAAHardpointMediumEnergy" },
        new object[] { "WeaponTurretRetribution", "AAAHardpointSmallEnergy" },
    };

    [TestCaseSource(nameof(Guns))]
    public async Task TilesModeBreaksExposedFloor(string gunProto, string hardpointProto)
    {
        await using var pair = await PoolManager.GetServerClient();
        var setup = await Setup(pair, gunProto, hardpointProto, ShipWeaponTargetingMode.TilesAndWalls, wall: false);
        var server = pair.Server;
        var maps = server.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(maps.GetTileRef(setup.Shooter, Vector2i.Zero).Tile, Is.EqualTo(setup.ShooterTile),
                $"{gunProto} must never break the floor of the ship that fired it.");
            Assert.That(server.EntMan.GetComponent<DamageableComponent>(setup.Hardpoint).TotalDamage.Float(), Is.Zero,
                $"{gunProto} must not hit its own hardpoint.");
            Assert.That(maps.GetTileRef(setup.Target, Vector2i.Zero).Tile.TypeId, Is.Not.EqualTo(setup.Floor.TypeId),
                $"{gunProto} fired in tiles mode must break the first exposed floor tile it reaches.");
        });

        await pair.CleanReturnAsync();
    }

    [TestCaseSource(nameof(Guns))]
    public async Task WallModeHitsWall(string gunProto, string hardpointProto)
    {
        await using var pair = await PoolManager.GetServerClient();
        var setup = await Setup(pair, gunProto, hardpointProto, ShipWeaponTargetingMode.Walls, wall: true);
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.GetComponent<DamageableComponent>(setup.Hardpoint).TotalDamage.Float(), Is.Zero,
                $"{gunProto} must not hit its own hardpoint.");
            Assert.That(server.EntMan.Deleted(setup.Wall) ||
                    server.EntMan.GetComponent<DamageableComponent>(setup.Wall!.Value).TotalDamage.Float() > 0, Is.True,
                $"{gunProto} fired in wall mode must hit the wall in its path.");
        });

        await pair.CleanReturnAsync();
    }

    private sealed class SetupData
    {
        public Entity<MapGridComponent> Shooter;
        public Entity<MapGridComponent> Target;
        public EntityUid Hardpoint;
        public EntityUid? Wall;
        public Tile Floor;
        public Tile ShooterTile;
    }

    private static async Task<SetupData> Setup(Pair.TestPair pair, string gunProto, string hardpointProto,
        ShipWeaponTargetingMode mode, bool wall)
    {
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var transforms = server.System<SharedTransformSystem>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var data = new SetupData { Shooter = map.Grid, ShooterTile = map.Tile.Tile };

        EntityUid gun = default;
        EntityUid console = default;

        await server.WaitPost(() =>
        {
            data.Floor = new Tile(tiles["FloorSteel"].TileId);
            data.Target = maps.CreateGridEntity(map.MapId);
            transforms.SetWorldPosition(data.Target, new Vector2(20f, 0f));
            for (var x = 0; x < 4; x++)
            for (var y = -1; y < 2; y++)
                maps.SetTile(data.Target, new Vector2i(x, y), data.Floor);

            if (wall)
                data.Wall = em.SpawnEntity("WallSolid", new EntityCoordinates(data.Target, 0.5f, 0.5f));

            data.Hardpoint = em.SpawnEntity(hardpointProto, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            gun = em.SpawnEntity(gunProto, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            // Off the firing line, so a round that clips the console is not mistaken for one that missed.
            console = em.SpawnEntity("ComputerTargeting", new MapCoordinates(new Vector2(0f, -10f), map.MapId));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<HardpointAnchorableOnlyComponent>(gun).anchoredTo, Is.EqualTo(data.Hardpoint),
                "Test setup failed: the gun never mounted on the hardpoint.");

            em.EnsureComponent<ShipWeaponTargetingComponent>(console).Mode = mode;
            var cannons = server.System<PointCannonSystem>();
            var target = transforms.ToMapCoordinates(new EntityCoordinates(data.Target, 1.5f, 0.5f)).Position;
            Assert.That(cannons.TryFireCannon(gun, target, console: console), Is.True,
                "Test setup failed: the console could not fire the gun.");
        });
        await pair.RunTicksSync(30);

        return data;
    }
}
