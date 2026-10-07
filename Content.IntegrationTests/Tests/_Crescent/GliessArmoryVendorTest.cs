using System.Collections.Generic;
using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class GliessArmoryVendorTest
{
    /// <summary>
    /// Gliess Santo's civilian armory vendors, and the vendors they are a cheaper copy of.
    /// </summary>
    private static readonly (string Gliess, string Original)[] Vendors =
    {
        ("ShinoharaBudgetVendorGliess", "ShinoharaBudgetVendor"),
        ("TaypaniSurplusVendorGliess", "TaypaniSurplusVendor"),
    };

    /// <summary>
    /// Gliess stocks the same goods in the same amounts as the original vendor. Its weapons and ammo
    /// are half-price storefront variants of the original's, everything else is the original's own.
    /// </summary>
    [Test]
    public async Task WeaponsAndAmmoCostHalf()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var pricing = entManager.System<PricingSystem>();

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (var (gliessId, originalId) in Vendors)
                {
                    var gliess = GetVendor(protoManager, factory, gliessId);
                    var original = GetVendor(protoManager, factory, originalId);
                    var gliessPack = protoManager.Index(gliess.PackPrototypeId).StartingInventory;
                    var originalPack = protoManager.Index(original.PackPrototypeId).StartingInventory;

                    Assert.That(gliess.GlobalPriceMod, Is.EqualTo(original.GlobalPriceMod));
                    Assert.That(gliessPack, Has.Count.EqualTo(originalPack.Count),
                        $"{gliessId} must stock everything {originalId} does.");

                    var halved = 0;
                    foreach (var (item, amount) in gliessPack)
                    {
                        if (GetCounterpart(protoManager, originalPack, item) is not { } counterpart)
                        {
                            Assert.Fail($"{gliessId} sells {item}, which is neither in {originalId} nor a variant of an item there.");
                            continue;
                        }

                        Assert.That(amount, Is.EqualTo(originalPack[counterpart]), $"{gliessId} stocks a different amount of {item}.");
                        if (counterpart == item)
                            continue;

                        Assert.That(pricing.GetEstimatedPrice(protoManager.Index(item)),
                            Is.EqualTo(pricing.GetEstimatedPrice(protoManager.Index(counterpart)) / 2).Within(0.01),
                            $"{item} must cost half of {counterpart}.");
                        halved++;
                    }

                    Assert.That(halved, Is.Positive, $"{gliessId} sells nothing at half price.");
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The discount comes off what the goods are worth as well as what they cost, so nothing bought
    /// from Gliess sells on for more over its price than the same thing bought from the original vendor.
    /// </summary>
    [Test]
    public async Task DiscountOpensNoResaleMargin()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var pricing = entManager.System<PricingSystem>();

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (var (gliessId, originalId) in Vendors)
                {
                    var gliess = GetVendor(protoManager, factory, gliessId);
                    var original = GetVendor(protoManager, factory, originalId);
                    var originalPack = protoManager.Index(original.PackPrototypeId).StartingInventory;

                    foreach (var item in protoManager.Index(gliess.PackPrototypeId).StartingInventory.Keys)
                    {
                        if (GetCounterpart(protoManager, originalPack, item) is not { } counterpart)
                            continue;

                        var margin = GetResaleMargin(item, gliess);
                        var originalMargin = GetResaleMargin(counterpart, original);
                        Assert.That(margin, Is.AtMost(originalMargin + 0.01),
                            $"{item} from {gliessId} sells on for {margin} over its price, {counterpart} from {originalId} for only {originalMargin}!");
                    }
                }
            }

            double GetResaleMargin(EntProtoId item, VendingMachineComponent vendor)
            {
                var ent = entManager.SpawnEntity(item, testMap.MapCoords);
                var margin = pricing.GetPrice(ent) - pricing.GetEstimatedPrice(protoManager.Index(item)) * vendor.GlobalPriceMod;
                entManager.DeleteEntity(ent);
                return margin;
            }
        });

        await pair.CleanReturnAsync();
    }

    private static VendingMachineComponent GetVendor(IPrototypeManager protoManager, IComponentFactory factory, string id)
    {
        Assert.That(protoManager.Index<EntityPrototype>(id).TryGetComponent<VendingMachineComponent>(out var vendor, factory));
        return vendor!;
    }

    /// <summary>
    /// The original vendor's entry a Gliess entry stands for: the same item, or the one it is a variant of.
    /// </summary>
    private static EntProtoId? GetCounterpart(IPrototypeManager protoManager, Dictionary<EntProtoId, uint> originalPack, EntProtoId item)
    {
        if (originalPack.ContainsKey(item))
            return item;

        if (protoManager.Index(item).Parents?.SingleOrDefault() is { } parent && originalPack.ContainsKey(parent))
            return parent;

        return null;
    }
}
