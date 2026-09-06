using System.Collections.Generic;
using System.Linq;
using Content.Server._Crescent.Taxation;
using Content.Shared._Crescent.Overwatch;
using Content.Shared._Crescent.Payment;
using Content.Shared._Crescent.Taxation;
using Content.Shared.Access.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Guards the faction management consoles: overwatch, payroll, taxation and the treasury vault.
/// </summary>
/// <remarks>
/// These consoles hand out a faction's roster, its live personnel positions and its money, so the two
/// things worth pinning down are that none of them ever ships without an access list, and that a
/// faction's balance stays one number no matter how many stations it owns.
/// </remarks>
public sealed class ManagementConsoleTest
{
    /// <summary>
    /// Components that must never appear on an entity whose <c>AccessReader</c> allows everyone.
    /// </summary>
    /// <remarks>
    /// An <c>AccessReader</c> with an empty access list lets anyone through, so a faction variant that
    /// forgets its list ships wide open rather than failing loudly. The overwatch consoles shipped
    /// exactly that way: no reader at all, on fixed consoles and on portable clipboard variants, which
    /// handed anyone who picked one up the owning faction's roster, live positions, camera feeds and
    /// announcement channel.
    /// </remarks>
    private static readonly string[] GatedComponents =
    {
        "OverwatchConsole",
        "PaymentConsole",
        "FactionTreasuryConsole",
        "TaxationConsole",
    };

    /// <summary>
    /// Factions that deliberately run on overwatch alone, with no money consoles of their own.
    /// </summary>
    private static readonly string[] OverwatchOnlyFactions = { "SRM" };

    private static readonly string[] MoneyComponents =
    {
        "PaymentConsole",
        "FactionTreasuryConsole",
        "TaxationConsole",
    };

    [Test]
    public async Task ManagementConsolesAreAccessGated()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var compFact = server.ResolveDependency<IComponentFactory>();

        var accessName = compFact.GetComponentName(typeof(AccessReaderComponent));
        var offenders = new List<string>();

        await server.WaitAssertion(() =>
        {
            foreach (var proto in protoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract)
                    continue;

                if (!GatedComponents.Any(c => proto.Components.ContainsKey(c)))
                    continue;

                if (!proto.Components.TryGetComponent(accessName, out var raw)
                    || raw is not AccessReaderComponent reader
                    || reader.AccessLists.Count == 0)
                {
                    offenders.Add(proto.ID);
                }
            }

            Assert.That(offenders, Is.Empty,
                $"Management consoles with no access list (an empty AccessReader admits everyone): {string.Join(", ", offenders)}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OverwatchOnlyFactionsHaveNoMoneyConsoles()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var offenders = new List<string>();

        await server.WaitAssertion(() =>
        {
            foreach (var proto in protoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract)
                    continue;

                if (!MoneyComponents.Any(c => proto.Components.ContainsKey(c)))
                    continue;

                if (OverwatchOnlyFactions.Any(f => proto.ID.Contains(f, System.StringComparison.Ordinal)))
                    offenders.Add(proto.ID);
            }

            Assert.That(offenders, Is.Empty,
                $"SRM has no payroll, taxation or treasury consoles: {string.Join(", ", offenders)}");
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("ComputerPaymentTAP")]
    [TestCase("ComputerFactionTreasuryTAP")]
    public async Task PactMoneyConsolesRequireAllThreeFamilyCredentials(string prototypeId)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var prototype = protoMan.Index<EntityPrototype>(prototypeId);
            var reader = (AccessReaderComponent) prototype.Components["AccessReader"].Component;
            Assert.That(reader.AccessLists, Has.Count.EqualTo(1));
            Assert.That(reader.AccessLists.Single().Select(id => id.ToString()),
                Is.EquivalentTo(new[] { "Arabet", "Alseik", "Thukker" }));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A faction's balance is one number, wherever it is read from.
    /// </summary>
    /// <remarks>
    /// It used to be mirrored onto each station's <c>StationTradeMarketComponent</c>, and a faction owns
    /// several stations at once — its home station plus every shipyard-bought hull, which becomes its
    /// own station and inherits the buyer's IFF faction. Each copy loaded the full balance and then
    /// raced the others writing back, so spending from a ship was refunded by the next sale on the home
    /// station, and withdrawals at the vault were undone the same way.
    /// </remarks>
    [Test]
    public async Task FactionTreasuryIsASingleBalance()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var treasury = server.System<FactionTreasurySystem>();
        const string faction = "TestTreasuryFaction";

        await server.WaitAssertion(() =>
        {
            treasury.Forget(faction);

            Assert.Multiple(() =>
            {
                Assert.That(treasury.Add(faction, 1000), Is.EqualTo(1000), "Add should return the new balance.");
                Assert.That(treasury.Get(faction), Is.EqualTo(1000));

                // Withdrawals never go negative and report what they actually took.
                Assert.That(treasury.TryWithdraw(faction, 400), Is.EqualTo(400));
                Assert.That(treasury.Get(faction), Is.EqualTo(600));
                Assert.That(treasury.TryWithdraw(faction, 10_000), Is.EqualTo(600), "Should be clamped to the balance.");
                Assert.That(treasury.Get(faction), Is.EqualTo(0));
                Assert.That(treasury.TryWithdraw(faction, 100), Is.EqualTo(0), "An empty vault yields nothing.");

                // A balance can never be driven below zero, even by an explicit Set.
                treasury.Set(faction, -500);
                Assert.That(treasury.Get(faction), Is.EqualTo(0));
            });

            treasury.Forget(faction);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LargeTreasuryBalancesAndWithdrawalLedgersDoNotOverflow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var treasury = server.System<FactionTreasurySystem>();
        const string faction = "TestLargeTreasury";
        var user = new NetUserId(System.Guid.NewGuid());

        await server.WaitAssertion(() =>
        {
            treasury.Set(faction, int.MaxValue - 1);
            Assert.That(treasury.Add(faction, 10), Is.EqualTo(int.MaxValue));
            Assert.That(treasury.GetRemainingWithdrawal(faction, user, float.NaN), Is.Zero);
            Assert.That(treasury.GetRemainingWithdrawal(faction, user, 1f), Is.EqualTo(int.MaxValue));
            Assert.That(treasury.TryWithdrawCapped(faction, user, int.MaxValue, 1f), Is.EqualTo(int.MaxValue));

            treasury.Add(faction, int.MaxValue);
            Assert.That(treasury.GetRemainingWithdrawal(faction, user, 0.5f), Is.Zero);
            Assert.That(treasury.TryWithdrawCapped(faction, user, int.MaxValue, 1f), Is.EqualTo(int.MaxValue));
            Assert.That(treasury.GetWithdrawnThisRound(faction, user), Is.EqualTo(2L * int.MaxValue));

            treasury.RefundCapped(faction, user, int.MaxValue);
            Assert.That(treasury.Get(faction), Is.EqualTo(int.MaxValue));
            Assert.That(treasury.GetWithdrawnThisRound(faction, user), Is.EqualTo((long) int.MaxValue));
            Assert.That(treasury.GetRemainingWithdrawal(faction, user, 0.5f), Is.Zero);
            treasury.Forget(faction);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The per-person withdrawal cap holds across repeat visits, and a failed payout gives the budget
    /// back rather than quietly costing the operator their share.
    /// </summary>
    [Test]
    public async Task WithdrawalCapIsPerPlayerAndRefundable()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var treasury = server.System<FactionTreasurySystem>();
        const string faction = "TestCapFaction";
        var alice = new NetUserId(System.Guid.NewGuid());
        var bob = new NetUserId(System.Guid.NewGuid());

        await server.WaitAssertion(() =>
        {
            treasury.Set(faction, 1000);

            Assert.Multiple(() =>
            {
                // Half of 1000. Measured against the vault as it stood before Alice started, so coming
                // back for a second helping cannot beat the cap.
                Assert.That(treasury.GetRemainingWithdrawal(faction, alice, 0.5f), Is.EqualTo(500));
                Assert.That(treasury.TryWithdrawCapped(faction, alice, 300, 0.5f), Is.EqualTo(300));
                Assert.That(treasury.TryWithdrawCapped(faction, alice, 300, 0.5f), Is.EqualTo(200),
                    "Second visit should be trimmed to what is left of Alice's share.");
                Assert.That(treasury.TryWithdrawCapped(faction, alice, 100, 0.5f), Is.EqualTo(0),
                    "Alice has spent her whole share.");

                Assert.That(treasury.Get(faction), Is.EqualTo(500));

                // Bob's budget is his own and is measured against the vault he finds.
                Assert.That(treasury.TryWithdrawCapped(faction, bob, 1000, 0.5f), Is.EqualTo(250));
                Assert.That(treasury.Get(faction), Is.EqualTo(250));

                // A payout that could not be delivered returns both the money and the allowance.
                treasury.RefundCapped(faction, bob, 250);
                Assert.That(treasury.Get(faction), Is.EqualTo(500));
                Assert.That(treasury.GetWithdrawnThisRound(faction, bob), Is.EqualTo(0),
                    "Refund should restore the operator's spent allowance.");
            });

            treasury.Forget(faction);
        });

        await pair.CleanReturnAsync();
    }
}
