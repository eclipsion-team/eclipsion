using System.Numerics;
using Robust.Client;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._Crescent.Translator;

// most of the ui keeps its old text until a restart
public sealed class LanguageRestartWindow : DefaultWindow
{
    [Dependency] private readonly IGameController _game = default!;

    public LanguageRestartWindow(bool manual = false)
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("language-restart-title");
        MinSize = new Vector2(420, 0);

        // redial only works when the launcher started us
        var canRedial = !manual && _game.LaunchState.FromLauncher && _game.LaunchState.Ss14Address != null;

        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        var text = new RichTextLabel();
        text.SetMessage(Loc.GetString(canRedial ? "language-restart-text" : "language-restart-text-manual"));
        box.AddChild(text);

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 6,
            HorizontalAlignment = HAlignment.Right,
        };

        if (canRedial)
        {
            var restart = new Button { Text = Loc.GetString("language-restart-now") };
            restart.OnPressed += _ => Restart();
            buttons.AddChild(restart);
        }

        var later = new Button { Text = Loc.GetString(canRedial ? "language-restart-later" : "language-restart-ok") };
        later.OnPressed += _ => Close();
        buttons.AddChild(later);

        box.AddChild(buttons);
        Contents.AddChild(box);
    }

    private void Restart()
    {
        try
        {
            _game.Redial(_game.LaunchState.Ss14Address!, Loc.GetString("language-restart-redial"));
        }
        catch (Exception e)
        {
            Logger.GetSawmill("language").Error($"Redial for language change failed: {e}");
            Close();
            new LanguageRestartWindow(true).OpenCentered();
        }
    }
}
