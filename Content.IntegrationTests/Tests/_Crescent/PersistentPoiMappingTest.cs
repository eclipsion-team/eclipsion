using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Territory;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Movement.Components;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class PersistentPoiMappingTest
{
    [Test]
    public async Task WorksConsoleBuildsFromAfarAndLetsGo()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var mapping = server.System<PersistentPoiMappingSystem>();
        var silos = server.System<PersistentPoiSiloSystem>();
        var dismantle = server.System<PersistentPoiDeconstructSystem>();
        var xforms = server.System<SharedTransformSystem>();

        EntityUid grid = default;
        EntityUid user = default;
        EntityUid console = default;
        EntityUid silo = default;
        EntityUid eye = default;

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            grid = gridEnt.Owner;
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 30; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(plating.TileId));
                }
            }

            var territory = entMan.EnsureComponent<PersistentPoiGridComponent>(grid);
            territory.Owner = "DSM";
            territory.Level = 1;

            user = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 1.5f, 1.5f));
            entMan.EnsureComponent<HullrotFactionComponent>(user).Faction = "DSM";

            console = entMan.SpawnEntity("ComputerPersistentPoiMapping", new EntityCoordinates(grid, 2.5f, 1.5f));
            entMan.System<PowerReceiverSystem>().SetNeedsPower(console, false);

            silo = entMan.SpawnEntity("PersistentPoiMaterialSilo", new EntityCoordinates(grid, 0.5f, 0.5f));
            var steel = entMan.SpawnEntity("SheetSteel", entMan.GetComponent<TransformComponent>(user).Coordinates);
            entMan.EventBus.RaiseLocalEvent(silo,
                new InteractUsingEvent(user, steel, silo, entMan.GetComponent<TransformComponent>(silo).Coordinates));
        });

        // power nets only settle every half second
        await server.WaitRunTicks(90);
        await server.WaitAssertion(() =>
        {
            var consoleComp = entMan.GetComponent<PersistentPoiMappingConsoleComponent>(console);
            Assert.That(mapping.StartViewing((console, consoleComp), user), Is.True);

            var viewer = entMan.GetComponent<PersistentPoiMappingViewerComponent>(user);
            eye = viewer.Eye;
            Assert.That(entMan.GetComponent<RelayInputMoverComponent>(user).RelayEntity, Is.EqualTo(eye));
            Assert.That(entMan.GetComponent<EyeComponent>(user).Target, Is.EqualTo(eye));
            Assert.That(entMan.System<ActionBlockerSystem>().CanAttack(user), Is.False, "the operator could still fight");

            // far past the body's own reach, but under the view
            xforms.SetCoordinates(eye, new EntityCoordinates(grid, 22.5f, 1.5f));
            Assert.That(silos.TryBuild(user, "PersistentPoiWall", new EntityCoordinates(grid, 24.5f, 1.5f), Direction.South), Is.True);

            // and the view may not wander off the territory
            xforms.SetCoordinates(eye, new EntityCoordinates(grid, 400f, 1.5f));
        });

        await server.WaitRunTicks(200);
        EntityUid wall = default;
        await server.WaitAssertion(() =>
        {
            var local = entMan.GetComponent<TransformComponent>(eye).LocalPosition;
            Assert.That(local.X, Is.LessThan(40f), "the view left the territory");

            wall = AnchoredAt(entMan, mapSystem, grid, new Vector2i(24, 1), "WallSolidPersistentPoi");
            Assert.That(wall, Is.Not.EqualTo(EntityUid.Invalid), "the wall never went up");
            Assert.That(Steel(entMan, silo), Is.EqualTo(25));

            xforms.SetCoordinates(eye, new EntityCoordinates(grid, 22.5f, 1.5f));
            Assert.That(dismantle.TryStartDismantle(user, wall), Is.True, "the console could not tear down without tools");
        });

        await server.WaitRunTicks(200);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(wall), Is.True, "the wall survived");
            Assert.That(Steel(entMan, silo), Is.EqualTo(30), "the wall was not paid back into the silo");

            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Blunt"), 5);
            entMan.System<DamageableSystem>().TryChangeDamage(user, damage, true, origin: console);

            Assert.That(entMan.HasComponent<PersistentPoiMappingViewerComponent>(user), Is.False, "harm did not end the view");
            Assert.That(entMan.HasComponent<RelayInputMoverComponent>(user), Is.False);
            Assert.That(entMan.GetComponent<EyeComponent>(user).Target, Is.Null);
            Assert.That(entMan.GetComponent<EyeComponent>(user).DrawFov, Is.True);
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(eye), Is.True, "the view outlived its operator");
        });

        await pair.CleanReturnAsync();
    }

    private static int Steel(IEntityManager entMan, EntityUid silo)
    {
        return entMan.GetComponent<PersistentPoiMaterialSiloComponent>(silo).Stored.GetValueOrDefault("Steel");
    }

    private static EntityUid AnchoredAt(IEntityManager entMan, SharedMapSystem maps, EntityUid grid, Vector2i tile, string proto)
    {
        var mapGrid = entMan.GetComponent<MapGridComponent>(grid);
        return maps.GetAnchoredEntities(grid, mapGrid, tile)
            .FirstOrDefault(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == proto);
    }
}
