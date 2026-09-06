using System.Collections.Generic;
using System.Linq;
using Content.Server.Ame.EntitySystems;
using Content.Server.Chemistry.Components;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared._Crescent.ShipPower;
using Content.Shared.Ame.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Power.Components;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Storage.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class ShipFuelEconomyTest
{
    [Test]
    public async Task BothFuelsAreCraftableThroughChemistry()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();

        Assert.Multiple(() =>
        {
            Assert.That(protoMan.HasIndex<ReagentPrototype>("BoriaticFuel"), Is.True);
            Assert.That(protoMan.HasIndex<ReagentPrototype>("AmeFuel"), Is.True);
            Assert.That(protoMan.HasIndex<ReagentPrototype>("AmeCatalyst"), Is.True);
        });

        var boriatic = protoMan.Index<ReactionPrototype>("BoriaticFuel");
        var catalyst = protoMan.Index<ReactionPrototype>("AmeCatalyst");
        var ame = protoMan.Index<ReactionPrototype>("AmeFuel");

        Assert.Multiple(() =>
        {
            Assert.That(boriatic.Products.ContainsKey("BoriaticFuel"), Is.True);
            Assert.That(ame.Products.ContainsKey("AmeFuel"), Is.True);

            Assert.That(boriatic.Reactants, Has.Count.LessThan(ame.Reactants.Count),
                "Boriatic must stay the simple recipe.");
            Assert.That(ame.Reactants.ContainsKey("AmeCatalyst"), Is.True,
                "AME fuel must go through its intermediate rather than being a one-step mix.");
            Assert.That(catalyst.MinimumTemperature, Is.GreaterThan(0f));
            Assert.That(ame.MinimumTemperature, Is.GreaterThan(catalyst.MinimumTemperature));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FuelCanistersScaleOnlyInCapacity()
    {
        var expected = new Dictionary<string, int>
        {
            ["JugBoriaticFuelEmpty"] = 200,
            ["FuelCanisterBoriaticT2"] = 500,
            ["FuelCanisterBoriaticT3"] = 1000,
        };

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            foreach (var (proto, capacity) in expected)
            {
                var uid = entMan.SpawnEntity(proto, map.GridCoords);

                Assert.That(solutionSys.TryGetSolution(uid, "beaker", out _, out var solution), Is.True,
                    $"{proto} must carry a fuel solution.");
                Assert.That(solution!.MaxVolume.Int(), Is.EqualTo(capacity),
                    $"{proto} has the wrong capacity.");
                Assert.That(solution.Volume.Int(), Is.Zero,
                    $"{proto} must fabricate empty - chemistry fills it, not the lathe.");

                entMan.DeleteEntity(uid);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FuelTanksHaveCanonicalContentsAndAreBidirectional()
    {
        const int tankCapacity = 2000;
        var tanks = new[] { "BoriaticFuelTank", "AmeFuelTank" };
        var filled = new Dictionary<string, string>
        {
            ["BoriaticFuelTankFull"] = "BoriaticFuel",
            ["AmeFuelTankFull"] = "AmeFuel",
        };

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            foreach (var proto in tanks)
            {
                var uid = entMan.SpawnEntity(proto, map.GridCoords);

                Assert.Multiple(() =>
                {
                    Assert.That(entMan.HasComponent<SolutionRegenerationComponent>(uid), Is.False,
                        $"{proto} must not regenerate fuel.");
                    Assert.That(entMan.HasComponent<DrainableSolutionComponent>(uid), Is.True,
                        $"{proto} must be drainable.");
                    Assert.That(entMan.HasComponent<RefillableSolutionComponent>(uid), Is.True,
                        $"{proto} must accept fuel back from portable containers.");
                });

                Assert.That(solutionSys.TryGetSolution(uid, "tank", out _, out var solution), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(solution!.MaxVolume.Int(), Is.EqualTo(tankCapacity));
                    Assert.That(solution.Volume.Int(), Is.Zero, $"{proto} must spawn empty.");
                });

                entMan.DeleteEntity(uid);
            }

            foreach (var (proto, reagent) in filled)
            {
                var uid = entMan.SpawnEntity(proto, map.GridCoords);

                Assert.That(solutionSys.TryGetSolution(uid, "tank", out _, out var solution), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(solution!.MaxVolume.Int(), Is.EqualTo(tankCapacity));
                    Assert.That(solution.Volume.Int(), Is.EqualTo(tankCapacity));
                    Assert.That(solution.GetTotalPrototypeQuantity(reagent).Int(), Is.EqualTo(tankCapacity),
                        $"{proto} must contain only the canonical, producible {reagent} reagent.");
                });

                entMan.DeleteEntity(uid);
            }

            var tankUid = entMan.SpawnEntity("BoriaticFuelTankFull", map.GridCoords);
            var canisterUid = entMan.SpawnEntity("JugBoriaticFuelEmpty", map.GridCoords);
            var transferSys = entMan.System<SolutionTransferSystem>();
            var direction = entMan.GetComponent<SolutionTransferDirectionComponent>(canisterUid);

            Assert.That(solutionSys.TryGetSolution(tankUid, "tank", out _, out var tankSolution), Is.True);
            Assert.That(solutionSys.TryGetSolution(canisterUid, "beaker", out _, out var canisterSolution), Is.True);
            Assert.That(direction.Direction, Is.EqualTo(SolutionTransferDirection.Receive));

            var interact = new AfterInteractEvent(
                canisterUid,
                canisterUid,
                tankUid,
                map.GridCoords,
                true);
            entMan.EventBus.RaiseLocalEvent(canisterUid, interact);

            Assert.Multiple(() =>
            {
                Assert.That(tankSolution!.Volume.Int(), Is.EqualTo(1995),
                    "Receive mode must draw from a refillable tank.");
                Assert.That(canisterSolution!.Volume.Int(), Is.EqualTo(5));
            });

            transferSys.SetDirection((canisterUid, direction), SolutionTransferDirection.Send);
            interact = new AfterInteractEvent(canisterUid, canisterUid, tankUid, map.GridCoords, true);
            entMan.EventBus.RaiseLocalEvent(canisterUid, interact);

            Assert.Multiple(() =>
            {
                Assert.That(tankSolution.Volume.Int(), Is.EqualTo(tankCapacity),
                    "Send mode must return fuel to the tank.");
                Assert.That(canisterSolution.Volume.Int(), Is.Zero);
            });

            entMan.DeleteEntity(canisterUid);
            entMan.DeleteEntity(tankUid);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FuelIsPurchasableAndAntimatterCostsMorePerUnit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var compFactory = pair.Server.ResolveDependency<IComponentFactory>();

        var boriaticCrate = protoMan.Index<EntityPrototype>("CrateFuelBoriatic");
        Assert.That(boriaticCrate.TryGetComponent<StorageFillComponent>(out var boriaticFill, compFactory), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(boriaticFill!.Contents, Has.Count.EqualTo(1));
            Assert.That(boriaticFill.Contents[0].PrototypeId, Is.EqualTo("JugBoriaticFuel"));
        });

        var ameCrate = protoMan.Index<EntityPrototype>("CrateEngineeringAMEJar");
        Assert.That(ameCrate.TryGetComponent<StorageFillComponent>(out var ameFill, compFactory), Is.True);
        Assert.That(ameFill!.Contents[0].PrototypeId, Is.EqualTo("AmeJar"));

        var boriaticProduct = protoMan.Index<CargoProductPrototype>("FuelBoriaticGeneric");
        var ameProduct = protoMan.Index<CargoProductPrototype>("EngineAmeJarGeneric");

        var boriaticPerUnit = boriaticProduct.Cost / 200f;
        var amePerUnit = ameProduct.Cost / (float) (ameFill.Contents[0].Amount * 1000);

        Assert.That(amePerUnit, Is.GreaterThan(boriaticPerUnit * 0.2f),
            "Antimatter must not be an order of magnitude cheaper per unit than boriatic, or nobody "
            + "will ever run the chemistry line.");

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CapacityTiersAreGatedBehindResearch()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var compFactory = pair.Server.ResolveDependency<IComponentFactory>();

        var tier2 = protoMan.Index<TechnologyPrototype>("AstronauticsFuelStorage");
        var tier3 = protoMan.Index<TechnologyPrototype>("AstronauticsBulkFuelStorage");

        Assert.Multiple(() =>
        {
            Assert.That(tier2.RecipeUnlocks.Select(r => r.Id),
                Is.EquivalentTo(new[] { "FuelCanisterBoriaticT2Craft", "AmeJarT2Craft", "BoriaticFuelTankCraft", "AmeFuelTankCraft" }));
            Assert.That(tier3.RecipeUnlocks.Select(r => r.Id),
                Is.EquivalentTo(new[] { "FuelCanisterBoriaticT3Craft", "AmeJarT3Craft" }));
            Assert.That(tier3.TechnologyPrerequisites.Select(t => t.Id), Contains.Item("AstronauticsFuelStorage"));
            Assert.That(tier3.Cost, Is.GreaterThan(tier2.Cost));
        });

        var refiner = protoMan.Index<EntityPrototype>("BoriaticRefiner");
        Assert.That(refiner.TryGetComponent<Content.Shared.Lathe.LatheComponent>(out var lathe, compFactory), Is.True);

        Assert.Multiple(() =>
        {
            var statics = lathe!.StaticRecipes.Select(r => r.Id).ToList();
            Assert.That(statics, Contains.Item("JugBoriaticFuelEmptyCraft"));

            var dynamics = lathe.DynamicRecipes.Select(r => r.Id).ToList();
            Assert.That(dynamics, Contains.Item("FuelCanisterBoriaticT2Craft"));
            Assert.That(dynamics, Contains.Item("FuelCanisterBoriaticT3Craft"));
            Assert.That(dynamics, Contains.Item("AmeJarT2Craft"));
            Assert.That(dynamics, Contains.Item("AmeJarT3Craft"));
        });

        var microforge = protoMan.Index<EntityPrototype>("PristineMicroforge");
        Assert.That(microforge.TryGetComponent<Content.Shared.Lathe.LatheComponent>(out var forgeLathe, compFactory),
            Is.True);
        Assert.Multiple(() =>
        {
            var dynamics = forgeLathe!.DynamicRecipes.Select(r => r.Id).ToList();
            Assert.That(dynamics, Contains.Item("AmeJarT2Craft"));
            Assert.That(dynamics, Contains.Item("AmeJarT3Craft"));
        });

        var factionServers = new[]
        {
            "ResearchAndDevelopmentServerMinutemen",
            "ResearchAndDevelopmentServerInterdyne",
            "ResearchAndDevelopmentServerCyberdawn",
            "ResearchAndDevelopmentServerImperial",
            "ResearchAndDevelopmentServerCommunard",
            "ResearchAndDevelopmentServerCorporate",
            "ResearchAndDevelopmentServerGliessian",
            "ResearchAndDevelopmentServerFamilies",
            "ResearchAndDevelopmentServerCoalition",
        };

        foreach (var serverId in factionServers)
        {
            var researchServer = protoMan.Index<EntityPrototype>(serverId);
            Assert.That(researchServer.TryGetComponent<TechnologyDatabaseComponent>(out var database, compFactory),
                Is.True, $"{serverId} must have a technology database.");
            Assert.That(database!.SupportedDisciplines, Contains.Item("Astronautics"),
                $"{serverId} must expose the shared fuel-storage research.");
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AmeJarTiersHaveExpectedContainmentAndFabricateEmpty()
    {
        var tiers = new[]
        {
            (Full: "AmeJar", Empty: "AmeJarEmpty", Capacity: 1000, Transfer: 250),
            (Full: "AmeJarT2", Empty: "AmeJarT2Empty", Capacity: 5000, Transfer: 1250),
            (Full: "AmeJarT3", Empty: "AmeJarT3Empty", Capacity: 10000, Transfer: 2500),
        };

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            foreach (var tier in tiers)
            {
                var fullUid = entMan.SpawnEntity(tier.Full, map.GridCoords);
                var emptyUid = entMan.SpawnEntity(tier.Empty, map.GridCoords);
                var full = entMan.GetComponent<AmeFuelContainerComponent>(fullUid);
                var empty = entMan.GetComponent<AmeFuelContainerComponent>(emptyUid);
                var transfer = entMan.GetComponent<SolutionTransferComponent>(emptyUid);

                Assert.Multiple(() =>
                {
                    Assert.That(full.FuelCapacity, Is.EqualTo(tier.Capacity));
                    Assert.That(full.FuelAmount, Is.EqualTo(tier.Capacity));
                    Assert.That(empty.FuelCapacity, Is.EqualTo(tier.Capacity));
                    Assert.That(empty.FuelAmount, Is.Zero,
                        $"{tier.Empty} is the fabricated variant and must not create free AME fuel.");
                    Assert.That(transfer.TransferAmount.Int(), Is.EqualTo(tier.Transfer));
                    Assert.That(transfer.MaximumTransferAmount.Int(), Is.EqualTo(tier.Capacity),
                        $"{tier.Empty} should be fillable without a ten-click transfer cap.");
                });

                Assert.That(solutionSys.TryGetSolution(fullUid, AmeFuelRefillSystem.RefillSolution,
                    out _, out var fullSolution), Is.True);
                Assert.That(solutionSys.TryGetSolution(emptyUid, AmeFuelRefillSystem.RefillSolution,
                    out _, out var emptySolution), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(fullSolution!.MaxVolume.Int(), Is.EqualTo(tier.Capacity));
                    Assert.That(emptySolution!.MaxVolume.Int(), Is.EqualTo(tier.Capacity));
                });

                entMan.DeleteEntity(fullUid);
                entMan.DeleteEntity(emptyUid);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AmeJarsRefillFromTheFuelReagent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();

        EntityUid fullUid = default;
        EntityUid jarUid = default;
        Entity<SolutionComponent>? soln = null;

        await server.WaitPost(() =>
        {
            fullUid = entMan.SpawnEntity("AmeJar", map.GridCoords);
            jarUid = entMan.SpawnEntity("AmeJarEmpty", map.GridCoords);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var full = entMan.GetComponent<AmeFuelContainerComponent>(fullUid);
            Assert.Multiple(() =>
            {
                Assert.That(full.FuelAmount, Is.EqualTo(full.FuelCapacity), "A stock jar must spawn full.");
                Assert.That(solutionSys.TryGetSolution(fullUid, AmeFuelRefillSystem.RefillSolution, out _, out var fullSoln), Is.True);
                Assert.That(fullSoln!.GetTotalPrototypeQuantity(AmeFuelRefillSystem.FuelReagent).Int(),
                    Is.EqualTo(full.FuelCapacity),
                    "A full jar must actually contain the reagent, not just claim a number.");
            });

            var jar = entMan.GetComponent<AmeFuelContainerComponent>(jarUid);
            Assert.That(jar.FuelAmount, Is.Zero, "The empty variant must spawn spent.");
            Assert.That(solutionSys.TryGetSolution(jarUid, AmeFuelRefillSystem.RefillSolution, out var s, out _), Is.True);
            soln = s;

            solutionSys.TryAddReagent(soln!.Value, AmeFuelRefillSystem.FuelReagent, FixedPoint2.New(400));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var jar = entMan.GetComponent<AmeFuelContainerComponent>(jarUid);
            Assert.Multiple(() =>
            {
                Assert.That(jar.FuelAmount, Is.EqualTo(400), "Fuel must track the reagent one to one.");
                Assert.That(soln!.Value.Comp.Solution.GetTotalPrototypeQuantity(AmeFuelRefillSystem.FuelReagent),
                    Is.EqualTo(FixedPoint2.New(400)),
                    "The reagent must stay in the jar - it IS the fuel, not a feedstock for it.");
                Assert.That(soln.Value.Comp.Solution.MaxVolume.Int(), Is.EqualTo(jar.FuelCapacity),
                    "Solution volume and fuel rating must be kept equal, or the two readings diverge.");
            });

            entMan.DeleteEntity(jarUid);
            entMan.DeleteEntity(fullUid);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BoriaticLadderIsCompleteAndMonotonic()
    {
        var boriatic = new[] { "BoriaticGeneratorHercules", "BoriaticGeneratorEinstein", "BoriaticGeneratorTitan" };

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var solutionSys = entMan.System<SharedSolutionContainerSystem>();
        var generatorSys = entMan.System<GeneratorSystem>();

        await server.WaitAssertion(() =>
        {
            var output = new Dictionary<string, float>();

            foreach (var proto in boriatic)
            {
                var uid = entMan.SpawnEntity(proto, map.GridCoords);
                var gen = entMan.GetComponent<FuelGeneratorComponent>(uid);
                output[proto] = gen.MaxTargetPower;

                Assert.That(solutionSys.TryGetSolution(uid, "tank", out var fuelSolution, out _), Is.True);
                solutionSys.TryAddReagent(fuelSolution!.Value, "BoriaticFuel", FixedPoint2.New(10));

                Assert.Multiple(() =>
                {
                    Assert.That(generatorSys.GetFuel(uid), Is.EqualTo(10f),
                        $"{proto} must burn the same BoriaticFuel reagent produced by chemistry and stored in tanks.");
                    Assert.That(generatorSys.GetIsClogged(uid), Is.False,
                        $"{proto} must treat canonical BoriaticFuel as valid fuel, not contamination.");
                    Assert.That(protoMan.HasIndex<EntityPrototype>($"{proto}Shuttle"), Is.True,
                        $"{proto} needs a hull-mounted variant for mappers.");
                });

                entMan.DeleteEntity(uid);
            }

            Assert.Multiple(() =>
            {
                for (var i = 1; i < boriatic.Length; i++)
                {
                    Assert.That(output[boriatic[i]], Is.GreaterThan(output[boriatic[i - 1]]),
                        $"{boriatic[i]} must out-produce {boriatic[i - 1]}.");
                }

                foreach (var proto in new[] { "AmeController", "AmeShielding", "AmeJar" })
                    Assert.That(protoMan.HasIndex<EntityPrototype>(proto), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WeaponsDrawMorePowerWhileFiringAndScaleWithDoctrine()
    {
        var weapons = new[]
        {
            "WeaponTurretRetribution",      // energy, small
            "WeaponTurretDamnation",        // energy, large
            "WeaponTurretSmallMachinegun",  // ballistic, small
            "WeaponTurretLargeArtillery",   // ballistic, large
            "BaseWeaponTurretShortbow",     // missile, small
            "BaseWeaponTurretCatapult",     // missile, large
        };

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var stats = new Dictionary<string, (float PerShot, float Draw, float Capacity)>();

            foreach (var proto in weapons)
            {
                var uid = entMan.SpawnEntity(proto, map.GridCoords);
                var draw = entMan.GetComponent<WeaponPowerDrawComponent>(uid);
                var battery = entMan.GetComponent<BatteryComponent>(uid);
                var apcBattery = entMan.GetComponent<ApcPowerReceiverBatteryComponent>(uid);

                Assert.Multiple(() =>
                {
                    Assert.That(draw.EnergyPerShot, Is.GreaterThan(0f),
                        $"{proto} must cost energy to fire.");
                    Assert.That(battery.MaxCharge, Is.GreaterThanOrEqualTo(draw.EnergyPerShot * 2),
                        $"{proto} must hold at least two shots of charge.");
                    Assert.That(apcBattery.BatteryRechargeRate, Is.GreaterThan(0f),
                        $"{proto} must place a real load on the grid while refilling.");
                });

                stats[proto] = (draw.EnergyPerShot, apcBattery.BatteryRechargeRate, battery.MaxCharge);
                entMan.DeleteEntity(uid);
            }

            Assert.Multiple(() =>
            {
                Assert.That(stats["WeaponTurretDamnation"].Draw,
                    Is.GreaterThan(stats["WeaponTurretRetribution"].Draw));
                Assert.That(stats["WeaponTurretLargeArtillery"].Draw,
                    Is.GreaterThan(stats["WeaponTurretSmallMachinegun"].Draw));
                Assert.That(stats["BaseWeaponTurretCatapult"].Draw,
                    Is.GreaterThan(stats["BaseWeaponTurretShortbow"].Draw));

                Assert.That(stats["WeaponTurretDamnation"].Draw,
                    Is.GreaterThan(stats["WeaponTurretLargeArtillery"].Draw));
                Assert.That(stats["WeaponTurretLargeArtillery"].Draw,
                    Is.GreaterThan(stats["BaseWeaponTurretCatapult"].Draw));
            });
        });

        await pair.CleanReturnAsync();
    }
}
