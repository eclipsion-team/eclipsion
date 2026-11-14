using Content.Client.Lobby;
using Content.Client.MainMenu;
using Content.Client.UserInterface.Systems.EscapeMenu;
using Content.Client.UserInterface.Systems.Info;
using Content.Shared.CCVar;
using Content.Shared.Localizations;
using Robust.Client.State;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Configuration;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Client._Crescent.Translator;

public sealed class LanguageUIController : UIController, IOnStateEntered<LobbyState>
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ContentLocalizationManager _contentLoc = default!;
    [Dependency] private readonly IStateManager _state = default!;

    [Dependency] private readonly ILocalizationManager _loc = default!;

    private LanguagePickerWindow? _picker;
    private LanguageRestartWindow? _restart;

    public override void Initialize()
    {
        base.Initialize();
        // server needs this for chat translation
        _cfg.SetCVar(CCVars.PlayerLanguage, _loc.DefaultCulture?.Name ?? "en-US");
    }

    public void OnStateEntered(LobbyState state)
    {
        if (_cfg.GetCVar(CCVars.LanguagePicked) || _picker is { Disposed: false })
            return;

        _picker = new LanguagePickerWindow();
        _picker.Picked += name =>
        {
            _picker?.Close();
            _picker = null;
            Pick(name);
        };
        _picker.OnClose += () =>
        {
            // closing it with the X counts as keeping the current one
            _cfg.SetCVar(CCVars.LanguagePicked, true);
            _cfg.SaveToFile();
            _picker = null;
        };
        _picker.OpenCentered();
    }

    public void Pick(string name)
    {
        var changed = _contentLoc.SwitchLanguage(name);
        _cfg.SetCVar(CCVars.PlayerLanguage, name);
        _cfg.SetCVar(CCVars.LanguagePicked, true);
        _cfg.SaveToFile();

        if (!changed)
            return;

        // options window is still in its dropdown callback, remake it next frame
        Timer.Spawn(0, () =>
        {
            UIManager.GetUIController<OptionsUIController>().Rebuild();
            UIManager.GetUIController<InfoUIController>().RebuildRules();
        });

        var current = _state.CurrentState;
        if (current is MainScreen)
        {
            Reload(current);
            return;
        }

        // lobby and hud only change after a restart
        _restart?.Close();
        _restart = new LanguageRestartWindow();
        _restart.OpenCentered();
    }

    private void Reload(State current)
    {
        var reload = _state.RequestStateChange<LanguageReloadState>();
        reload.Back = current.GetType();
    }
}
