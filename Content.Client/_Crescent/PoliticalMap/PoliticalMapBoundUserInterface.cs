using Content.Client._Crescent.Mainframe;
using Content.Shared._Crescent.Mainframe;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.PoliticalMap;

public sealed class PoliticalMapBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;

    private PoliticalMapPanel? _panel;
    private MainframeUiController? _mainframe;

    public PoliticalMapBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _panel = new PoliticalMapPanel { Viewer = Owner };

        if (UiSystem.HasUi(Owner, MainframeUiKey.Key))
        {
            _mainframe = _uiManager.GetUIController<MainframeUiController>();
            _mainframe.AttachPanel(Owner, MainframeTab.Politics, _panel, this);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _mainframe?.DetachPanel(Owner, MainframeTab.Politics);
    }
}
