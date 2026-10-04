using Content.Server._Crescent.Economy;
using Content.Server.Cargo.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class ManufacturedPriceCapTest
{
    /// <summary>
    /// Anything a lathe prints, contents included, must not sell for more than the materials an
    /// upgraded lathe spends printing it. Otherwise printing goods for cargo is free money.
    /// </summary>
    [Test]
    public async Task NoLatheRecipeArbitrage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var pricing = entManager.System<PricingSystem>();
        var priceCap = entManager.System<ManufacturedPriceCapSystem>();

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (var recipe in protoManager.EnumeratePrototypes<LatheRecipePrototype>())
                {
                    if (!priceCap.IsCappedRecipe(recipe))
                        continue;

                    var ent = entManager.SpawnEntity(recipe.Result!.Value, testMap.MapCoords);
                    var price = pricing.GetPrice(ent);
                    var cap = priceCap.GetRecipeSaleCap(recipe);

                    Assert.That(price, Is.AtMost(cap + 0.01),
                        $"Lathe recipe {recipe.ID} prints {recipe.Result} from {cap} spesos of materials, but it sells for {price}!");
                    entManager.DeleteEntity(ent);
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The cap scales a printed container, never what is poured into it. Otherwise reagents sold in
    /// a printed beaker would be worth less than the same reagents in any other container.
    /// </summary>
    [Test]
    public async Task ReagentsInPrintedContainersAreNotCapped()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        var entManager = server.ResolveDependency<IEntityManager>();
        var pricing = entManager.System<PricingSystem>();
        var solutions = entManager.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            var beaker = entManager.SpawnEntity("Beaker", testMap.MapCoords);
            var empty = pricing.GetPrice(beaker);

            Assert.That(solutions.TryGetSolution(beaker, "beaker", out var soln, out _), Is.True);
            solutions.TryAddReagent(soln!.Value, "Nutriment", FixedPoint2.New(50), out _);

            var full = pricing.GetPrice(beaker, true, out var movable);
            Assert.That(movable, Is.GreaterThan(0), "Nutriment should be worth something.");
            Assert.That(full - empty, Is.EqualTo(movable).Within(0.01),
                "Reagents in a printed beaker must sell for their full value.");

            entManager.DeleteEntity(beaker);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Printed stack units can be split off or merged into a stack of another prototype of the same
    /// stack type. Every prototype of a printed stack type must keep to the printed unit's cap.
    /// </summary>
    [Test]
    public async Task PrintedStackTypesAreCappedForEveryPrototype()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var pricing = entManager.System<PricingSystem>();
        var priceCap = entManager.System<ManufacturedPriceCapSystem>();
        var economy = entManager.System<EconomyPriceSystem>();

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (var proto in protoManager.EnumeratePrototypes<EntityPrototype>())
                {
                    if (proto.Abstract
                        || proto.Components.ContainsKey(factory.GetComponentName(typeof(MaterialComponent)))
                        || !proto.TryGetComponent<StackComponent>(out var stackProto, factory)
                        || !priceCap.TryGetStackUnitCap(stackProto.StackTypeId, out var unitCap)
                        || economy.TryGetItemOverride(proto.ID, out _))
                        continue;

                    var ent = entManager.SpawnEntity(proto.ID, testMap.MapCoords);
                    var count = entManager.GetComponent<StackComponent>(ent).Count;
                    if (count > 0)
                    {
                        var unit = (pricing.GetPrice(ent, false, out var movable) - movable) / count;
                        Assert.That(unit, Is.AtMost(unitCap + 0.01),
                            $"{proto.ID} sells for {unit} a unit, but a lathe prints {stackProto.StackTypeId} for {unitCap}.");
                    }

                    entManager.DeleteEntity(ent);
                }
            }
        });

        await pair.CleanReturnAsync();
    }
}
