using Content.Client.Eui;
using Content.Shared._Crescent.LagMonitor;
using Content.Shared.Eui;

namespace Content.Client._Crescent.LagMonitor;

public sealed class LagMonitorEui : BaseEui
{
    private readonly LagMonitorWindow _window;

    public LagMonitorEui()
    {
        _window = new LagMonitorWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.ClearPressed += () => SendMessage(new LagMonitorEuiMsg.ClearHitches());
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is LagMonitorEuiState lag)
            _window.UpdateState(lag);
    }
}
