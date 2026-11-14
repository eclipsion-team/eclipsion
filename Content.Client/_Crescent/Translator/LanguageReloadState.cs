using Robust.Client.State;
using Robust.Shared.Timing;

namespace Content.Client._Crescent.Translator;

// empty state for one frame so the main menu gets rebuilt
public sealed class LanguageReloadState : State
{
    [Dependency] private readonly IStateManager _state = default!;

    public Type? Back;

    protected override void Startup()
    {
    }

    protected override void Shutdown()
    {
    }

    public override void FrameUpdate(FrameEventArgs e)
    {
        if (Back == null)
            return;

        var back = Back;
        Back = null;
        _state.RequestStateChange(back);
    }
}
