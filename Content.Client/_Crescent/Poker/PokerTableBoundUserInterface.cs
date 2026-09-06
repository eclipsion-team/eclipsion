using Content.Shared._Crescent.Poker;
using Robust.Client.Player;
using Robust.Shared.IoC;

namespace Content.Client._Crescent.Poker;

public sealed class PokerTableBoundUserInterface : BoundUserInterface
{
    private PokerTableWindow? _window;
    private PokerPrivateHandMessage? _privateHand;
    private PokerTableBoundUserInterfaceState? _lastState;

    public PokerTableBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = new PokerTableWindow();

        _window.OnClose += Close;
        _window.OnJoin += () => SendMessage(new PokerJoinMessage());
        _window.OnLeave += () => SendMessage(new PokerLeaveMessage());
        _window.OnFold += () => SendMessage(new PokerFoldMessage());
        _window.OnCheck += () => SendMessage(new PokerCheckMessage());
        _window.OnCall += () => SendMessage(new PokerCallMessage());
        _window.OnBet += amount => SendMessage(new PokerBetMessage(amount));
        _window.OnRaise += amount => SendMessage(new PokerRaiseMessage(amount));
        _window.OnStartGame += () => SendMessage(new PokerStartGameMessage());

        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not PokerTableBoundUserInterfaceState uiState)
            return;

        _lastState = uiState;

        // Resolve per-player fields locally — server sends one shared state
        var playerManager = IoCManager.Resolve<IPlayerManager>();
        var entManager = IoCManager.Resolve<IEntityManager>();
        var localEntity = playerManager.LocalSession?.AttachedEntity;

        NetEntity? myNetEntity = null;
        if (localEntity.HasValue && entManager.EntityExists(localEntity.Value))
            myNetEntity = entManager.GetNetEntity(localEntity.Value);

        var myPlayer = myNetEntity.HasValue
            ? uiState.Players.Find(p => p.PlayerEntity == myNetEntity.Value)
            : null;

        if (myPlayer != null && _privateHand?.RoundNumber == uiState.RoundNumber)
            myPlayer.HoleCards = new List<PokerCard>(_privateHand.Cards);

        uiState.MySeatIndex = myPlayer?.SeatIndex ?? -1;
        uiState.MyStack = myPlayer?.Stack ?? 0;
        uiState.MyBet = myPlayer?.CurrentBet ?? 0;
        uiState.IsMyTurn = myNetEntity.HasValue
            && uiState.CurrentTurnEntity.HasValue
            && uiState.CurrentTurnEntity.Value == myNetEntity.Value;

        _window?.UpdateState(uiState);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is not PokerPrivateHandMessage hand)
            return;

        _privateHand = hand;
        if (_lastState != null)
            UpdateState(_lastState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _window?.Dispose();
    }
}
