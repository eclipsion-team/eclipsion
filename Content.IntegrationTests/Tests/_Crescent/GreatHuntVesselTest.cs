#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Server.Maps;
using Content.Server.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Server._Crescent.GreatHunt;
using Content.Shared.Shipyard.Prototypes;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shipyard.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// The Great Hunt yards only sell the hunt versions of their hulls, and those leave the yard cloaked with a cloak that
/// never overheats.
/// </summary>
[TestFixture]
[TestOf(typeof(GreatHuntCloakedVesselSystem))]
public sealed class GreatHuntVesselTest
{
    private static readonly (string Yard, string[] Vessels)[] Yards =
    [
        ("RatSRMGreatHuntMothershipComputer", ["SpiderGreatHunt"]),
        ("RatTAPGreatHuntMothershipComputer", ["FadeemGreatHunt", "LuciferGreatHunt"]),
    ];

    [Test]
    public async Task HuntYardsListOnlyHuntHulls()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            foreach (var (yard, vessels) in Yards)
            {
                Assert.That(proto.Index<EntityPrototype>(yard).TryGetComponent<ShipyardListingComponent>(out var listing, factory),
                    Is.True, $"{yard} has no shipyard listing.");
                Assert.That(listing!.Shuttles.Select(s => s.ToString()), Is.EquivalentTo(vessels));
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HuntHullsLaunchWithUnlimitedCloak()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var shipyard = entMan.System<ShipyardSystem>();
        var station = entMan.System<StationSystem>();

        var grids = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            foreach (var id in Yards.SelectMany(y => y.Vessels))
            {
                var vessel = proto.Index<VesselPrototype>(id);
                var shuttle = shipyard.TryCreateShuttle(vessel.Path.ToString());
                Assert.That(shuttle, Is.Not.Null, $"Failed to spawn shuttle {id}.");

                var gameMap = proto.Index<GameMapPrototype>(id);
                station.InitializeNewStation(gameMap.Stations[id], [shuttle!.Value.Owner]);
                grids.Add(shuttle.Value.Owner);
            }
        });

        // Long past the point where an ordinary cloak would have burned out.
        await pair.RunTicksSync(timing.TickRate * 3);

        await server.WaitAssertion(() =>
        {
            foreach (var grid in grids)
            {
                var name = entMan.ToPrettyString(grid);
                Assert.That(entMan.TryGetComponent<IFFComponent>(grid, out var iff) && (iff.Flags & IFFFlags.Hide) != 0,
                    Is.True, $"{name} did not launch cloaked.");

                var consoles = entMan.EntityQuery<IFFConsoleComponent, TransformComponent>()
                    .Where(c => c.Item2.GridUid == grid)
                    .Select(c => c.Item1)
                    .ToList();

                Assert.That(consoles, Is.Not.Empty, $"{name} has no IFF console.");
                foreach (var console in consoles)
                {
                    Assert.That(console.UnlimitedCloak, Is.True, $"{name}'s IFF console is not unlimited.");
                    Assert.That(console.CurrentHeat, Is.Zero, $"{name}'s cloak is heating up.");
                }
            }
        });

        await pair.CleanReturnAsync();
    }
}
