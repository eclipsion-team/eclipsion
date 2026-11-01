using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.NpcSquad;
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
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

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
            Load(entMan, user, silo, "SheetPlasma", 20);
            Load(entMan, user, silo, "IngotSilver", 20);

            var stored = entMan.GetComponent<PersistentPoiMaterialSiloComponent>(silo).Stored;
            Assert.That(stored.GetValueOrDefault("Steel"), Is.EqualTo(30));
            Assert.That(stored.GetValueOrDefault("CableHV"), Is.EqualTo(10));
            Assert.That(stored.GetValueOrDefault("Plasma"), Is.EqualTo(20), "the silo refused plasma");
            Assert.That(stored.GetValueOrDefault("Silver"), Is.EqualTo(20), "the silo refused silver");

            // a handful out, not the whole stack
            silos.Take((silo, entMan.GetComponent<PersistentPoiMaterialSiloComponent>(silo)), user, "Plasma", 5);
            Assert.That(stored.GetValueOrDefault("Plasma"), Is.EqualTo(15));
            var held = entMan.System<SharedHandsSystem>().EnumerateHeld(user)
                .Select(held => entMan.GetComponentOrNull<StackComponent>(held))
                .FirstOrDefault(stack => stack?.StackTypeId == "Plasma");
            Assert.That(held?.Count, Is.EqualTo(5), "the five sheets never came out");

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

            // tools or not, nothing comes down by hand any more
            var hands = entMan.System<SharedHandsSystem>();
            foreach (var item in hands.EnumerateHeld(user).ToArray())
            {
                hands.TryDrop(user, item);
            }

            Assert.That(hands.TryPickupAnyHand(user, entMan.SpawnEntity("Wrench", entMan.GetComponent<TransformComponent>(user).Coordinates)));
            Assert.That(hands.TryPickupAnyHand(user, entMan.SpawnEntity("Crowbar", entMan.GetComponent<TransformComponent>(user).Coordinates)));
            Assert.That(dismantle.TryStartDismantle(user, wall), Is.False, "dismantled by hand, away from the construction console");
            Assert.That(dismantle.TryStartTearUp(user, new EntityCoordinates(grid, 5.5f, 1.5f)), Is.False, "tore up floor by hand");
        });

        await server.WaitRunTicks(200);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(wall), Is.False);
            var mapGrid = entMan.GetComponent<MapGridComponent>(grid);
            Assert.That(mapSystem.GetTileRef(grid, mapGrid, new Vector2i(5, 1)).Tile.TypeId, Is.EqualTo(plating.TileId));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AdministrationDrawsFromSiloAndHandsBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var progression = server.System<PersistentPoiProgressionSystem>();
        var levels = server.System<PersistentPoiLevelSystem>();

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            var grid = gridEnt.Owner;
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(plating.TileId));
                }
            }

            var territory = entMan.EnsureComponent<PersistentPoiGridComponent>(grid);
            territory.RegionId = "test_silo_admin";
            territory.Owner = "DSM";
            territory.Level = 1;
            progression.SetLevel(territory.RegionId, 1);

            var user = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 3.5f, 1.5f));
            entMan.EnsureComponent<HullrotFactionComponent>(user).Faction = "DSM";

            var console = entMan.SpawnEntity("ComputerPersistentPoiAdministration", new EntityCoordinates(grid, 2.5f, 1.5f));
            var silo = entMan.SpawnEntity("PersistentPoiMaterialSilo", new EntityCoordinates(grid, 5.5f, 1.5f));
            var stored = entMan.GetComponent<PersistentPoiMaterialSiloComponent>(silo).Stored;

            var next = levels.GetNextLevel(1)!;
            foreach (var (material, required) in next.UpgradeCost)
            {
                stored[material] = required + 7;
            }

            var drawn = progression.DrawFromSilo(grid, territory);
            Assert.That(drawn.Count, Is.EqualTo(next.UpgradeCost.Count), "the console left materials in the silo");
            foreach (var (material, required) in next.UpgradeCost)
            {
                Assert.That(progression.GetDeposited(territory.RegionId, material), Is.EqualTo(required));
                Assert.That(stored.GetValueOrDefault(material), Is.EqualTo(7), $"took more {material} than the level needs");
            }

            // steel back into the silo, then a stack of glass into the hands
            Assert.That(progression.ReturnDeposit(console, user, "Steel", true), Is.True);
            Assert.That(progression.GetDeposited(territory.RegionId, "Steel"), Is.Zero);
            Assert.That(stored.GetValueOrDefault("Steel"), Is.EqualTo(next.UpgradeCost["Steel"] + 7));

            var glass = progression.GetDeposited(territory.RegionId, "Glass");
            Assert.That(progression.ReturnDeposit(console, user, "Glass", false), Is.True);
            Assert.That(progression.GetDeposited(territory.RegionId, "Glass"), Is.EqualTo(glass - 30));

            // the upgrade tops itself up from the silo
            stored["Glass"] = 30;
            Assert.That(progression.DrawFromSilo(grid, territory).Count, Is.EqualTo(2));
            Assert.That(progression.TryUpgrade(grid), Is.True, "the level did not go up with everything deposited");
            Assert.That(territory.Level, Is.EqualTo(2));
            progression.ResetProgress(territory.RegionId);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GarrisonStaysAndDeploymentHolds()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var squads = server.System<NpcSquadSystem>();

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            var grid = gridEnt.Owner;
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 8; x++)
            {
                mapSystem.SetTile(gridEnt, new Vector2i(x, 0), new Tile(plating.TileId));
            }

            var user = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 0.5f, 0.5f));
            entMan.EnsureComponent<HullrotFactionComponent>(user).Faction = "DSM";

            var guard = entMan.SpawnEntity("MobSoldierAIDSMRifleman", new EntityCoordinates(grid, 1.5f, 0.5f));
            entMan.EnsureComponent<TerritoryGarrisonSoldierComponent>(guard).Faction = "DSM";
            Assert.That(squads.CanRecruit(user, guard, out _), Is.False, "a garrison soldier joined a squad");

            var escort = entMan.SpawnEntity("MobSoldierAIDSMRifleman", new EntityCoordinates(grid, 2.5f, 0.5f));
            var marker = entMan.EnsureComponent<TerritoryGarrisonSoldierComponent>(escort);
            marker.Faction = "DSM";
            marker.Escort = true;
            Assert.That(squads.CanRecruit(user, escort, out var reason), Is.True, reason);

            foreach (var proto in new[] { "PersistentPoiCryoSpawner", "PersistentPoiGearRackDSM" })
            {
                var works = entMan.SpawnEntity(proto, new EntityCoordinates(grid, 5.5f, 0.5f));
                Assert.That(entMan.GetComponent<TransformComponent>(works).Anchored, Is.True, $"{proto} came up loose");
                Assert.That(entMan.GetComponent<PhysicsComponent>(works).BodyType, Is.EqualTo(BodyType.Static), $"{proto} can be shoved");
                entMan.DeleteEntity(works);
            }
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
