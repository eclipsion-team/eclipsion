using System.Collections.Generic;
using Content.Server.Cargo.Systems;
using Content.Server.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Shared.Shipyard.Prototypes;
using Content.Shared._Crescent.DroneControl;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.DeltaV;

[TestFixture]
[TestOf(typeof(ShipyardSystem))]
public sealed class ShipyardTest
{
    // Fog autonomous platforms: hostile encounters carrying salvage loot, only obtainable through
    // admin-spawned fabprint disks. The larger ones are sealed hulls with no exterior airlock.
    private static readonly HashSet<string> FogPlatforms = new()
    {
        "Alpha",
        "Autoscout",
        "Beta",
        "Gamma",
        "Fulgur",
        "Radiant",
        "Brilliant",
    };

    private static readonly HashSet<string> NoDockVessels = new()
    {
        "Fulgur",
        "Radiant",
        "Brilliant",
    };

    // Never sold: Playerbuilt is the station template for player-built ships.
    private static readonly HashSet<string> NotForSale = new(FogPlatforms) { "Playerbuilt" };

    [Test]
    public async Task NoShipyardArbitrage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var entities = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var shipyard = entities.System<ShipyardSystem>();
        var pricing = entities.System<PricingSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var vessel in proto.EnumeratePrototypes<VesselPrototype>())
                {
                    // Unionfall hulls are priced against the war mode's team budget, where nothing is resold.
                    if (NotForSale.Contains(vessel.ID) || vessel.ID.StartsWith("Unionfall"))
                        continue;

                    var shuttle = shipyard.TryCreateShuttle(vessel.Path.ToString());
                    Assert.That(shuttle, Is.Not.Null, $"Failed to spawn shuttle {vessel.ID}!");

                    var value = pricing.AppraiseGrid(shuttle.Value);
                    Assert.That(value, Is.AtMost(vessel.Price), $"Found arbitrage on shuttle {vessel.ID}! Price is {vessel.Price} but value is {value}!");
                    entities.DeleteEntity(shuttle);
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AllShuttlesValid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var entities = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var shipyard = entities.System<ShipyardSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var vessel in proto.EnumeratePrototypes<VesselPrototype>())
                {
                    var shuttle = shipyard.TryCreateShuttle(vessel.Path.ToString());
                    Assert.That(shuttle, Is.Not.Null, $"Failed to spawn shuttle {vessel.ID}!");

                    var console = FindComponent<ShuttleConsoleComponent>(entities, shuttle.Value)
                                  || FindComponent<DroneControlComponent>(entities, shuttle.Value);
                    Assert.That(console, Is.True, $"Shuttle {vessel.ID} had no shuttle console or drone control!");

                    var dock = FindComponent<DockingComponent>(entities, shuttle.Value);
                    if (!NoDockVessels.Contains(vessel.ID))
                        Assert.That(dock, Is.True, $"Shuttle {vessel.ID} had no shuttle dock!");

                    entities.DeleteEntity(shuttle);
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    private bool FindComponent<T>(IEntityManager entities, EntityUid shuttle) where T: Component
    {
        var query = entities.EntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out _, out var xform))
        {
            if (xform.ParentUid != shuttle)
                continue;

            return true;
        }

        return false;
    }
}
