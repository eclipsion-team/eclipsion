using Content.Shared._Crescent.FactionPortal;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.FactionPortal;

public sealed class FactionPortalBoundUserInterface : BoundUserInterface
{
    private FactionPortalWindow? _window;

    public FactionPortalBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<FactionPortalWindow>();
        _window.OnDestinationPicked += destination => SendMessage(new FactionPortalSelectMessage(destination));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is FactionPortalBuiState portalState)
            _window?.UpdateState(portalState);
    }
}
