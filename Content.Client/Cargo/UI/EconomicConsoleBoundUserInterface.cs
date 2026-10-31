using Content.Client._Crescent.Mainframe;
using Content.Shared._Crescent.Mainframe;
using Content.Shared.Cargo;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;

namespace Content.Client.Cargo.UI;

public sealed class EconomicConsoleBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;

    private EconomicConsoleWindow? _window;
    private EconomicConsolePanel? _panel;
    private MainframeUiController? _mainframe;

    public EconomicConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _panel = new EconomicConsolePanel();
        _panel.OnSearch += search => SendMessage(new EconomicConsoleSearchMessage(search));

        // On a mainframe this renders as a tab instead of its own window.
        if (UiSystem.HasUi(Owner, MainframeUiKey.Key))
        {
            _mainframe = _uiManager.GetUIController<MainframeUiController>();
            _mainframe.AttachPanel(Owner, MainframeTab.Economy, _panel, this);
        }
        else
        {
            _window = new EconomicConsoleWindow();
            _window.SetPanel(_panel);
            _window.OpenCentered();
            _window.OnClose += Close;
        }
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is EconomicConsoleBuiState buiState)
            _panel?.UpdateState(buiState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        _mainframe?.DetachPanel(Owner, MainframeTab.Economy);
        _window?.Dispose();
        _window = null;
    }
}
