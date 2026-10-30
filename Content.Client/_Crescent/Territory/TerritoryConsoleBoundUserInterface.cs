using Content.Client._Crescent.Mainframe;
using Content.Shared._Crescent.Mainframe;
using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class TerritoryConsoleBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;

    private TerritoryConsolePanel? _panel;
    private MainframeUiController? _mainframe;

    public TerritoryConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _panel = new TerritoryConsolePanel();
        _panel.OnSelect += regionId => SendMessage(new TerritoryConsoleSelectMessage(regionId));
        _panel.Production.OnToggle += machine => SendMessage(new PersistentPoiProductionToggleMessage(machine));
        _panel.Production.OnSelectRecipe += (machine, recipe) =>
            SendMessage(new PersistentPoiProductionSelectRecipeMessage(machine, recipe));
        _panel.Production.OnSetCapability += capability =>
            SendMessage(new PersistentPoiProductionSetCapabilityMessage(capability));
        _panel.Command.OnDefenceOrder += (order, enabled) => SendMessage(new TerritoryConsoleDefenceMessage(order, enabled));
        _panel.Command.OnRename += name => SendMessage(new TerritoryConsoleRenameMessage(name));
        _panel.Command.OnAbandon += () => SendMessage(new TerritoryConsoleAbandonMessage());
        _panel.Command.OnRemoteFire += () => SendMessage(new TerritoryConsoleRemoteFireMessage());
        _panel.Production.OnRush += (machine, source) => SendMessage(new PersistentPoiProductionRushMessage(machine, source));
        _panel.Production.OnOvertime += source => SendMessage(new PersistentPoiProductionOvertimeMessage(source));

        if (UiSystem.HasUi(Owner, MainframeUiKey.Key))
        {
            _mainframe = _uiManager.GetUIController<MainframeUiController>();
            _mainframe.AttachPanel(Owner, MainframeTab.Territory, _panel, this);
        }
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is TerritoryConsoleState console)
            _panel?.UpdateState(console);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _mainframe?.DetachPanel(Owner, MainframeTab.Territory);
    }
}
