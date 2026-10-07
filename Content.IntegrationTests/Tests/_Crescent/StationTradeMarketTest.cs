using Content.Server.Cargo.Systems;
using Content.Server.Crescent.Dispenser;
using Content.Shared.Crescent.Dispenser;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class StationTradeMarketTest
{
    [Test]
    public void CombinedMarketEffectsCannotBreakPayoutFloor()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DispenserSystem.CalculateTradePayoutMultiplier(0.5f, 0.5f), Is.EqualTo(0.5f));
            Assert.That(DispenserSystem.CalculateTradePayoutMultiplier(0.9f, 0.95f),
                Is.EqualTo(0.855f).Within(0.0001f));
            Assert.That(DispenserSystem.CalculateTradePayoutMultiplier(float.NaN, 1f), Is.EqualTo(0.5f));
        }
    }

    [Test]
    public async Task SaturationDevaluesGraduallyAndHasAFloor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        var entManager = server.ResolveDependency<IEntityManager>();
        var marketSystem = entManager.System<StationTradeMarketSystem>();

        await server.WaitAssertion(() =>
        {
            var station = entManager.SpawnEntity(null, testMap.MapCoords);
            var market = entManager.AddComponent<StationTradeMarketComponent>(station);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(market.PriceDropPerSale, Is.EqualTo(0.01f));
                Assert.That(market.MinMultiplier, Is.EqualTo(0.5f));
                Assert.That(market.RecoveryRatePerSecond, Is.EqualTo(1f / 30f));
                Assert.That(marketSystem.GetPriceMultiplier(station, "TestGood"), Is.EqualTo(1f));
            }

            for (var i = 0; i < 10; i++)
                marketSystem.RecordSale(station, "TestGood");

            Assert.That(marketSystem.GetPriceMultiplier(station, "TestGood"),
                Is.EqualTo(0.9f).Within(0.0001f),
                "Ten rapid sales should reduce the local price by only 10%.");

            for (var i = 0; i < 100; i++)
                marketSystem.RecordSale(station, "TestGood");

            Assert.That(marketSystem.GetPriceMultiplier(station, "TestGood"),
                Is.EqualTo(0.5f),
                "Market saturation must not reduce an item's local price below 50%.");

            entManager.DeleteEntity(station);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Old Gliess has a dispensary right beside its chute; the chute must not pay more for a good
    /// than that dispensary sells it for, while chutes on other grids keep their normal payout.
    /// </summary>
    [Test]
    public async Task ChuteIsCappedByVendorOnSameGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var dispenserSystem = entManager.System<DispenserSystem>();
        var pricing = entManager.System<PricingSystem>();

        await server.WaitAssertion(() =>
        {
            var otherGrid = mapSystem.CreateGridEntity(testMap.MapId);
            // Offset so the second grid's tile does not overlap the test grid's.
            var otherCoords = new EntityCoordinates(otherGrid.Owner, 50, 0);
            var tile = mapSystem.GetTileRef(testMap.Grid.Owner, testMap.Grid.Comp, testMap.GridCoords).Tile;
            mapSystem.SetTile(otherGrid.Owner, otherGrid.Comp, otherCoords, tile);

            var chute = entManager.SpawnEntity("GliessSantoCargoChute", testMap.GridCoords);
            entManager.SpawnEntity("GliessSantoCargoDispenser", testMap.GridCoords);
            var remoteChute = entManager.SpawnEntity("GliessSantoCargoChute", otherCoords);

            var antibiotics = protoManager.Index<EntityPrototype>("TradeGoodAntibiotics");
            var plasma = protoManager.Index<EntityPrototype>("TradeGoodPlasma");
            var vendPrice = (int) pricing.GetEstimatedPrice(antibiotics);
            var chutePayout = entManager.GetComponent<DispenserComponent>(chute).DynamicInventory[antibiotics.ID];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(vendPrice, Is.LessThan(chutePayout),
                    "Precondition: the chute pays more than the dispensary charges.");
                Assert.That(dispenserSystem.GetLocalVendPrice(chute, null, antibiotics), Is.EqualTo(vendPrice),
                    "A good sold on the same grid must be capped at its vend price.");
                Assert.That(dispenserSystem.GetLocalVendPrice(chute, null, plasma), Is.Null,
                    "Goods the local dispensary does not sell must not be capped.");
                Assert.That(dispenserSystem.GetLocalVendPrice(remoteChute, null, antibiotics), Is.Null,
                    "A vendor on another grid must not cap this chute.");
            }
        });

        await pair.CleanReturnAsync();
    }
}
