using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class TerritoryRecruitmentBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private TerritoryRecruitmentWindow? _window;

    public TerritoryRecruitmentBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<TerritoryRecruitmentWindow>();
        _window.OnRecruit += (kind, count, source) => SendMessage(new TerritoryRecruitMessage(kind, count, source));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is TerritoryRecruitmentState recruitment)
            _window?.UpdateState(recruitment);
    }
}
