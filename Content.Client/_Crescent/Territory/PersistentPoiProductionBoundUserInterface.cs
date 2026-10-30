using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiProductionBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PersistentPoiProductionWindow? _window;

    public PersistentPoiProductionBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<PersistentPoiProductionWindow>();
        _window.Panel.OnToggle += machine => SendMessage(new PersistentPoiProductionToggleMessage(machine));
        _window.Panel.OnSelectRecipe += (machine, recipe) => SendMessage(new PersistentPoiProductionSelectRecipeMessage(machine, recipe));
        _window.Panel.OnDispense += machine => SendMessage(new PersistentPoiProductionDispenseMessage(machine));
        _window.Panel.OnDecommission += machine => SendMessage(new PersistentPoiProductionDecommissionMessage(machine));
        _window.Panel.OnSetCapability += capability => SendMessage(new PersistentPoiProductionSetCapabilityMessage(capability));
        _window.Panel.OnRush += (machine, source) => SendMessage(new PersistentPoiProductionRushMessage(machine, source));
        _window.Panel.OnOvertime += source => SendMessage(new PersistentPoiProductionOvertimeMessage(source));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is PersistentPoiProductionConsoleState console)
            _window?.Panel.UpdateState(console);
    }
}
