using System.Linq;
using Content.Shared._Crescent.Poker;

namespace Content.Server._Crescent.Poker;

/// <summary>Hand comparison and pot splitting, independent of table UI and cash entities.</summary>
public static class PokerRules
{
    public static (HandRank Rank, List<PokerCard> Cards) Evaluate(List<PokerCard> cards)
    {
        var best = (Rank: HandRank.HighCard, Cards: new List<PokerCard>());
        foreach (var five in Combinations(cards, 5))
        {
            var hand = EvaluateFive(five);
            if (hand.Rank > best.Rank || hand.Rank == best.Rank && Compare(hand.Cards, best.Cards) > 0)
                best = hand;
        }
        return best;
    }

    private static (HandRank Rank, List<PokerCard> Cards) EvaluateFive(List<PokerCard> cards)
    {
        var sorted = cards.OrderByDescending(c => c.Rank).ToList();
        var ranks = sorted.Select(c => (int) c.Rank).ToArray();
        var flush = sorted.All(c => c.Suit == sorted[0].Suit);
        var wheel = ranks.SequenceEqual(new[] { 14, 5, 4, 3, 2 });
        var straight = wheel || ranks.Distinct().Count() == 5 && ranks[0] - ranks[4] == 4;
        if (wheel)
        {
            // An ace is low only in a five-high straight.
            sorted.Add(sorted[0]);
            sorted.RemoveAt(0);
        }

        if (straight)
            return (flush ? (wheel || ranks[0] != 14 ? HandRank.StraightFlush : HandRank.RoyalFlush) : HandRank.Straight, sorted);
        if (flush)
            return (HandRank.Flush, sorted);

        var groups = sorted.GroupBy(c => c.Rank)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToList();
        // Compare made groups before kickers: a pair of kings beats a pair of queens with an ace.
        var ordered = groups.SelectMany(g => g).ToList();
        var rank = groups[0].Count() switch
        {
            4 => HandRank.FourOfAKind,
            3 => groups[1].Count() == 2 ? HandRank.FullHouse : HandRank.ThreeOfAKind,
            2 => groups[1].Count() == 2 ? HandRank.TwoPair : HandRank.OnePair,
            _ => HandRank.HighCard,
        };
        return (rank, ordered);
    }

    public static int Compare(List<PokerCard> a, List<PokerCard> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var comparison = a[i].Rank.CompareTo(b[i].Rank);
            if (comparison != 0)
                return comparison;
        }
        return a.Count.CompareTo(b.Count);
    }

    public static Dictionary<PokerPlayer, int> Payouts(PokerTableComponent table)
    {
        var payouts = new Dictionary<PokerPlayer, int>();
        var contenders = table.Players.Where(p => p.Status is PokerPlayerStatus.Active or PokerPlayerStatus.AllIn).ToList();
        if (contenders.Count == 0)
        {
            foreach (var player in table.Players)
                payouts[player] = player.TotalBet;
            return payouts;
        }

        var hands = contenders.ToDictionary(p => p, p => Evaluate(p.HoleCards.Concat(table.CommunityCards).ToList()));
        var previous = 0;
        foreach (var level in table.Players.Select(p => p.TotalBet).Where(b => b > 0).Distinct().Order())
        {
            var contributors = table.Players.Where(p => p.TotalBet >= level).ToList();
            var amount = (level - previous) * contributors.Count;
            previous = level;
            var eligible = contenders.Where(p => p.TotalBet >= level).ToList();
            if (eligible.Count == 0)
            {
                // Return unmatched chips above the last contested pot.
                foreach (var player in contributors)
                    payouts[player] = payouts.GetValueOrDefault(player) + amount / contributors.Count;
                continue;
            }

            var best = eligible[0];
            foreach (var player in eligible.Skip(1))
            {
                if (hands[player].Rank > hands[best].Rank ||
                    hands[player].Rank == hands[best].Rank && Compare(hands[player].Cards, hands[best].Cards) > 0)
                    best = player;
            }

            var winners = eligible.Where(p => hands[p].Rank == hands[best].Rank && Compare(hands[p].Cards, hands[best].Cards) == 0)
                .OrderBy(p => (p.SeatIndex - table.DealerIndex - 1 + table.Players.Count) % table.Players.Count).ToList();
            var remainder = amount % winners.Count;
            foreach (var winner in winners)
                payouts[winner] = payouts.GetValueOrDefault(winner) + amount / winners.Count + (remainder-- > 0 ? 1 : 0);
        }
        return payouts;
    }

    private static IEnumerable<List<PokerCard>> Combinations(List<PokerCard> cards, int count)
    {
        if (count == 0)
        {
            yield return new List<PokerCard>();
            yield break;
        }
        for (var i = 0; i <= cards.Count - count; i++)
        {
            foreach (var rest in Combinations(cards.Skip(i + 1).ToList(), count - 1))
            {
                rest.Insert(0, cards[i]);
                yield return rest;
            }
        }
    }
}
