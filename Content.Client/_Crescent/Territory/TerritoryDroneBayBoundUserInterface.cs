using Content.Shared._Crescent;
using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class TerritoryDroneBayBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private TerritoryDroneBayWindow? _window;

    public TerritoryDroneBayBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<TerritoryDroneBayWindow>();
        _window.OnLaunch += (hull, source) => SendMessage(new TerritoryDroneBayLaunchMessage(hull, source));
        _window.OnCommand += (drone, command, target) =>
            SendMessage(new TerritoryDroneBayCommandMessage(drone, command, target));
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);

        if (message is ViewerUiStateMessage viewer)
            UpdateState(viewer.State);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is TerritoryDroneBayState bay)
            _window?.UpdateState(bay);
    }
}
