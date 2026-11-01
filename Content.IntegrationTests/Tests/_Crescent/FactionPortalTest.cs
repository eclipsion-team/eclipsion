using System.Numerics;
using Content.Server._Crescent.FactionPortal;
using Content.Server._Crescent.NpcSquad;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.FactionPortal;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared._Crescent.Territory;
using Content.Shared.Maps;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class FactionPortalTest
{
    [Test]
    public async Task PortalsOnlyJoinTheirOwnFaction()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var portals = server.System<FactionPortalSystem>();
        var pulling = server.System<PullingSystem>();
        var squads = server.System<NpcSquadSystem>();

        EntityUid station = default;
        EntityUid dsm = default;
        EntityUid ncwl = default;
        EntityUid poi = default;
        PersistentPoiGridComponent territory = default!;

        await server.WaitAssertion(() =>
        {
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            var stationGrid = mapSystem.CreateGridEntity(map.MapId);
            var poiGrid = mapSystem.CreateGridEntity(map.MapId);
            entMan.System<SharedTransformSystem>().SetWorldPosition(poiGrid.Owner, new Vector2(200, 0));
            for (var x = 0; x < 8; x++)
            {
                mapSystem.SetTile(stationGrid, new Vector2i(x, 0), new Tile(plating.TileId));
                mapSystem.SetTile(poiGrid, new Vector2i(x, 0), new Tile(plating.TileId));
            }

            station = stationGrid.Owner;
            territory = entMan.EnsureComponent<PersistentPoiGridComponent>(poiGrid.Owner);
            territory.Owner = "DSM";
            territory.Level = 2;

            dsm = entMan.SpawnEntity("FactionPortalDSM", new EntityCoordinates(station, 1.5f, 0.5f));
            ncwl = entMan.SpawnEntity("FactionPortalNCWL", new EntityCoordinates(station, 6.5f, 0.5f));
            poi = entMan.SpawnEntity("PersistentPoiFactionPortal", new EntityCoordinates(poiGrid.Owner, 3.5f, 0.5f));
            entMan.System<PowerReceiverSystem>().SetNeedsPower(poi, false);
        });

        await server.WaitRunTicks(120);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<FactionPortalComponent>(poi).Active, Is.True);
            Assert.That(entMan.GetComponent<FactionPortalComponent>(dsm).Active, Is.True);

            var member = SpawnMember(entMan, station, "DSM");
            var outsider = SpawnMember(entMan, station, "NCWL");

            Assert.That(portals.TryTravel(dsm, ncwl, member), Is.False, "a portal sent someone into another faction's network");
            Assert.That(portals.TryTravel(dsm, poi, outsider), Is.False, "an outsider used another faction's portal");

            Assert.That(portals.TryTravel(dsm, poi, member), Is.True);
            Assert.That(entMan.GetComponent<TransformComponent>(member).ParentUid, Is.EqualTo(entMan.GetComponent<TransformComponent>(poi).ParentUid));

            var captor = SpawnMember(entMan, station, "DSM");
            var prisoner = SpawnMember(entMan, station, "NCWL");
            Assert.That(pulling.TryStartPull(captor, prisoner), Is.True);
            Assert.That(portals.TryTravel(dsm, poi, captor), Is.True);
            Assert.That(entMan.GetComponent<TransformComponent>(prisoner).ParentUid, Is.EqualTo(station), "an enemy was dragged through a portal");

            Assert.That(pulling.CanPull(captor, poi), Is.False, "a portal could be dragged off");

            var leader = SpawnMember(entMan, station, "DSM");
            var follower = entMan.SpawnEntity("MobSoldierAIDSMRifleman", new EntityCoordinates(station, 3.5f, 0.5f));
            var holder = entMan.SpawnEntity("MobSoldierAIDSMRifleman", new EntityCoordinates(station, 5.5f, 0.5f));
            Assert.That(squads.TryRecruit(leader, follower), Is.True);
            Assert.That(squads.TryRecruit(leader, holder), Is.True);
            squads.SetOrder(holder, entMan.GetComponent<NpcSquadMemberComponent>(holder), NpcSquadOrder.Defend,
                entMan.GetComponent<TransformComponent>(holder).Coordinates);
            Assert.That(portals.TryTravel(dsm, poi, leader), Is.True);
            Assert.That(entMan.GetComponent<TransformComponent>(follower).ParentUid, Is.EqualTo(entMan.GetComponent<TransformComponent>(poi).ParentUid),
                "the squad didn't follow its leader through the portal");
            Assert.That(entMan.GetComponent<TransformComponent>(holder).ParentUid, Is.EqualTo(station), "a soldier left its post for the portal");

            territory.Level = 1;
        });

        await server.WaitRunTicks(120);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<FactionPortalComponent>(poi).Active, Is.False, "the territory portal stayed open below level 2");

            var member = SpawnMember(entMan, station, "DSM");
            Assert.That(portals.TryTravel(dsm, poi, member), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnMember(IEntityManager entMan, EntityUid grid, string faction)
    {
        var mob = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 4.5f, 0.5f));
        entMan.EnsureComponent<HullrotFactionComponent>(mob).Faction = faction;
        return mob;
    }
}
