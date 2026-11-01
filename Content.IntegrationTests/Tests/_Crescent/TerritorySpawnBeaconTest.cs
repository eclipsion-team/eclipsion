using Content.Server._Crescent.Territory;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Maps;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class TerritorySpawnBeaconTest
{
    [Test]
    public async Task SpawnerAndRackFollowTheHolder()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var beacons = server.System<TerritorySpawnBeaconSystem>();

        EntityUid grid = default;
        EntityUid beacon = default;
        EntityUid rack = default;
        PersistentPoiGridComponent territory = default!;

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            grid = gridEnt.Owner;
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 8; x++)
            {
                mapSystem.SetTile(gridEnt, new Vector2i(x, 0), new Tile(plating.TileId));
            }

            territory = entMan.EnsureComponent<PersistentPoiGridComponent>(grid);
            territory.Owner = "DSM";
            territory.Level = 3;

            beacon = entMan.SpawnEntity("PersistentPoiCryoSpawner", new EntityCoordinates(grid, 6.5f, 0.5f));
            entMan.System<PowerReceiverSystem>().SetNeedsPower(beacon, false);
            rack = entMan.SpawnEntity("PersistentPoiGearRackDSM", new EntityCoordinates(grid, 3.5f, 0.5f));
        });

        // power nets only settle every half second
        await server.WaitRunTicks(90);
        await server.WaitAssertion(() =>
        {
            var bx = entMan.GetComponent<TransformComponent>(beacon);
            var pr = entMan.GetComponent<ApcPowerReceiverComponent>(beacon);
            Assert.That(beacons.IsActive(beacon, out _), Is.True,
                $"anchored {bx.Anchored} grid {bx.GridUid == grid} owner {territory.Owner} level {territory.Level} powered {pr.Powered} needs {pr.NeedsPower}");

            var member = SpawnMember(entMan, grid, "DSM");
            Assert.That(beacons.TryDeploy(member, beacon), Is.True);
            Assert.That(entMan.GetComponent<TransformComponent>(member).Coordinates.Position,
                Is.EqualTo(entMan.GetComponent<TransformComponent>(beacon).Coordinates.Position));

            var outsider = SpawnMember(entMan, grid, "NCWL");
            Assert.That(beacons.TryDeploy(outsider, beacon), Is.False, "an outsider woke up in the holder's spawner");

            Assert.That(OpensRack(entMan, rack, member), Is.True);
            Assert.That(OpensRack(entMan, rack, outsider), Is.False);

            territory.Level = 1;
            Assert.That(beacons.IsActive(beacon, out _), Is.False, "the spawner works below level 2");

            territory.Level = 3;
            territory.Owner = "NCWL";
            Assert.That(beacons.TryDeploy(member, beacon), Is.False, "the spawner kept serving a former holder");
            Assert.That(OpensRack(entMan, rack, member), Is.False, "the rack kept serving a former holder");

            territory.Owner = null;
            Assert.That(beacons.IsActive(beacon, out _), Is.False, "the spawner works on unclaimed ground");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnMember(IEntityManager entMan, EntityUid grid, string faction)
    {
        var mob = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 0.5f, 0.5f));
        entMan.EnsureComponent<HullrotFactionComponent>(mob).Faction = faction;
        return mob;
    }

    private static bool OpensRack(IEntityManager entMan, EntityUid rack, EntityUid user)
    {
        var ev = new ActivatableUIOpenAttemptEvent(user);
        entMan.EventBus.RaiseLocalEvent(rack, ev);
        return !ev.Cancelled;
    }
}
