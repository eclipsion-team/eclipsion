#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.NuclearBlast;
using Content.Server.Atmos.Components;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Body.Organ;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(NuclearBlastSystem))]
public sealed class NuclearBlastTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: NuclearBlastTestCharge
  components:
  - type: NuclearBlast
    radius: 28
";

    private static readonly string[] NuclearPrototypes =
    {
        "AmeController",
        "AmeControllerStation",
        "UnionfallShipNodeDSM",
        "UnionfallShipNodeNCWL",
    };

    [Test]
    public async Task AmeAndDatabasesAreNuclear()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var compFactory = pair.Server.ResolveDependency<IComponentFactory>();

        Assert.Multiple(() =>
        {
            foreach (var id in NuclearPrototypes)
            {
                var proto = protoMan.Index<EntityPrototype>(id);
                Assert.That(proto.TryGetComponent<NuclearBlastComponent>(out var blast, compFactory), Is.True,
                    $"{id} must go off as a nuclear blast.");
                Assert.That(blast!.Radius, Is.EqualTo(28f), $"{id} must have a 28 tile blast radius.");
            }

            // The main objective has to survive a database going off next to it.
            Assert.That(protoMan.Index<EntityPrototype>("UnionfallCapturePoint")
                .TryGetComponent<NuclearBlastImmuneComponent>(out _, compFactory), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Everyone inside the radius is gibbed with their gear, items and structures are erased - including what was
    /// in a locker - while indestructible walls and anything outside the radius are left alone.
    /// </summary>
    [Test]
    public async Task BlastGibsAndErasesEverythingInRadius()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;

        EntityUid human = default;
        EntityUid idCard = default;
        EntityUid crowbar = default;
        EntityUid locker = default;
        EntityUid lockedAway = default;
        EntityUid wall = default;
        EntityUid indestructibleWall = default;
        EntityUid farCrowbar = default;
        EntityUid charge = default;

        await server.WaitPost(() =>
        {
            FillTiles(server, map.Grid, 34);

            EntityCoordinates At(float x, float y) => new(map.Grid, new Vector2(x + 0.5f, y + 0.5f));

            human = entMan.SpawnEntity("MobHuman", At(3, 0));
            idCard = entMan.SpawnEntity("SHIIDCardEmployee", MapCoordinates.Nullspace);
            Assert.That(server.System<InventorySystem>().TryEquip(human, idCard, "id", silent: true, force: true),
                Is.True);

            crowbar = entMan.SpawnEntity("Crowbar", At(-10, 4));
            locker = entMan.SpawnEntity("LockerSteel", At(0, -20));
            lockedAway = entMan.SpawnEntity("Crowbar", At(0, -20));
            Assert.That(server.System<EntityStorageSystem>().Insert(lockedAway, locker), Is.True);
            wall = entMan.SpawnEntity("WallSolid", At(15, 15));
            indestructibleWall = entMan.SpawnEntity("WallPlastitaniumIndestructible", At(-15, -15));
            farCrowbar = entMan.SpawnEntity("Crowbar", At(33, 0));
            charge = entMan.SpawnEntity("NuclearBlastTestCharge", At(0, 0));
        });

        // Let the placeholder organs a fresh body spawns with clean themselves up, as they would long before any blast.
        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            Assert.That(server.System<NuclearBlastSystem>().TryDetonate(charge), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(human), Is.True, "The human inside the radius was not gibbed.");
                Assert.That(entMan.Deleted(idCard), Is.True, "The gibbed human's gear was left behind.");
                Assert.That(entMan.Deleted(crowbar), Is.True, "An item inside the radius survived.");
                Assert.That(entMan.Deleted(locker), Is.True, "A locker inside the radius survived.");
                Assert.That(entMan.Deleted(lockedAway), Is.True, "An item inside a locker survived.");
                Assert.That(entMan.Deleted(wall), Is.True, "A wall inside the radius survived.");
                Assert.That(entMan.Deleted(indestructibleWall), Is.False, "An indestructible wall was erased.");
                Assert.That(entMan.Deleted(farCrowbar), Is.False, "An item outside the radius was erased.");

                // The human is meant to be turned into meat, not quietly deleted.
                var organs = new List<EntityUid>();
                var query = entMan.EntityQueryEnumerator<OrganComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var xform))
                {
                    if (xform.MapID == map.MapId)
                        organs.Add(uid);
                }

                Assert.That(organs, Is.Not.Empty, "Gibbing left no remains behind.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The real AME controller, fireball and all: the blast must gib and erase without the explosion it queues
    /// falling over the entities it already wiped out.
    /// </summary>
    [Test]
    public async Task AmeControllerDetonationGibs()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;

        EntityUid controller = default;
        EntityUid human = default;
        EntityUid crowbar = default;

        await server.WaitPost(() =>
        {
            FillTiles(server, map.Grid, 34);

            controller = entMan.SpawnEntity("AmeController", new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            human = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(20.5f, 0.5f)));
            crowbar = entMan.SpawnEntity("Crowbar", new EntityCoordinates(map.Grid, new Vector2(0.5f, 25.5f)));
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            Assert.That(server.System<NuclearBlastSystem>().TryDetonate(controller), Is.True);
            // A second trigger must not set it off again.
            Assert.That(server.System<NuclearBlastSystem>().TryDetonate(controller), Is.True);
        });

        await pair.RunTicksSync(150);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(controller), Is.True, "The AME controller survived its own blast.");
                Assert.That(entMan.Deleted(human), Is.True, "The human 20 tiles from the AME was not gibbed.");
                Assert.That(entMan.Deleted(crowbar), Is.True, "An item 25 tiles from the AME survived.");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static void FillTiles(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        Entity<MapGridComponent> grid,
        int halfSize)
    {
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var plating = new Tile(tileDefs["Plating"].TileId);
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = -halfSize; x <= halfSize; x++)
        {
            for (var y = -halfSize; y <= halfSize; y++)
            {
                tiles.Add((new Vector2i(x, y), plating));
            }
        }

        server.System<SharedMapSystem>().SetTiles(grid, tiles);

        // Real ships and stations already have an atmosphere. Without one, the crater makes the grid grow one halfway
        // through the explosion clearing out whole chunks, which trips a debug assert in the tile enumerator.
        server.EntMan.EnsureComponent<GridAtmosphereComponent>(grid);
    }
}
