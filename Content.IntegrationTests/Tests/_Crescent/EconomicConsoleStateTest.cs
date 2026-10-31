using Content.Shared.Cargo;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class EconomicConsoleStateTest
{
    // the full price table rode along in the mainframe's UI state and lagged the server
    [Test]
    public async Task PriceListIsFilteredOnTheServer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var ui = server.System<UserInterfaceSystem>();
            var console = entMan.SpawnEntity("EconomicConsole", map.GridCoords);

            EconomicConsoleBuiState Search(string text)
            {
                entMan.EventBus.RaiseLocalEvent(console, new EconomicConsoleSearchMessage(text));
                Assert.That(ui.TryGetUiState<EconomicConsoleBuiState>(console, EconomicConsoleUiKey.Key, out var state), Is.True);
                return state!;
            }

            var all = Search(string.Empty);
            Assert.That(all.MatchingItems, Is.GreaterThan(EconomicConsoleBuiState.MaxItems));
            Assert.That(all.Items, Has.Count.EqualTo(EconomicConsoleBuiState.MaxItems));

            var steel = Search("  SteEl ");
            Assert.That(steel.Items, Is.Not.Empty);
            Assert.That(steel.MatchingItems, Is.LessThan(all.MatchingItems));
            foreach (var item in steel.Items)
            {
                Assert.That(item.ItemId.ToLowerInvariant().Contains("steel") ||
                            item.LocalizedName.ToLowerInvariant().Contains("steel"), Is.True, item.ItemId);
            }

            // same result: the stored state is kept, so nothing goes out again
            Assert.That(Search("steel"), Is.SameAs(steel));
        });

        await pair.CleanReturnAsync();
    }
}
