using System.Collections.Generic;
using System.Linq;
using Content.Server._Crescent.Poker;
using Content.Shared._Crescent.Poker;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class PokerRulesTest
{
    private static List<PokerCard> Cards(params int[] ranks) => ranks
        .Select((rank, index) => new PokerCard((CardSuit) (index % 4), (CardRank) rank)).ToList();

    [TestCase(new[] { 13, 13, 10, 7, 2 }, new[] { 12, 12, 14, 8, 3 })]
    [TestCase(new[] { 10, 10, 10, 3, 2 }, new[] { 9, 9, 9, 14, 13 })]
    [TestCase(new[] { 8, 8, 8, 2, 2 }, new[] { 7, 7, 7, 14, 14 })]
    [TestCase(new[] { 6, 5, 4, 3, 2 }, new[] { 14, 5, 4, 3, 2 })]
    public void MadeHandRanksBeatKickersAndAceLowStraights(int[] stronger, int[] weaker)
    {
        var a = PokerRules.Evaluate(Cards(stronger));
        var b = PokerRules.Evaluate(Cards(weaker));
        Assert.That(a.Rank, Is.EqualTo(b.Rank));
        Assert.That(PokerRules.Compare(a.Cards, b.Cards), Is.GreaterThan(0));
    }

    [Test]
    public void SevenCardHighHandKeepsItsBestFiveCards()
    {
        var hand = PokerRules.Evaluate(Cards(14, 13, 10, 8, 6, 4, 2));
        Assert.That(hand.Rank, Is.EqualTo(HandRank.HighCard));
        Assert.That(hand.Cards.Select(c => (int) c.Rank), Is.EqualTo(new[] { 14, 13, 10, 8, 6 }));
    }

    [Test]
    public void AllInPlayersWinOnlyThePotsTheyContributedTo()
    {
        var table = new PokerTableComponent { CommunityCards = Cards(2, 3, 7, 9, 11) };
        var shortStack = new PokerPlayer { SeatIndex = 0, TotalBet = 100, HoleCards = Cards(14, 14), Status = PokerPlayerStatus.AllIn };
        var middleStack = new PokerPlayer { SeatIndex = 1, TotalBet = 200, HoleCards = Cards(13, 13), Status = PokerPlayerStatus.AllIn };
        var largeStack = new PokerPlayer { SeatIndex = 2, TotalBet = 300, HoleCards = Cards(12, 12), Status = PokerPlayerStatus.Active };
        table.Players.AddRange(new[] { shortStack, middleStack, largeStack });

        var payouts = PokerRules.Payouts(table);
        Assert.Multiple(() =>
        {
            Assert.That(payouts[shortStack], Is.EqualTo(300));
            Assert.That(payouts[middleStack], Is.EqualTo(200));
            Assert.That(payouts[largeStack], Is.EqualTo(100));
            Assert.That(payouts.Values.Sum(), Is.EqualTo(600));
        });
    }

    [Test]
    public void TiedBoardSplitsThePotAndAssignsOddChipAfterTheDealer()
    {
        var table = new PokerTableComponent
        {
            CommunityCards = new[] { 10, 11, 12, 13, 14 }.Select(rank => new PokerCard(CardSuit.Hearts, (CardRank) rank)).ToList(),
        };
        var dealer = new PokerPlayer { SeatIndex = 0, TotalBet = 5, HoleCards = Cards(2, 3), Status = PokerPlayerStatus.Active };
        var next = new PokerPlayer { SeatIndex = 1, TotalBet = 5, HoleCards = Cards(4, 5), Status = PokerPlayerStatus.AllIn };
        var folded = new PokerPlayer { SeatIndex = 2, TotalBet = 5, HoleCards = Cards(6, 7), Status = PokerPlayerStatus.Folded };
        table.Players.AddRange(new[] { dealer, next, folded });

        var payouts = PokerRules.Payouts(table);
        Assert.Multiple(() =>
        {
            Assert.That(payouts[dealer], Is.EqualTo(7));
            Assert.That(payouts[next], Is.EqualTo(8));
            Assert.That(payouts.ContainsKey(folded), Is.False);
            Assert.That(payouts.Values.Sum(), Is.EqualTo(15));
        });
    }
}
