using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._Crescent.Poker;

[RegisterComponent]
public sealed partial class PokerTableComponent : Component
{
    [DataField]
    public int BigBlind { get; set; } = 100;

    [DataField]
    public int SmallBlind { get; set; } = 50;

    [DataField]
    public int MaxPlayers { get; set; } = 6;

    [DataField]
    public int MinPlayers { get; set; } = 2;

    [DataField]
    public int StartingBuyIn { get; set; } = 1000;

    public List<PokerPlayer> Players { get; set; } = new();
    public List<PokerCard> Deck { get; set; } = new();
    public List<PokerCard> CommunityCards { get; set; } = new();
    public int Pot { get; set; }
    public int CurrentBet { get; set; }
    public int CurrentPlayerIndex { get; set; }
    public int DealerIndex { get; set; }
    public PokerRoundPhase Phase { get; set; } = PokerRoundPhase.Waiting;
    public int LastRaiseAmount { get; set; }
    public int RoundNumber { get; set; }
}

public sealed class PokerPlayer
{
    public EntityUid Entity { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Stack { get; set; }
    public int CurrentBet { get; set; }
    public PokerPlayerStatus Status { get; set; } = PokerPlayerStatus.Waiting;
    public int TotalBet { get; set; }
    public bool HasLeft { get; set; }
    public List<PokerCard> HoleCards { get; set; } = new();
    public int SeatIndex { get; set; }
    public bool HasActed { get; set; }
}
