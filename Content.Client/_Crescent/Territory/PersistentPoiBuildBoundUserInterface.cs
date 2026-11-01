using Robust.Client.UserInterface;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiBuildBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PersistentPoiBuildWindow? _window;

    public PersistentPoiBuildBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        var build = EntMan.System<PersistentPoiBuildSystem>();

        _window = this.CreateWindow<PersistentPoiBuildWindow>();
        _window.Populate(build.GetMenu());

        _window.OnBuild += recipe =>
        {
            build.StartPlacing(recipe);

            // from the construction console the menu stays up, like a mapping palette
            if (!build.IsViewing)
                Close();
        };
        _window.OnPaint += () =>
        {
            build.OpenPainter();
            Close();
        };
    }
}
