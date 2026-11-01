using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiSiloBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PersistentPoiSiloWindow? _window;

    public PersistentPoiSiloBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<PersistentPoiSiloWindow>();
        _window.OnEject += (material, amount) => SendMessage(new PersistentPoiSiloEjectMessage(material, amount));
        Refresh();
    }

    // the stock comes with the component, so the window follows it rather than a UI state
    public void Refresh()
    {
        if (_window != null && EntMan.TryGetComponent(Owner, out PersistentPoiMaterialSiloComponent? silo))
            _window.Update(silo);
    }
}
