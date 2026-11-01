using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Territory;
using Content.Server.Maps;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Economy;
using Content.Shared._Crescent.HeatSeeking;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._Crescent.Territory;
using Content.Shared.Damage.Systems;
using Content.Shared.Maps;
using Content.Shared.PointCannons;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class TerritoryDroneDefenceTest
{
    [Test]
    public async Task PointDefenceTurnsOnLockedMissile()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var campaign = $"DroneDefenceTest{Guid.NewGuid():N}";
        var regionId = $"integration-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid console = default;
        EntityUid gun = default;
        EntityUid missile = default;

        await server.WaitPost(() => server.System<TerritoryProfileSystem>().SetProfile(campaign, null));
        await server.WaitPost(() => grid = SpawnTerritory(server, regionId, out _));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var power = entMan.System<PowerReceiverSystem>();
            console = entMan.SpawnEntity("ComputerTargetingPersistentPoi", new EntityCoordinates(grid, -3.5f, -3.5f));
            power.SetNeedsPower(console, false);

            var hardpoint = entMan.SpawnEntity("HardpointPersistentPoiLaserEmplacement", new EntityCoordinates(grid, 3.5f, 3.5f));
            power.SetNeedsPower(hardpoint, false);

            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "DSM"), Is.True);
            server.System<PersistentPoiProgressionSystem>().SetLevel(regionId, 1);

            // holding fire on ships must not hold it on missiles
            entMan.GetComponent<PersistentPoiGridComponent>(grid).AutoDefense = false;
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            gun = Find<TerritoryMissileInterceptorComponent>(entMan, grid).Single();
            entMan.System<PowerReceiverSystem>().SetNeedsPower(gun, false);

            // nobody fired it, so nobody it could be friends with
            var mapUid = entMan.GetComponent<TransformComponent>(grid).MapUid!.Value;
            missile = entMan.SpawnEntity("BulletRocketSmart", new EntityCoordinates(mapUid, 300f, 0f));
            var seeker = entMan.GetComponent<HeatSeekingComponent>(missile);
            seeker.TargetEntity = console;
            seeker.StartDelay = 0f;
            entMan.System<SharedTransformSystem>().SetWorldRotation(missile, Angle.FromWorldVec(new Vector2(-1f, 0f)));
            entMan.System<SharedPhysicsSystem>().SetLinearVelocity(missile, new Vector2(-60f, 0f));
        });

        await server.WaitRunTicks(15);
        await server.WaitAssertion(() =>
        {
            var defense = entMan.GetComponent<TerritoryAutoDefenseComponent>(console);
            var interceptor = entMan.GetComponent<TerritoryMissileInterceptorComponent>(gun);
            var linked = entMan.GetComponent<PointCannonComponent>(gun).LinkedConsoleIds.Contains(console);
            var missileAt = entMan.Deleted(missile)
                ? "deleted"
                : entMan.System<SharedTransformSystem>().GetWorldPosition(missile).ToString();
            Assert.That(entMan.Deleted(missile) || defense.Missile == missile, Is.True,
                $"The battery never picked up a missile locked on it (gun linked {linked}, missile at {missileAt}, " +
                $"powered {entMan.System<PowerReceiverSystem>().IsPowered(console)}).");
            Assert.That(interceptor.EngagedUntil, Is.GreaterThan(TimeSpan.Zero),
                "The point-defence laser never turned on the missile.");

            entMan.DeleteEntity(entMan.GetComponent<TransformComponent>(grid).MapUid);
            server.System<TerritoryProfileSystem>().ArchiveProfile(campaign);
            server.System<TerritoryProfileSystem>().SetProfile(TerritoryProfileSystem.DefaultProfile, null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DroneBaySellsOnlyAtLevelThree()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var campaign = $"DroneDefenceTest{Guid.NewGuid():N}";
        var regionId = $"integration-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid flag = default;
        EntityUid console = default;
        EntityUid holder = default;

        await server.WaitPost(() => server.System<TerritoryProfileSystem>().SetProfile(campaign, null));
        await server.WaitPost(() => grid = SpawnTerritory(server, regionId, out flag));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "DSM"), Is.True);
            server.System<PersistentPoiProgressionSystem>().SetLevel(regionId, 2);

            console = entMan.SpawnEntity("ComputerPersistentPoiDroneBay", new EntityCoordinates(grid, 4.5f, 4.5f));
            entMan.System<PowerReceiverSystem>().SetNeedsPower(console, false);
            var bay = entMan.GetComponent<TerritoryDroneBayConsoleComponent>(console);
            foreach (var hull in bay.Hulls.Keys.ToList())
            {
                bay.Hulls[hull] = 0;
            }

            holder = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 3.5f, 4.5f));
            entMan.EnsureComponent<HullrotFactionComponent>(holder).Faction = "DSM";
            entMan.System<SharedGodmodeSystem>().EnableGodmode(holder);
        });

        // power nets settle every half second
        await server.WaitRunTicks(60);
        await server.WaitAssertion(() =>
        {
            var bays = server.System<TerritoryDroneBaySystem>();
            var ent = (console, entMan.GetComponent<TerritoryDroneBayConsoleComponent>(console));

            Assert.That(bays.TryLaunch(ent, holder, "PoiHornetDrone", PaymentSource.Personal, out _, out _, out var error),
                Is.False, "A level 2 territory bought a drone.");
            Assert.That(error, Is.EqualTo("territory-drone-bay-full"));

            Assert.That(bays.TryLaunch(ent, holder, "PoiShieldedNothing", PaymentSource.Personal, out _, out _, out error),
                Is.False);
            Assert.That(error, Is.EqualTo("territory-drone-bay-unknown-hull"));

            server.System<PersistentPoiProgressionSystem>().SetLevel(regionId, 3);
            var hulls = ent.Item2.Hulls.Keys.Select(h => h.Id).ToList();
            for (var i = 0; i < 4; i++)
            {
                Assert.That(bays.TryLaunch(ent, holder, hulls[i % hulls.Count], PaymentSource.Personal,
                    out var drone, out _, out error), Is.True, $"Launch {i + 1} failed: {error}");

                var droneGrid = entMan.GetComponent<TransformComponent>(drone!.Value).GridUid!.Value;
                Assert.That(entMan.GetComponent<IFFComponent>(droneGrid).Faction, Is.EqualTo("DSM"),
                    "A bought drone doesn't fly for the holder.");
            }

            Assert.That(bays.TryLaunch(ent, holder, hulls[0], PaymentSource.Personal, out _, out _, out error),
                Is.False, "The bay went past the level's drones.");
            Assert.That(error, Is.EqualTo("territory-drone-bay-full"));

            var pickets = entMan.GetComponent<TerritoryDronePicketsComponent>(flag);
            Assert.That(pickets.Pickets, Has.Count.EqualTo(4), "The bought drones didn't join the picket.");
        });

        await server.WaitAssertion(() =>
        {
            foreach (var drone in entMan.GetComponent<TerritoryDronePicketsComponent>(flag).Pickets.ToList())
            {
                if (entMan.GetComponent<TransformComponent>(drone).GridUid is { } droneGrid)
                    entMan.DeleteEntity(droneGrid);
            }

            entMan.DeleteEntity(entMan.GetComponent<TransformComponent>(grid).MapUid);
            server.System<TerritoryProfileSystem>().ArchiveProfile(campaign);
            server.System<TerritoryProfileSystem>().SetProfile(TerritoryProfileSystem.DefaultProfile, null);
        });

        await pair.CleanReturnAsync();
    }

    // the user's rule for bought drones: no shields, nothing heavier than 150cc
    [Test]
    public async Task DroneBayHullsCarryNoShieldsAndLightGuns()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        var allowedGuns = new HashSet<string> { "WeaponTurretRetribution" };

        await server.WaitAssertion(() =>
        {
            var bay = server.ProtoMan.Index("ComputerPersistentPoiDroneBay");
            Assert.That(bay.TryGetComponent<TerritoryDroneBayConsoleComponent>(out var hulls, entMan.ComponentFactory),
                Is.True);

            var loader = entMan.System<MapLoaderSystem>();
            var offset = 0f;
            foreach (var hull in hulls!.Hulls.Keys)
            {
                var gameMap = server.ProtoMan.Index<GameMapPrototype>(hull);
                Assert.That(loader.TryLoadGrid(map.MapId, gameMap.MapPath, out var grid, offset: new Vector2(offset, 500f)),
                    Is.True, $"{hull} didn't load.");
                offset += 200f;

                Assert.That(Find<ShipShieldEmitterComponent>(entMan, grid!.Value.Owner), Is.Empty,
                    $"{hull} carries a shield emitter.");

                foreach (var gun in Find<PointCannonComponent>(entMan, grid.Value.Owner))
                {
                    var proto = entMan.GetComponent<MetaDataComponent>(gun).EntityPrototype?.ID;
                    Assert.That(allowedGuns, Does.Contain(proto), $"{hull} carries {proto}, heavier than 150cc.");
                }

                entMan.DeleteEntity(grid.Value.Owner);
            }
        });

        await pair.CleanReturnAsync();
    }

    private static List<EntityUid> Find<T>(IEntityManager entMan, EntityUid grid) where T : IComponent
    {
        var found = new List<EntityUid>();
        var query = entMan.AllEntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == grid)
                found.Add(uid);
        }

        return found;
    }

    private static EntityUid SpawnTerritory(RobustIntegrationTest.ServerIntegrationInstance server, string regionId,
        out EntityUid flag)
    {
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];

        mapSystem.CreateMap(out var mapId);
        var grid = mapSystem.CreateGridEntity(mapId);
        for (var x = -6; x <= 6; x++)
        {
            for (var y = -6; y <= 6; y++)
            {
                mapSystem.SetTile(grid, new Vector2i(x, y), new Tile(plating.TileId));
            }
        }

        flag = entMan.CreateEntityUninitialized("PersistentCaptureRegionFlag", new EntityCoordinates(grid, 0.5f, 0.5f));
        entMan.GetComponent<PersistentCaptureRegionComponent>(flag).RegionId = regionId;
        entMan.InitializeAndStartEntity(flag);
        return grid.Owner;
    }
}
