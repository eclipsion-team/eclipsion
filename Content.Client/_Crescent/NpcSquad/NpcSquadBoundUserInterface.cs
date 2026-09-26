using Content.Shared._Crescent.NpcSquad;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.NpcSquad;

public sealed class NpcSquadBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private NpcSquadWindow? _window;

    public NpcSquadBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<NpcSquadWindow>();
        _window.OnOrder += (member, order) => SendMessage(new NpcSquadOrderMessage(member, order));
        _window.OnDismiss += member => SendMessage(new NpcSquadDismissMessage(member));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is NpcSquadBuiState squadState)
            _window?.UpdateState(squadState);
    }
}
