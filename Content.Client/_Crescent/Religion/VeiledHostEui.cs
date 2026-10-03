using Content.Client.Eui;
using Content.Shared._Crescent.Religion;
using Content.Shared.Eui;

namespace Content.Client._Crescent.Religion;

public sealed class VeiledHostEui : BaseEui
{
    private readonly VeiledHostWindow _window;

    public VeiledHostEui()
    {
        _window = new VeiledHostWindow();
        _window.OnChastise += member => SendMessage(new VeiledHostChastiseMessage(member));
        _window.OnRefresh += () => SendMessage(new VeiledHostRefreshMessage());
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is VeiledHostEuiState hostState)
            _window.UpdateState(hostState);
    }

    public override void Closed()
    {
        _window.Close();
    }
}
