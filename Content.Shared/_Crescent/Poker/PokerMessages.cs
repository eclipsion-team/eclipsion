using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Poker;

[Serializable, NetSerializable]
public sealed class PokerPlayerState
{
    public string PlayerName { get; set; } = string.Empty;
    public int Stack { get; set; }
    public int CurrentBet { get; set; }
    public PokerPlayerStatus Status { get; set; }
    public List<PokerCard>? HoleCards { get; set; }
    public bool IsCurrentTurn { get; set; }
    public bool CanRaise { get; set; }
    public int SeatIndex { get; set; }
    public NetEntity PlayerEntity { get; set; }
}

[Serializable, NetSerializable]
public sealed class PokerTableBoundUserInterfaceState : BoundUserInterfaceState
{
    public List<PokerPlayerState> Players { get; set; } = new();
    public List<PokerCard> CommunityCards { get; set; } = new();
    public int Pot { get; set; }
    public PokerRoundPhase Phase { get; set; }
    public int RoundNumber { get; set; }
    public int CurrentBet { get; set; }
    public int MinRaise { get; set; }
    public int MyStack { get; set; }
    public int MyBet { get; set; }
    public bool IsMyTurn { get; set; }
    public int MySeatIndex { get; set; }
    public int BigBlind { get; set; }
    public string? WinnerName { get; set; }
    public string? WinningHand { get; set; }
    public NetEntity? CurrentTurnEntity { get; set; }
}

[Serializable, NetSerializable]
public sealed class PokerPrivateHandMessage : BoundUserInterfaceMessage
{
    public readonly int RoundNumber;
    public readonly List<PokerCard> Cards;

    public PokerPrivateHandMessage(int roundNumber, List<PokerCard> cards)
    {
        RoundNumber = roundNumber;
        Cards = cards;
    }
}

[Serializable, NetSerializable]
public sealed class PokerJoinMessage : BoundUserInterfaceMessage { }

[Serializable, NetSerializable]
public sealed class PokerLeaveMessage : BoundUserInterfaceMessage { }

[Serializable, NetSerializable]
public sealed class PokerFoldMessage : BoundUserInterfaceMessage { }

[Serializable, NetSerializable]
public sealed class PokerCheckMessage : BoundUserInterfaceMessage { }

[Serializable, NetSerializable]
public sealed class PokerCallMessage : BoundUserInterfaceMessage { }

[Serializable, NetSerializable]
public sealed class PokerBetMessage : BoundUserInterfaceMessage
{
    public int Amount { get; set; }

    public PokerBetMessage(int amount)
    {
        Amount = amount;
    }
}

[Serializable, NetSerializable]
public sealed class PokerRaiseMessage : BoundUserInterfaceMessage
{
    public int Amount { get; set; }

    public PokerRaiseMessage(int amount)
    {
        Amount = amount;
    }
}

[Serializable, NetSerializable]
public sealed class PokerStartGameMessage : BoundUserInterfaceMessage { }
