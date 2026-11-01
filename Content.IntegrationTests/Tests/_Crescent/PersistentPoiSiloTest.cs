using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Territory;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class PersistentPoiSiloTest
{
    [Test]
    public async Task SiloBuildsAndDismantleTearsDown()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var silos = server.System<PersistentPoiSiloSystem>();
        var dismantle = server.System<PersistentPoiDeconstructSystem>();

        EntityUid grid = default;
        EntityUid user = default;
        EntityUid silo = default;
        var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            grid = gridEnt.Owner;
            for (var x = 0; x < 12; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(plating.TileId));
                }
            }

            var territory = entMan.EnsureComponent<PersistentPoiGridComponent>(grid);
            territory.Owner = "DSM";
            territory.Level = 1;
            territory.ProtectedCenter = new Vector2(6.5f, 0.5f);
            territory.ProtectedRadius = 0.6f;

            user = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 6.5f, 1.5f));
            entMan.EnsureComponent<HullrotFactionComponent>(user).Faction = "DSM";
            entMan.EnsureComponent<PersistentPoiBuilderComponent>(user);

            silo = entMan.SpawnEntity("PersistentPoiMaterialSilo", new EntityCoordinates(grid, 1.5f, 1.5f));
            Load(entMan, user, silo, "SheetSteel", 30);
            Load(entMan, user, silo, "CableHVStack", 10);

            var stored = entMan.GetComponent<PersistentPoiMaterialSiloComponent>(silo).Stored;
            Assert.That(stored.GetValueOrDefault("Steel"), Is.EqualTo(30));
            Assert.That(stored.GetValueOrDefault("CableHV"), Is.EqualTo(10));

            var wallSpot = new EntityCoordinates(grid, 7.5f, 1.5f);
            Assert.That(silos.TryBuild(user, "PersistentPoiWall", wallSpot, Direction.South), Is.True);
            Assert.That(silos.TryBuild(user, "PersistentPoiSubstation", new EntityCoordinates(grid, 9.5f, 1.5f), Direction.South),
                Is.False, "the silo built a substation it holds no cable coil for");
        });

        await server.WaitRunTicks(200);
        EntityUid wall = default;
        await server.WaitAssertion(() =>
        {
            wall = AnchoredAt(entMan, mapSystem, grid, new Vector2i(7, 1), "WallSolidPersistentPoi");
            Assert.That(wall, Is.Not.EqualTo(EntityUid.Invalid), "the silo never put the wall up");
            Assert.That(entMan.GetComponent<PersistentPoiMaterialSiloComponent>(silo).Stored.GetValueOrDefault("Steel"), Is.EqualTo(25));

            // cable goes in under the wall straight from the silo
            var wallSpot = new EntityCoordinates(grid, 7.5f, 1.5f);
            Assert.That(silos.TryBuild(user, "PersistentPoiCableHV", wallSpot, Direction.South), Is.True);
        });

        await server.WaitRunTicks(200);
        await server.WaitAssertion(() =>
        {
            Assert.That(AnchoredAt(entMan, mapSystem, grid, new Vector2i(7, 1), "CableHVPersistentPoi"), Is.Not.EqualTo(EntityUid.Invalid),
                "no cable under the wall");
            Assert.That(silos.TryBuild(user, "PersistentPoiCableHV", new EntityCoordinates(grid, 7.5f, 1.5f), Direction.South), Is.False,
                "a second cable of the same kind went onto the tile");

            Assert.That(dismantle.TryStartDismantle(user, wall), Is.False, "dismantled without tools in hand");

            var hands = entMan.System<SharedHandsSystem>();
            Assert.That(hands.TryPickupAnyHand(user, entMan.SpawnEntity("Wrench", entMan.GetComponent<TransformComponent>(user).Coordinates)));
            Assert.That(hands.TryPickupAnyHand(user, entMan.SpawnEntity("Crowbar", entMan.GetComponent<TransformComponent>(user).Coordinates)));

            Assert.That(dismantle.TryStartTearUp(user, new EntityCoordinates(grid, 6.5f, 0.5f)), Is.False, "the permanent floor came up");
            Assert.That(dismantle.TryStartDismantle(user, wall), Is.True);
        });

        await server.WaitRunTicks(300);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(wall), Is.True, "the wall survived its dismantling");
            Assert.That(dismantle.TryStartTearUp(user, new EntityCoordinates(grid, 5.5f, 1.5f)), Is.True);
        });

        await server.WaitRunTicks(200);
        await server.WaitAssertion(() =>
        {
            var mapGrid = entMan.GetComponent<MapGridComponent>(grid);
            Assert.That(mapSystem.GetTileRef(grid, mapGrid, new Vector2i(5, 1)).Tile.TypeId, Is.Not.EqualTo(plating.TileId),
                "the plating was not torn up");
            Assert.That(mapSystem.GetTileRef(grid, mapGrid, new Vector2i(6, 0)).Tile.TypeId, Is.EqualTo(plating.TileId));
        });

        await pair.CleanReturnAsync();
    }

    private static void Load(IEntityManager entMan, EntityUid user, EntityUid silo, string stack, int count)
    {
        var item = entMan.SpawnEntity(stack, entMan.GetComponent<TransformComponent>(user).Coordinates);
        entMan.System<SharedStackSystem>().SetCount(item, count);
        var ev = new InteractUsingEvent(user, item, silo, entMan.GetComponent<TransformComponent>(silo).Coordinates);
        entMan.EventBus.RaiseLocalEvent(silo, ev);
    }

    private static EntityUid AnchoredAt(IEntityManager entMan, SharedMapSystem maps, EntityUid grid, Vector2i tile, string proto)
    {
        var mapGrid = entMan.GetComponent<MapGridComponent>(grid);
        return maps.GetAnchoredEntities(grid, mapGrid, tile)
            .FirstOrDefault(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == proto);
    }
}
