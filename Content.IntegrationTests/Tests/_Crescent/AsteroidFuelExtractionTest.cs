#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Prototypes;
using Content.Shared._Crescent.Fuel;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Coordinates;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class AsteroidFuelExtractionTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: FuelExtractionMvSupplyDummy
  components:
  - type: NodeContainer
    nodes:
      output:
        !type:CableDeviceNode
        nodeGroupID: MVPower
  - type: PowerSupplier
    voltage: Medium
    supplyRate: 100000
    supplyRampRate: 100000
    supplyRampTolerance: 100000
  - type: Transform
    anchored: true
";

    [Test]
    public async Task SeepsCarryTheirOwnFuel()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var compFactory = pair.Server.ResolveDependency<IComponentFactory>();

        var expected = new[]
        {
            ("FuelSeepBoriatic", "BoriaticFuel"),
            ("FuelSeepAme", "AmeFuel"),
        };

        Assert.Multiple(() =>
        {
            foreach (var (protoId, reagent) in expected)
            {
                var proto = protoMan.Index<EntityPrototype>(protoId);
                Assert.That(proto.TryGetComponent<FuelSeepComponent>(out var seep, compFactory), Is.True,
                    $"{protoId} must be a fuel seep.");
                Assert.That(seep!.Reagent.Id, Is.EqualTo(reagent));
                Assert.That(seep.Reserve, Is.GreaterThan(FixedPoint2.Zero),
                    $"{protoId} must start with something in it.");
            }

            // The drill must remain fuel-agnostic.
            var drill = protoMan.Index<EntityPrototype>("FuelExtractor");
            Assert.That(drill.TryGetComponent<FuelExtractorComponent>(out _, compFactory), Is.True);
            Assert.That(
                protoMan.EnumeratePrototypes<EntityPrototype>()
                    .Count(p => p.TryGetComponent<FuelExtractorComponent>(out _, compFactory)),
                Is.EqualTo(1),
                "The drill must stay a single fuel-agnostic prototype.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LiveWorldgenAsteroidsGenerateSeeps()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var mapSys = entMan.System<SharedMapSystem>();

        Assert.Multiple(() =>
        {
            var config = protoMan.Index<WorldgenConfigPrototype>("RatWorld");
            Assert.That(config.Components.TryGetComponent("BiomeSelection", out var registration), Is.True);
            var biomes = ((BiomeSelectionComponent) registration!).Biomes;

            // The default belts must include their seep-bearing asteroid types. RatCraster is not among them
            // any more: the Craster belts were removed and their chromite folded into the two Taypan belts.
            Assert.That(biomes, Contains.Item("RatAsteroidsStandardTaypanOne"));
            Assert.That(biomes, Contains.Item("RatAsteroidsStandardTaypanTwo"));
        });

        const int samples = 25;
        var boriaticSeeps = 0;
        var ameSeeps = 0;
        var chromiteAsteroids = new List<EntityUid>(samples);

        await server.WaitPost(() =>
        {
            mapSys.CreateMap(out var mapId);

            for (var i = 0; i < samples; i++)
            {
                entMan.SpawnEntity("AsteroidDebrisNFLarge", new MapCoordinates(new Vector2(i * 200, 0), mapId));
                chromiteAsteroids.Add(entMan.SpawnEntity("AsteroidDebrisNFChromiteLarge",
                    new MapCoordinates(new Vector2(i * 200, 400), mapId)));
            }
        });

        // Allow LocalityLoaderSystem to populate the asteroid.
        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            var seeps = entMan.EntityQueryEnumerator<FuelSeepComponent>();
            while (seeps.MoveNext(out var seep))
            {
                if (seep.Reagent == "BoriaticFuel")
                    boriaticSeeps++;
                else if (seep.Reagent == "AmeFuel")
                    ameSeeps++;
            }

            foreach (var asteroid in chromiteAsteroids)
            {
                var chromiteWalls = 0;
                var asteroidAmeSeeps = 0;
                var children = entMan.GetComponent<TransformComponent>(asteroid).ChildEnumerator;

                while (children.MoveNext(out var child))
                {
                    var prototype = entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                    if (prototype?.StartsWith("WallRockChromite") == true)
                        chromiteWalls++;

                    if (entMan.TryGetComponent<FuelSeepComponent>(child, out var seep) && seep.Reagent == "AmeFuel")
                        asteroidAmeSeeps++;
                }

                Assert.Multiple(() =>
                {
                    Assert.That(chromiteWalls, Is.GreaterThan(0),
                        $"{asteroid} generated without any chromite rock walls.");
                    Assert.That(asteroidAmeSeeps, Is.GreaterThanOrEqualTo(1),
                        $"{asteroid} must contain at least one accessible AME seep.");
                });
            }
        });

        Assert.Multiple(() =>
        {
            // Keep a wide band for procedural generation variance.
            Assert.That(boriaticSeeps, Is.InRange(samples / 2, samples * 3),
                $"{samples} sand asteroids gave {boriaticSeeps} boriatic seeps; expected roughly one per asteroid.");
            Assert.That(ameSeeps, Is.InRange(samples / 2, samples * 3),
                $"{samples} chromite asteroids gave {ameSeeps} AME seeps; expected roughly one per asteroid.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PoweredDrillDrainsSeepIntoSlottedContainer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();
        var itemSlots = entMan.System<ItemSlotsSystem>();

        EntityUid seepEnt = default;
        EntityUid drillEnt = default;
        EntityUid canister = default;
        FixedPoint2 startingReserve = default;

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);

            // Power the drill through an anchored MV cable.
            for (var i = 0; i < 2; i++)
            {
                mapSys.SetTile(grid, new Vector2i(0, i), new Tile(1));
                entMan.SpawnEntity("CableMV", grid.Owner.ToCoordinates(0, i));
            }

            entMan.SpawnEntity("FuelExtractionMvSupplyDummy", grid.Owner.ToCoordinates(0, 1));

            seepEnt = entMan.SpawnEntity("FuelSeepBoriatic", grid.Owner.ToCoordinates(0, 0));
            drillEnt = entMan.SpawnEntity("FuelExtractor", grid.Owner.ToCoordinates(0, 0));
            Assert.That(entMan.GetComponent<TransformComponent>(seepEnt).Anchored, Is.True,
                "Seeps must anchor on spawn or worldgen would leave them floating.");
            xformSys.AnchorEntity(drillEnt);

            // Shorten the pump cycle for the test.
            entMan.GetComponent<FuelExtractorComponent>(drillEnt).CycleDelay = TimeSpan.FromSeconds(0.1);
            startingReserve = entMan.GetComponent<FuelSeepComponent>(seepEnt).Reserve;
        });

        // Wait for the power net and pump cycle.
        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            var consumer = entMan.GetComponent<PowerConsumerComponent>(drillEnt);
            Assert.That(consumer.ReceivedPower, Is.EqualTo(consumer.DrawRate).Within(0.1),
                "The drill must draw straight off an MV wire - asteroids have nowhere to hang an APC.");
        });

        await server.WaitAssertion(() =>
        {
            var seep = entMan.GetComponent<FuelSeepComponent>(seepEnt);
            Assert.That(seep.Reserve, Is.LessThan(startingReserve), "The drill should have drained the seep.");

            Assert.That(solutionSys.TryGetSolution(drillEnt, "buffer", out _, out var buffer), Is.True);
            Assert.That(buffer!.GetTotalPrototypeQuantity("BoriaticFuel"), Is.GreaterThan(FixedPoint2.Zero),
                "The buffer should hold the seep's reagent, not the drill's.");

            canister = entMan.SpawnEntity("JugBoriaticFuelEmpty", entMan.GetComponent<TransformComponent>(drillEnt).Coordinates);
            Assert.That(itemSlots.TryInsert(drillEnt, "fuelContainer", canister, null), Is.True,
                "The cradle must accept any refillable container.");
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(solutionSys.TryGetSolution(canister, "beaker", out _, out var canSolution), Is.True);
            Assert.That(canSolution!.GetTotalPrototypeQuantity("BoriaticFuel"), Is.GreaterThan(FixedPoint2.Zero),
                "The drill should have bled its buffer into the slotted container.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullDrillSpillsOntoTheFloor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();

        EntityUid seepEnt = default;
        EntityUid drillEnt = default;
        FixedPoint2 startingReserve = default;

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);

            for (var i = 0; i < 2; i++)
            {
                mapSys.SetTile(grid, new Vector2i(0, i), new Tile(1));
                entMan.SpawnEntity("CableMV", grid.Owner.ToCoordinates(0, i));
            }

            entMan.SpawnEntity("FuelExtractionMvSupplyDummy", grid.Owner.ToCoordinates(0, 1));

            seepEnt = entMan.SpawnEntity("FuelSeepBoriatic", grid.Owner.ToCoordinates(0, 0));
            drillEnt = entMan.SpawnEntity("FuelExtractor", grid.Owner.ToCoordinates(0, 0));
            xformSys.AnchorEntity(drillEnt);
            entMan.GetComponent<FuelExtractorComponent>(drillEnt).CycleDelay = TimeSpan.FromSeconds(0.1);

            // Fill the buffer so the next cycle spills.
            Assert.That(solutionSys.TryGetSolution(drillEnt, "buffer", out var soln, out var buffer), Is.True);
            solutionSys.TryAddReagent(soln!.Value, "BoriaticFuel", buffer!.AvailableVolume, out _);

            startingReserve = entMan.GetComponent<FuelSeepComponent>(seepEnt).Reserve;
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<FuelSeepComponent>(seepEnt).Reserve, Is.LessThan(startingReserve),
                "A full tank must not stop the drill.");

            var spilled = FixedPoint2.Zero;
            var puddles = entMan.EntityQueryEnumerator<PuddleComponent>();
            while (puddles.MoveNext(out var puddleUid, out var puddle))
            {
                if (solutionSys.TryGetSolution(puddleUid, puddle.SolutionName, out _, out var puddleSolution))
                    spilled += puddleSolution.GetTotalPrototypeQuantity("BoriaticFuel");
            }

            Assert.That(spilled, Is.GreaterThan(FixedPoint2.Zero),
                "Fuel the drill could not store should be on the floor.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DrillShipsAsAResearchedFlatpack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();
        var mapSys = entMan.System<SharedMapSystem>();

        Assert.Multiple(() =>
        {
            var flatpack = protoMan.Index<EntityPrototype>("FuelExtractorFlatpack");
            Assert.That(flatpack.TryGetComponent<FlatpackComponent>(out var pack, compFactory), Is.True);
            Assert.That(pack!.Entity, Is.EqualTo(new EntProtoId("FuelExtractor")));

            var lathe = protoMan.Index<EntityPrototype>("BoriaticRefiner");
            Assert.That(lathe.TryGetComponent<LatheComponent>(out var latheComp, compFactory), Is.True);
            Assert.That(latheComp!.DynamicRecipes.Select(r => r.Id), Contains.Item("FuelExtractorFlatpackCraft"));
            Assert.That(latheComp.StaticRecipes.Select(r => r.Id), Does.Not.Contain("FuelExtractorFlatpackCraft"));

            var tech = protoMan.Index<TechnologyPrototype>("AstronauticsSeepExtraction");
            Assert.That(tech.RecipeUnlocks.Select(r => r.Id), Contains.Item("FuelExtractorFlatpackCraft"));
        });

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));

            var drill = entMan.SpawnEntity("FuelExtractor", grid.Owner.ToCoordinates(0, 0));
            Assert.That(entMan.GetComponent<TransformComponent>(drill).Anchored, Is.False,
                "An unpacked drill has to be wrenched down, not arrive anchored.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DrillOnBareRockProducesNothing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();

        EntityUid drillEnt = default;

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);

            for (var i = 0; i < 2; i++)
            {
                mapSys.SetTile(grid, new Vector2i(0, i), new Tile(1));
                entMan.SpawnEntity("CableMV", grid.Owner.ToCoordinates(0, i));
            }

            entMan.SpawnEntity("FuelExtractionMvSupplyDummy", grid.Owner.ToCoordinates(0, 1));

            drillEnt = entMan.SpawnEntity("FuelExtractor", grid.Owner.ToCoordinates(0, 0));
            xformSys.AnchorEntity(drillEnt);
            entMan.GetComponent<FuelExtractorComponent>(drillEnt).CycleDelay = TimeSpan.FromSeconds(0.1);
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(solutionSys.TryGetSolution(drillEnt, "buffer", out _, out var buffer), Is.True);
            Assert.That(buffer!.Volume, Is.EqualTo(FixedPoint2.Zero),
                "A drill with no seep under it must not conjure fuel.");
        });

        await pair.CleanReturnAsync();
    }
}
