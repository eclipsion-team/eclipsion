using Content.Shared._Crescent.Territory;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiMappingBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly ILightManager _lights = default!;

    [ViewVariables]
    private PersistentPoiMappingWindow? _window;

    // lighting as it was before full brightness, put back when the view ends
    private bool? _drewLighting;

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
        _window.OnDismantle += () => build.ToggleDeconstructing();
        _window.OnPaint += () => build.OpenPainter();
        _window.OnRecenter += () => SendMessage(new PersistentPoiMappingRecenterMessage());
        _window.OnLights += SetFullBright;
        _window.OnLeave += Close;
    }

    private void SetFullBright(bool on)
    {
        if (on)
        {
            _drewLighting ??= _lights.DrawLighting;
            _lights.DrawLighting = false;
            return;
        }

        RestoreLighting();
    }

    private void RestoreLighting()
    {
        if (_drewLighting is not { } drew)
            return;

        _lights.DrawLighting = drew;
        _drewLighting = null;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            RestoreLighting();
    }
}
