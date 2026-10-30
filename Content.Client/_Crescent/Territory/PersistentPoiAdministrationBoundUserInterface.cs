using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiAdministrationBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PersistentPoiAdministrationWindow? _window;

    public PersistentPoiAdministrationBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<PersistentPoiAdministrationWindow>();
        _window.Panel.OnUpgrade += () => SendMessage(new PersistentPoiUpgradeMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is PersistentPoiAdministrationState administration)
            _window?.Panel.UpdateState(administration);
    }
}
