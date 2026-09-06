using System.Linq;
using Content.Shared._Crescent.Poker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class PokerTableTest
{
    [Test]
    public async Task HeldCashPaysForBuyInAndDeletingTheTableRefundsOutstandingBets()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        await server.WaitAssertion(() =>
        {
            System.Collections.Generic.Dictionary<EntityUid, int> CreditStacks()
            {
                var result = new System.Collections.Generic.Dictionary<EntityUid, int>();
                var query = entMan.EntityQueryEnumerator<StackComponent>();
                while (query.MoveNext(out var uid, out var stack))
                {
                    if (stack.StackTypeId == "Credit")
                        result[uid] = stack.Count;
                }
                return result;
            }

            var existingCash = CreditStacks();
            var table = entMan.SpawnEntity("PokerTable", MapCoordinates.Nullspace);
            var body = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var cash = entMan.SpawnEntity("SpaceCash", MapCoordinates.Nullspace);
            var hands = entMan.System<SharedHandsSystem>();
            var stacks = entMan.System<SharedStackSystem>();
            stacks.SetCount(cash, 1000);
            Assert.That(hands.TryPickup(body, cash), Is.True);

            entMan.EventBus.RaiseLocalEvent(table, new PokerJoinMessage { Actor = body });
            var comp = entMan.GetComponent<PokerTableComponent>(table);
            Assert.That(comp.Players, Has.Count.EqualTo(1));
            Assert.That(comp.Players[0].Stack, Is.EqualTo(1000));
            Assert.That(entMan.GetComponent<StackComponent>(cash).Count, Is.Zero);

            comp.Players[0].Stack = 600;
            comp.Players[0].TotalBet = 400;
            comp.Pot = 400;
            entMan.DeleteEntity(table);
            // Cash may land at the player's feet when the active hand is still occupied.
            var remainingCash = CreditStacks();
            Assert.That(remainingCash.Values.Sum() - existingCash.Values.Sum(), Is.EqualTo(1000));
            foreach (var uid in remainingCash.Keys.Where(uid => !existingCash.ContainsKey(uid)))
                entMan.DeleteEntity(uid);
            entMan.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShortAllInRaiseDoesNotReopenBettingForPlayersWhoAlreadyCalled()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        await server.WaitAssertion(() =>
        {
            var table = entMan.SpawnEntity("PokerTable", MapCoordinates.Nullspace);
            var comp = entMan.GetComponent<PokerTableComponent>(table);
            var bodies = Enumerable.Range(0, 3).Select(_ => entMan.SpawnEntity(null, MapCoordinates.Nullspace)).ToArray();
            for (var i = 0; i < bodies.Length; i++)
                comp.Players.Add(new PokerPlayer { Entity = bodies[i], SeatIndex = i, Stack = i == 1 ? 150 : 1000 });

            entMan.EventBus.RaiseLocalEvent(table, new PokerStartGameMessage { Actor = bodies[0] });
            entMan.EventBus.RaiseLocalEvent(table, new PokerCallMessage { Actor = bodies[0] });
            entMan.EventBus.RaiseLocalEvent(table, new PokerRaiseMessage(150) { Actor = bodies[1] });
            Assert.That(comp.CurrentBet, Is.EqualTo(150));
            Assert.That(comp.Players[1].Status, Is.EqualTo(PokerPlayerStatus.AllIn));
            entMan.EventBus.RaiseLocalEvent(table, new PokerCallMessage { Actor = bodies[2] });
            Assert.That(comp.CurrentPlayerIndex, Is.Zero);
            entMan.EventBus.RaiseLocalEvent(table, new PokerRaiseMessage(250) { Actor = bodies[0] });
            Assert.That(comp.CurrentBet, Is.EqualTo(150));
            Assert.That(comp.CurrentPlayerIndex, Is.Zero);
            entMan.EventBus.RaiseLocalEvent(table, new PokerCallMessage { Actor = bodies[0] });
            Assert.That(comp.Phase, Is.EqualTo(PokerRoundPhase.Flop));
            Assert.That(comp.Players.Sum(p => p.Stack) + comp.Pot, Is.EqualTo(2150));

            comp.Players.Clear();
            entMan.DeleteEntity(table);
            foreach (var body in bodies)
                entMan.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FoldKeepsSeatOrderAndPublicStateHidesCards()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        await server.WaitAssertion(() =>
        {
            var table = entMan.SpawnEntity("PokerTable", MapCoordinates.Nullspace);
            var comp = entMan.GetComponent<PokerTableComponent>(table);
            var bodies = Enumerable.Range(0, 4).Select(_ => entMan.SpawnEntity(null, MapCoordinates.Nullspace)).ToArray();
            for (var i = 0; i < bodies.Length; i++)
                comp.Players.Add(new PokerPlayer { Entity = bodies[i], SeatIndex = i, Stack = 1000 });

            entMan.EventBus.RaiseLocalEvent(table, new PokerStartGameMessage { Actor = bodies[0] });
            Assert.That(comp.CurrentPlayerIndex, Is.EqualTo(3));
            entMan.EventBus.RaiseLocalEvent(table, new PokerFoldMessage { Actor = bodies[3] });
            Assert.That(comp.CurrentPlayerIndex, Is.Zero, "The next seat follows the folding player, not an index into a shortened list.");

            var state = (PokerTableBoundUserInterfaceState) entMan.GetComponent<UserInterfaceComponent>(table).States[PokerUiKey.Key];
            Assert.That(state.Players.All(p => p.HoleCards is { Count: 0 }), Is.True);
            Assert.That(comp.Players.All(p => p.HoleCards.Count == 2), Is.True);
            Assert.That(comp.Players.Sum(p => p.Stack) + comp.Pot, Is.EqualTo(4000));

            comp.Players.Clear();
            entMan.DeleteEntity(table);
            foreach (var body in bodies)
                entMan.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AllInRunsOutTheBoardAndPreservesThePot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        await server.WaitAssertion(() =>
        {
            var table = entMan.SpawnEntity("PokerTable", MapCoordinates.Nullspace);
            var comp = entMan.GetComponent<PokerTableComponent>(table);
            var alice = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var bob = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            comp.Players.Add(new PokerPlayer { Entity = alice, SeatIndex = 0, Stack = 100 });
            comp.Players.Add(new PokerPlayer { Entity = bob, SeatIndex = 1, Stack = 100 });

            entMan.EventBus.RaiseLocalEvent(table, new PokerStartGameMessage { Actor = alice });
            Assert.That(comp.Players[0].CurrentBet, Is.EqualTo(50), "The dealer posts the small blind heads-up.");
            Assert.That(comp.CurrentPlayerIndex, Is.Zero);
            entMan.EventBus.RaiseLocalEvent(table, new PokerCallMessage { Actor = alice });
            Assert.That(comp.Phase, Is.EqualTo(PokerRoundPhase.Showdown));
            Assert.That(comp.CommunityCards, Has.Count.EqualTo(5));
            Assert.That(comp.Players.Sum(p => p.Stack), Is.EqualTo(200));
            Assert.That(comp.Pot, Is.Zero);

            comp.Players.Clear();
            entMan.DeleteEntity(table);
            entMan.DeleteEntity(alice);
            entMan.DeleteEntity(bob);
        });
        await pair.CleanReturnAsync();
    }
}
