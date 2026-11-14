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
        _window.Panel.OnDraw += () => SendMessage(new PersistentPoiDrawFromSiloMessage());
        _window.Panel.OnReturn += (material, toSilo) => SendMessage(new PersistentPoiReturnDepositMessage(material, toSilo));
        _window.Panel.OnRename += name => SendMessage(new PersistentPoiRenameMessage(name));
        _window.Panel.OnSetDrones += off => SendMessage(new PersistentPoiSetDronesMessage(off));
        _window.Panel.OnAddMember += body => SendMessage(new PersistentPoiAddMemberMessage(body));
        _window.Panel.OnRemoveMember += user => SendMessage(new PersistentPoiRemoveMemberMessage(user));
        _window.Panel.OnSetCoOwner += (user, coOwner) => SendMessage(new PersistentPoiSetCoOwnerMessage(user, coOwner));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is PersistentPoiAdministrationState administration)
            _window?.Panel.UpdateState(administration);
    }
}
