using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiMappingBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PersistentPoiMappingWindow? _window;

    public PersistentPoiMappingBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        var build = EntMan.System<PersistentPoiBuildSystem>();

        _window = this.CreateWindow<PersistentPoiMappingWindow>();
        _window.OpenCenteredLeft();

        _window.OnMenu += () => SendMessage(new PersistentPoiMappingOpenMenuMessage());
        _window.OnDismantle += () => build.StartDeconstructing();
        _window.OnPaint += () => build.OpenPainter();
        _window.OnRecenter += () => SendMessage(new PersistentPoiMappingRecenterMessage());
        _window.OnLeave += Close;
    }
}
