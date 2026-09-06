using System.Linq;
using Content.Shared._Crescent.Poker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Poker;

public sealed class PokerTableSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PokerTableComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<PokerTableComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<PokerTableComponent, PokerJoinMessage>(OnJoin);
        SubscribeLocalEvent<PokerTableComponent, PokerLeaveMessage>(OnLeave);
        SubscribeLocalEvent<PokerTableComponent, PokerFoldMessage>(OnFold);
        SubscribeLocalEvent<PokerTableComponent, PokerCheckMessage>(OnCheck);
        SubscribeLocalEvent<PokerTableComponent, PokerCallMessage>(OnCall);
        SubscribeLocalEvent<PokerTableComponent, PokerBetMessage>(OnBet);
        SubscribeLocalEvent<PokerTableComponent, PokerRaiseMessage>(OnRaise);
        SubscribeLocalEvent<PokerTableComponent, PokerStartGameMessage>(OnStartGame);
        SubscribeLocalEvent<PokerTableComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnUiOpened(EntityUid uid, PokerTableComponent comp, BoundUIOpenedEvent args)
    {
        SendState(uid, comp);
    }

    private void OnUiClosed(EntityUid uid, PokerTableComponent comp, BoundUIClosedEvent args)
    {
        var player = comp.Players.FirstOrDefault(p => p.Entity == args.Actor);
        if (player != null)
            RemovePlayer(uid, comp, player);
    }

    private void OnJoin(EntityUid uid, PokerTableComponent comp, PokerJoinMessage msg)
    {
        if (comp.Players.Any(p => p.Entity == msg.Actor))
            return;
        if (comp.Players.Count >= comp.MaxPlayers)
            return;
        if (comp.Phase != PokerRoundPhase.Waiting)
            return;

        var balance = ScanPlayerCash(msg.Actor);
        if (balance <= 0 || comp.StartingBuyIn <= 0)
            return;

        var buyIn = Math.Min(balance, comp.StartingBuyIn);
        if (comp.Players.Sum(p => (long) p.Stack + p.TotalBet) + buyIn > int.MaxValue)
            return;
        TakeCash(msg.Actor, buyIn);

        var name = Name(msg.Actor);
        var player = new PokerPlayer
        {
            Entity = msg.Actor,
            Name = name,
            Stack = buyIn,
            SeatIndex = comp.Players.Count
        };
        comp.Players.Add(player);
        SendState(uid, comp);
    }

    private void OnLeave(EntityUid uid, PokerTableComponent comp, PokerLeaveMessage msg)
    {
        var player = comp.Players.FirstOrDefault(p => p.Entity == msg.Actor);
        if (player != null)
            RemovePlayer(uid, comp, player);
    }

    private void RemovePlayer(EntityUid uid, PokerTableComponent comp, PokerPlayer player)
    {
        if (player.HasLeft)
            return;

        // An all-in player has no decisions left to make, so losing the window must not cost them the pot they
        // already funded - only Active players are folded out, since they are the ones who still owe an action.
        // Payouts still reach them: FinishRound cashes out anyone who left with a stack.
        var midHand = comp.Phase is not (PokerRoundPhase.Waiting or PokerRoundPhase.Showdown);
        var stillContending = midHand && player.Status == PokerPlayerStatus.AllIn;

        if (player.Stack > 0)
            GiveCash(player.Entity, player.Stack, uid);
        player.Stack = 0;
        player.HasLeft = true;
        if (!stillContending)
            player.Status = PokerPlayerStatus.Folded;

        if (!midHand)
        {
            comp.Players.Remove(player);
            Reseat(comp);
        }
        else
        {
            // Keep the contribution in the hand, even after its owner leaves.
            AdvanceTurn(uid, comp, advancePlayer: comp.Players.IndexOf(player) == comp.CurrentPlayerIndex);
        }
        SendState(uid, comp);
    }

    private void OnShutdown(EntityUid uid, PokerTableComponent comp, ref ComponentShutdown args)
    {
        foreach (var player in comp.Players)
        {
            GiveCash(player.Entity, player.Stack + player.TotalBet, uid);
        }
        comp.Players.Clear();
        comp.Pot = 0;
    }

    private static void Reseat(PokerTableComponent comp)
    {
        for (var i = 0; i < comp.Players.Count; i++)
            comp.Players[i].SeatIndex = i;
        comp.DealerIndex %= Math.Max(1, comp.Players.Count);
    }

    private void OnStartGame(EntityUid uid, PokerTableComponent comp, PokerStartGameMessage msg)
    {
        if (comp.Phase != PokerRoundPhase.Waiting)
            return;
        if (!comp.Players.Any(p => p.Entity == msg.Actor) || comp.Players.Count(p => p.Stack > 0) < comp.MinPlayers)
            return;

        StartNewRound(uid, comp);
    }

    private void StartNewRound(EntityUid uid, PokerTableComponent comp)
    {
        comp.Players.RemoveAll(p => p.HasLeft || p.Stack <= 0);
        Reseat(comp);
        comp.Deck = BuildAndShuffleDeck();
        comp.CommunityCards.Clear();
        comp.Pot = 0;
        comp.CurrentBet = 0;
        comp.LastRaiseAmount = comp.BigBlind;
        comp.Phase = PokerRoundPhase.PreFlop;
        comp.RoundNumber++;

        foreach (var p in comp.Players)
        {
            p.HoleCards.Clear();
            p.CurrentBet = 0;
            p.TotalBet = 0;
            p.HasActed = false;
            p.Status = p.Stack > 0 ? PokerPlayerStatus.Active : PokerPlayerStatus.Folded;
        }

        // Everyone left in comp.Players is funded - the RemoveAll at the top of this method saw to that - so the
        // seats are indexed directly. Deriving the blinds from a filtered copy and then applying the result
        // modulo comp.Players.Count only lined up because the two lists happened to be identical.
        if (comp.Players.Count < 2)
        {
            EndRound(uid, comp);
            return;
        }

        comp.DealerIndex %= comp.Players.Count;

        var sbIndex = comp.Players.Count == 2 ? comp.DealerIndex : (comp.DealerIndex + 1) % comp.Players.Count;
        var bbIndex = (sbIndex + 1) % comp.Players.Count;

        PostBlind(comp, comp.Players[sbIndex], comp.SmallBlind);
        PostBlind(comp, comp.Players[bbIndex], comp.BigBlind);

        comp.CurrentBet = comp.Players.Max(p => p.CurrentBet);
        comp.CurrentPlayerIndex = (bbIndex + 1) % comp.Players.Count;

        foreach (var p in comp.Players)
        {
            p.HoleCards.Add(DealCard(comp));
            p.HoleCards.Add(DealCard(comp));
        }

        AdvanceTurn(uid, comp, advancePlayer: false);
    }

    private void PostBlind(PokerTableComponent comp, PokerPlayer player, int amount)
    {
        var actual = Math.Min(player.Stack, amount);
        player.Stack -= actual;
        player.CurrentBet += actual;
        player.TotalBet += actual;
        comp.Pot += actual;
        if (player.Stack == 0)
            player.Status = PokerPlayerStatus.AllIn;
    }

    private void OnFold(EntityUid uid, PokerTableComponent comp, PokerFoldMessage msg)
    {
        if (!ValidateTurn(comp, msg.Actor, out var player))
            return;

        player.Status = PokerPlayerStatus.Folded;
        player.HasActed = true;
        AdvanceTurn(uid, comp);
    }

    private void OnCheck(EntityUid uid, PokerTableComponent comp, PokerCheckMessage msg)
    {
        if (!ValidateTurn(comp, msg.Actor, out var player))
            return;

        if (comp.CurrentBet > player.CurrentBet)
            return;

        player.HasActed = true;
        AdvanceTurn(uid, comp);
    }

    private void OnCall(EntityUid uid, PokerTableComponent comp, PokerCallMessage msg)
    {
        if (!ValidateTurn(comp, msg.Actor, out var player))
            return;

        var callAmount = comp.CurrentBet - player.CurrentBet;
        if (callAmount <= 0)
            return;
        var actual = Math.Min(player.Stack, callAmount);

        player.Stack -= actual;
        player.CurrentBet += actual;
        player.TotalBet += actual;
        comp.Pot += actual;

        if (player.Stack == 0)
            player.Status = PokerPlayerStatus.AllIn;

        player.HasActed = true;
        AdvanceTurn(uid, comp);
    }

    private void OnBet(EntityUid uid, PokerTableComponent comp, PokerBetMessage msg)
    {
        if (!ValidateTurn(comp, msg.Actor, out var player))
            return;
        if (comp.CurrentBet > 0)
            return;
        if (msg.Amount <= 0 || msg.Amount > player.Stack ||
            msg.Amount < comp.BigBlind && msg.Amount != player.Stack)
            return;

        var needed = msg.Amount - player.CurrentBet;
        if (needed > player.Stack)
            return;

        player.Stack -= needed;
        player.CurrentBet += needed;
        player.TotalBet += needed;
        comp.Pot += needed;
        comp.CurrentBet = player.CurrentBet;
        comp.LastRaiseAmount = Math.Max(comp.BigBlind, msg.Amount);

        if (player.Stack == 0)
            player.Status = PokerPlayerStatus.AllIn;

        foreach (var p in comp.Players)
            if (p != player && p.Status == PokerPlayerStatus.Active)
                p.HasActed = false;

        player.HasActed = true;
        AdvanceTurn(uid, comp);
    }

    private void OnRaise(EntityUid uid, PokerTableComponent comp, PokerRaiseMessage msg)
    {
        if (!ValidateTurn(comp, msg.Actor, out var player))
            return;

        var maximum = (long) player.Stack + player.CurrentBet;
        if (player.HasActed || msg.Amount <= comp.CurrentBet || msg.Amount > maximum)
            return;

        var raise = msg.Amount - comp.CurrentBet;
        var fullRaise = raise >= comp.LastRaiseAmount;
        if (!fullRaise && msg.Amount != maximum)
            return;

        var needed = msg.Amount - player.CurrentBet;
        if (needed <= 0 || needed > player.Stack)
            return;

        if (fullRaise)
            comp.LastRaiseAmount = raise;
        comp.CurrentBet = msg.Amount;
        player.Stack -= needed;
        player.CurrentBet = msg.Amount;
        player.TotalBet += needed;
        comp.Pot += needed;
        if (player.Stack == 0)
            player.Status = PokerPlayerStatus.AllIn;

        if (fullRaise)
        {
            foreach (var other in comp.Players.Where(p => p != player && p.Status == PokerPlayerStatus.Active))
                other.HasActed = false;
        }

        player.HasActed = true;
        AdvanceTurn(uid, comp);
    }

    private bool ValidateTurn(PokerTableComponent comp, EntityUid actor, out PokerPlayer player)
    {
        player = null!;
        if (comp.Phase == PokerRoundPhase.Waiting || comp.Phase == PokerRoundPhase.Showdown)
            return false;

        if (comp.CurrentPlayerIndex < 0 || comp.CurrentPlayerIndex >= comp.Players.Count)
            return false;

        // Whether anyone at all is Active is not worth a filtered copy on every fold, check, call, bet and
        // raise: the seat on turn being Active already implies it.
        var current = comp.Players[comp.CurrentPlayerIndex];
        if (current.Status != PokerPlayerStatus.Active)
            return false;
        if (current.Entity != actor)
            return false;

        player = current;
        return true;
    }

    private void AdvanceTurn(EntityUid uid, PokerTableComponent comp, bool advancePlayer = true)
    {
        var contenders = comp.Players.Count(p => p.Status is PokerPlayerStatus.Active or PokerPlayerStatus.AllIn);
        if (contenders <= 1)
        {
            EndRound(uid, comp);
            return;
        }

        var active = comp.Players.Where(p => p.Status == PokerPlayerStatus.Active).ToList();
        if (active.Count == 0 || active.Count == 1 && active[0].CurrentBet >= comp.CurrentBet)
        {
            while (comp.CommunityCards.Count < 5)
                comp.CommunityCards.Add(DealCard(comp));
            DoShowdown(uid, comp);
            return;
        }
        if (active.All(p => p.HasActed && p.CurrentBet == comp.CurrentBet))
        {
            AdvancePhase(uid, comp);
            return;
        }

        for (var offset = advancePlayer ? 1 : 0; offset <= comp.Players.Count; offset++)
        {
            var index = (comp.CurrentPlayerIndex + offset) % comp.Players.Count;
            if (comp.Players[index].Status != PokerPlayerStatus.Active)
                continue;
            comp.CurrentPlayerIndex = index;
            break;
        }
        SendState(uid, comp);
    }

    private void AdvancePhase(EntityUid uid, PokerTableComponent comp)
    {
        foreach (var p in comp.Players)
        {
            p.CurrentBet = 0;
            if (p.Status == PokerPlayerStatus.Active)
                p.HasActed = false;
        }
        comp.CurrentBet = 0;
        comp.LastRaiseAmount = comp.BigBlind;

        switch (comp.Phase)
        {
            case PokerRoundPhase.PreFlop:
                comp.Phase = PokerRoundPhase.Flop;
                comp.CommunityCards.Add(DealCard(comp));
                comp.CommunityCards.Add(DealCard(comp));
                comp.CommunityCards.Add(DealCard(comp));
                break;
            case PokerRoundPhase.Flop:
                comp.Phase = PokerRoundPhase.Turn;
                comp.CommunityCards.Add(DealCard(comp));
                break;
            case PokerRoundPhase.Turn:
                comp.Phase = PokerRoundPhase.River;
                comp.CommunityCards.Add(DealCard(comp));
                break;
            case PokerRoundPhase.River:
                comp.Phase = PokerRoundPhase.Showdown;
                DoShowdown(uid, comp);
                return;
        }

        var activePlayers = comp.Players.Where(p => p.Status == PokerPlayerStatus.Active).ToList();
        if (activePlayers.Count == 0)
        {
            EndRound(uid, comp);
            return;
        }
        comp.CurrentPlayerIndex = comp.DealerIndex;
        AdvanceTurn(uid, comp);
    }

    private void DoShowdown(EntityUid uid, PokerTableComponent comp)
    {
        comp.Phase = PokerRoundPhase.Showdown;
        AwardPot(comp);
        HoldResult(uid, comp);
    }

    /// <summary>
    /// Ends a hand that never reached a showdown, because everyone but one contender folded or left. The pot is
    /// still awarded and the winner still announced - otherwise the table just silently resets to zero chips.
    /// </summary>
    private void EndRound(EntityUid uid, PokerTableComponent comp)
    {
        // StartNewRound bails through here when it cannot seat two funded players. Nothing was ever staked in
        // that case, so there is no result to show and holding the table on it would stall the next deal.
        var staked = comp.Pot > 0 || comp.Players.Any(p => p.TotalBet > 0);

        AwardPot(comp);

        if (!staked)
        {
            FinishRound(uid, comp);
            return;
        }

        comp.Phase = PokerRoundPhase.Showdown;
        HoldResult(uid, comp);
    }

    /// <summary>
    /// Pays every side pot out into the winners' stacks and marks who took them, so <see cref="SendState"/> has
    /// someone to name. Leaves the phase alone; the caller decides whether the result gets shown.
    /// </summary>
    private void AwardPot(PokerTableComponent comp)
    {
        foreach (var (player, payout) in PokerRules.Payouts(comp))
        {
            player.Stack += payout;
            if (payout > 0 && player.Status != PokerPlayerStatus.Folded)
                player.Status = PokerPlayerStatus.Winner;
        }
        foreach (var player in comp.Players)
            player.TotalBet = 0;
        comp.Pot = 0;
    }

    /// <summary>
    /// Parks the table on its result for a few seconds so everyone can read who won, then cleans up and deals
    /// the next hand if enough players are still funded.
    /// </summary>
    private void HoldResult(EntityUid uid, PokerTableComponent comp)
    {
        SendState(uid, comp);

        var round = comp.RoundNumber;
        Timer.Spawn(5000, () =>
        {
            if (!TryComp<PokerTableComponent>(uid, out var current) ||
                current != comp || current.RoundNumber != round || current.Phase != PokerRoundPhase.Showdown)
                return;
            FinishRound(uid, current);
            if (current.Players.Count(p => p.Stack > 0) >= current.MinPlayers)
                StartNewRound(uid, current);
        });
    }

    private void FinishRound(EntityUid uid, PokerTableComponent comp)
    {
        foreach (var player in comp.Players.Where(p => p.HasLeft && p.Stack > 0))
        {
            GiveCash(player.Entity, player.Stack, uid);
        }
        comp.Players.RemoveAll(p => p.HasLeft || p.Stack <= 0);
        comp.DealerIndex++;
        Reseat(comp);
        comp.Phase = PokerRoundPhase.Waiting;
        foreach (var player in comp.Players)
        {
            player.CurrentBet = 0;
            player.HoleCards.Clear();
            player.Status = PokerPlayerStatus.Waiting;
        }
        comp.CurrentBet = 0;
        SendState(uid, comp);
    }

    private void SendState(EntityUid uid, PokerTableComponent comp)
    {
        var winners = comp.Phase == PokerRoundPhase.Showdown
            ? comp.Players.Where(p => p.Status == PokerPlayerStatus.Winner).ToList()
            : new List<PokerPlayer>();

        // Cards only go face up when the pot was actually contested. A hand everyone else folded out of is won
        // without a showdown, so the winner mucks - revealing there would leak whether they were bluffing.
        var contested = comp.Players.Count(p => p.Status != PokerPlayerStatus.Folded) > 1;

        // Determine whose turn it is
        NetEntity? currentTurnEntity = null;
        var active = comp.Players.Where(p => p.Status == PokerPlayerStatus.Active).ToList();
        if (active.Count > 0
            && comp.Phase != PokerRoundPhase.Waiting
            && comp.Phase != PokerRoundPhase.Showdown)
        {
            currentTurnEntity = GetNetEntity(comp.Players[comp.CurrentPlayerIndex % comp.Players.Count].Entity);
        }

        // Public state must never contain a hidden hand, including folded hands at showdown.
        var playerStatesWithCards = comp.Players.Select(p => new PokerPlayerState
        {
            PlayerName = p.Name,
            Stack = p.Stack,
            CurrentBet = p.CurrentBet,
            Status = p.Status,
            HoleCards = comp.Phase == PokerRoundPhase.Showdown && contested && p.Status != PokerPlayerStatus.Folded
                ? new List<PokerCard>(p.HoleCards) : new List<PokerCard>(),
            IsCurrentTurn = currentTurnEntity.HasValue && GetNetEntity(p.Entity) == currentTurnEntity,
            CanRaise = p.Status == PokerPlayerStatus.Active && !p.HasActed,
            SeatIndex = p.SeatIndex,
            PlayerEntity = GetNetEntity(p.Entity)
        }).ToList();

        var state = new PokerTableBoundUserInterfaceState
        {
            Players = playerStatesWithCards,
            CommunityCards = new List<PokerCard>(comp.CommunityCards),
            Pot = comp.Pot,
            Phase = comp.Phase,
            RoundNumber = comp.RoundNumber,
            CurrentBet = comp.CurrentBet,
            MinRaise = (int) Math.Min((long) comp.CurrentBet + comp.LastRaiseAmount, int.MaxValue),
            // These are placeholders — client overwrites with local entity data
            MyStack = 0,
            MyBet = 0,
            IsMyTurn = false,
            MySeatIndex = -1,
            BigBlind = comp.BigBlind,
            WinnerName = winners.Count > 0 ? string.Join(", ", winners.Select(p => p.Name)) : null,
            WinningHand = winners.Count == 1 && contested
                ? PokerRules.Evaluate(winners[0].HoleCards.Concat(comp.CommunityCards).ToList()).Rank.ToString()
                : null,
            CurrentTurnEntity = currentTurnEntity
        };

        _ui.SetUiState(uid, PokerUiKey.Key, state);
        foreach (var player in comp.Players.Where(p => !p.HasLeft))
        {
            _ui.ServerSendUiMessage(uid, PokerUiKey.Key,
                new PokerPrivateHandMessage(comp.RoundNumber, new List<PokerCard>(player.HoleCards)), player.Entity);
        }
    }

    private PokerCard DealCard(PokerTableComponent comp)
    {
        var card = comp.Deck[^1];
        comp.Deck.RemoveAt(comp.Deck.Count - 1);
        return card;
    }

    private List<PokerCard> BuildAndShuffleDeck()
    {
        var deck = new List<PokerCard>();
        foreach (CardSuit suit in Enum.GetValues(typeof(CardSuit)))
            foreach (CardRank rank in Enum.GetValues(typeof(CardRank)))
                deck.Add(new PokerCard(suit, rank));

        var rng = new Random();
        for (var i = deck.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }

    private int ScanPlayerCash(EntityUid player)
    {
        return (int) Math.Min(CashRoots(player).Sum(CountCashInEntity), int.MaxValue);
    }

    private HashSet<EntityUid> CashRoots(EntityUid player)
    {
        var items = _hands.EnumerateHeld(player).ToHashSet();
        if (_inventory.TryGetContainerSlotEnumerator(player, out var enumerator))
        {
            while (enumerator.NextItem(out var item, out _))
                items.Add(item);
        }
        return items;
    }

    private long CountCashInEntity(EntityUid entity)
    {
        long total = 0;

        if (TryComp<StackComponent>(entity, out var stack) &&
            stack.StackTypeId == "Credit")
        {
            total += stack.Count;
        }

        if (TryComp<Robust.Shared.Containers.ContainerManagerComponent>(entity, out var containerManager))
        {
            foreach (var container in containerManager.Containers.Values)
            {
                foreach (var contained in container.ContainedEntities)
                {
                    total += CountCashInEntity(contained);
                }
            }
        }
        return total;
    }

    private void TakeCash(EntityUid player, int amount)
    {
        if (amount <= 0) return;

        var remaining = amount;
        foreach (var item in CashRoots(player))
        {
            remaining = TakeCashFromEntity(item, remaining);
            if (remaining <= 0)
                break;
        }
    }

    private int TakeCashFromEntity(EntityUid entity, int remaining)
    {
        if (remaining <= 0)
            return 0;

        if (TryComp<StackComponent>(entity, out var stack) && stack.StackTypeId == "Credit")
        {
            var take = Math.Min(stack.Count, remaining);
            _stack.SetCount(entity, stack.Count - take, stack);
            remaining -= take;
        }

        if (TryComp<Robust.Shared.Containers.ContainerManagerComponent>(entity, out var containerManager))
        {
            foreach (var container in containerManager.Containers.Values)
            {
                foreach (var contained in container.ContainedEntities.ToList())
                {
                    remaining = TakeCashFromEntity(contained, remaining);
                    if (remaining <= 0) break;
                }
                if (remaining <= 0) break;
            }
        }
        return remaining;
    }

    private void GiveCash(EntityUid player, int amount, EntityUid? fallback = null)
    {
        var destination = !TerminatingOrDeleted(player) ? player : fallback;
        if (amount <= 0 || destination == null)
            return;

        var coordinates = Transform(destination.Value).Coordinates;
        while (amount > 0)
        {
            var cash = Spawn("SpaceCash", coordinates);
            var stack = Comp<StackComponent>(cash);
            var count = Math.Min(amount, _stack.GetMaxCount(stack));
            _stack.SetCount(cash, count, stack);
            amount -= count;
            if (!TerminatingOrDeleted(player))
                _hands.TryPickup(player, cash);
        }
    }
}
