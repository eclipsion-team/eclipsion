using Content.Client._Crescent.Lobby;
using Content.Client._NF.Latejoin;
using Content.Client.Audio;
using Content.Client.GameTicking.Managers;
using Content.Client.LateJoin;
using Content.Client.Lobby.UI;
using Content.Client.Message;
using Content.Client.ReadyManifest;
using Content.Client.UserInterface.Systems.Chat;
using Content.Client.Voting;
using Robust.Client;
using Robust.Client.Console;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.Lobby
{
    public sealed class LobbyState : Robust.Client.State.State
    {
        [Dependency] private readonly IBaseClient _baseClient = default!;
        [Dependency] private readonly IClientConsoleHost _consoleHost = default!;
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly IResourceCache _resourceCache = default!;
        [Dependency] private readonly IUserInterfaceManager _userInterfaceManager = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly IVoteManager _voteManager = default!;

        private ISawmill _sawmill = default!;
        private ClientGameTicker _gameTicker = default!;
        private ContentAudioSystem _contentAudioSystem = default!;
        private ReadyManifestSystem _readyManifest = default!;

        /// The late-join window, while one is open. Pressing join again focuses it instead of stacking another.
        private NFLateJoinGui? _lateJoin;

        // Eclipsion - the lobby notes window, while one is open.
        private LobbyNotesWindow? _notes;

        protected override Type? LinkedScreenType { get; } = typeof(LobbyGui);
        public LobbyGui? Lobby;

        protected override void Startup()
        {
            if (_userInterfaceManager.ActiveScreen == null)
                return;

            Lobby = (LobbyGui) _userInterfaceManager.ActiveScreen;

            var chatController = _userInterfaceManager.GetUIController<ChatUIController>();
            _gameTicker = _entityManager.System<ClientGameTicker>();
            _contentAudioSystem = _entityManager.System<ContentAudioSystem>();
            _contentAudioSystem.LobbySoundtrackChanged += UpdateLobbySoundtrackInfo;
            _sawmill = Logger.GetSawmill("lobby");
            _readyManifest = _entityManager.EntitySysManager.GetEntitySystem<ReadyManifestSystem>();

            chatController.SetMainChat(true);

            _voteManager.SetPopupContainer(Lobby.VoteContainer);
            LayoutContainer.SetAnchorPreset(Lobby, LayoutContainer.LayoutPreset.Wide);
            Lobby.ServerName.Text = _baseClient.GameInfo?.ServerName; // The eye of refactor gazes upon you...

            UpdateLobbyUi();

            Lobby.CharacterPreview.CharacterSetupButton.OnPressed += OnSetupPressed;
            Lobby.ManifestButton.OnPressed += OnManifestPressed;
            Lobby.ReadyButton.OnPressed += OnReadyPressed;
            Lobby.ReadyButton.OnToggled += OnReadyToggled;
            Lobby.NotesButton.OnPressed += OnNotesPressed;

            _gameTicker.InfoBlobUpdated += UpdateLobbyUi;
            _gameTicker.LobbyStatusUpdated += LobbyStatusUpdated;
            _gameTicker.LobbyLateJoinStatusUpdated += LobbyLateJoinStatusUpdated;
        }

        protected override void Shutdown()
        {
            var chatController = _userInterfaceManager.GetUIController<ChatUIController>();
            chatController.SetMainChat(false);
            _gameTicker.InfoBlobUpdated -= UpdateLobbyUi;
            _gameTicker.LobbyStatusUpdated -= LobbyStatusUpdated;
            _gameTicker.LobbyLateJoinStatusUpdated -= LobbyLateJoinStatusUpdated;
            _contentAudioSystem.LobbySoundtrackChanged -= UpdateLobbySoundtrackInfo;

            _voteManager.ClearPopupContainer();

            Lobby!.CharacterPreview.CharacterSetupButton.OnPressed -= OnSetupPressed;
            Lobby!.ManifestButton.OnPressed -= OnManifestPressed;
            Lobby!.ReadyButton.OnPressed -= OnReadyPressed;
            Lobby!.ReadyButton.OnToggled -= OnReadyToggled;
            Lobby!.NotesButton.OnPressed -= OnNotesPressed;

            // Leaving the lobby (joining or observing) should not leave the join window floating over the game.
            _lateJoin?.Close();
            _lateJoin = null;

            // Closing saves, so nothing typed right before the round starts is lost.
            _notes?.Close();
            _notes = null;

            Lobby = null;
        }

        public void SwitchState(LobbyGui.LobbyGuiState state)
        {
            // Yeah I hate this but LobbyState contains all the badness for now
            Lobby?.SwitchState(state);
        }

        private void OnSetupPressed(BaseButton.ButtonEventArgs args)
        {
            SetReady(false);
            Lobby?.SwitchState(LobbyGui.LobbyGuiState.CharacterSetup);
        }

        private void OnReadyPressed(BaseButton.ButtonEventArgs args)
        {
            if (!_gameTicker.IsGameStarted)
                return;

            // Every window listens to the ticker for job counts, so opening a second one doubles that work
            // and leaves the player with a stack of identical windows to close.
            if (_lateJoin is { Disposed: false, IsOpen: true })
            {
                _lateJoin.MoveToFront();
                return;
            }

            _lateJoin = new NFLateJoinGui();
            _lateJoin.OnClose += () => _lateJoin = null;
            _lateJoin.OpenCentered();
        }

        private void OnNotesPressed(BaseButton.ButtonEventArgs args)
        {
            if (_notes is { Disposed: false, IsOpen: true })
            {
                _notes.Close();
                return;
            }

            _notes = new LobbyNotesWindow();
            _notes.OnClose += () => _notes = null;
            _notes.OpenCentered();
        }

        private void OnReadyToggled(BaseButton.ButtonToggledEventArgs args)
        {
            SetReady(args.Pressed);
            // Don't wait for the server to echo the new state back before the label agrees with the button.
            UpdateReadyButtonText(args.Pressed);
        }

        private void UpdateReadyButtonText(bool ready)
        {
            if (Lobby == null)
                return;

            Lobby.ReadyButton.Text = Loc.GetString(ready
                ? "lobby-state-player-status-ready"
                : "lobby-state-player-status-not-ready");
        }

        private void OnManifestPressed(BaseButton.ButtonEventArgs args)
        {
            _readyManifest.RequestReadyManifest();
        }

        public override void FrameUpdate(FrameEventArgs e)
        {
            if (_gameTicker.IsGameStarted)
            {
                Lobby!.StartTime.Text = string.Empty;
                var roundTime = _gameTiming.CurTime.Subtract(_gameTicker.RoundStartTimeSpan);
                Lobby!.StationTime.Text = Loc.GetString("lobby-state-player-status-round-time", ("hours", roundTime.Hours), ("minutes", roundTime.Minutes));
                return;
            }

            Lobby!.StationTime.Text = Loc.GetString("lobby-state-player-status-round-not-started");
            string text;

            if (_gameTicker.Paused)
                text = Loc.GetString("lobby-state-paused");
            else if (_gameTicker.StartTime < _gameTiming.CurTime)
            {
                Lobby!.StartTime.Text = Loc.GetString("lobby-state-soon");
                return;
            }
            else
            {
                var difference = _gameTicker.StartTime - _gameTiming.CurTime;
                var seconds = difference.TotalSeconds;
                if (seconds < 0)
                    text = Loc.GetString(seconds < -5
                        ? "lobby-state-right-now-question"
                        : "lobby-state-right-now-confirmation");
                else
                    text = $"{difference.Minutes}:{difference.Seconds:D2}";
            }

            Lobby!.StartTime.Text = Loc.GetString("lobby-state-round-start-countdown-text", ("timeLeft", text));
        }

        private void LobbyStatusUpdated()
        {
            UpdateLobbyBackground();
            UpdateLobbyUi();
        }

        private void LobbyLateJoinStatusUpdated()
        {
            Lobby!.ReadyButton.Disabled = _gameTicker.DisallowedLateJoin;
        }

        private void UpdateLobbyUi()
        {
            if (_gameTicker.IsGameStarted)
            {
                Lobby!.ReadyButton.Text = Loc.GetString("lobby-state-ready-button-join-state");
                Lobby!.ReadyButton.ToggleMode = false;
                Lobby!.ReadyButton.Pressed = false;
                Lobby!.ObserveButton.Disabled = false;
                Lobby!.ManifestButton.Disabled = true;
            }
            else
            {
                Lobby!.StartTime.Text = string.Empty;
                Lobby!.ReadyButton.ToggleMode = true;
                Lobby!.ReadyButton.Disabled = false;
                Lobby!.ReadyButton.Pressed = _gameTicker.AreWeReady;
                // Read from the ticker, not from the button: the label used to be built before Pressed was
                // updated, so anything that changed readiness without a click (opening character setup, the
                // server unreadying you) left the button saying the opposite of what it showed.
                UpdateReadyButtonText(_gameTicker.AreWeReady);
                Lobby!.ManifestButton.Disabled = false;
                Lobby!.ObserveButton.Disabled = true;
            }

            if (_gameTicker.ServerInfoBlob != null)
                Lobby!.ServerInfo.SetInfoBlob(_gameTicker.ServerInfoBlob);
        }

        private void UpdateLobbySoundtrackInfo(LobbySoundtrackChangedEvent ev)
        {
            if (ev.SoundtrackFilename == null)
                Lobby!.LobbySong.SetMarkup(Loc.GetString("lobby-state-song-no-song-text"));
            else if (ev.SoundtrackFilename != null
                && _resourceCache.TryGetResource<AudioResource>(ev.SoundtrackFilename, out var lobbySongResource))
            {
                var lobbyStream = lobbySongResource.AudioStream;

                var title = string.IsNullOrEmpty(lobbyStream.Title)
                    ? Loc.GetString("lobby-state-song-unknown-title")
                    : lobbyStream.Title;

                var artist = string.IsNullOrEmpty(lobbyStream.Artist)
                    ? Loc.GetString("lobby-state-song-unknown-artist")
                    : lobbyStream.Artist;

                var markup = Loc.GetString("lobby-state-song-text",
                    ("songTitle", title),
                    ("songArtist", artist));

                Lobby!.LobbySong.SetMarkup(markup);
            }
        }

        private void UpdateLobbyBackground()
        {
            if (_gameTicker.LobbyBackground != null)
            {
                var lobbyBackground = _gameTicker.LobbyBackground;

                if (lobbyBackground.AnimatedBackground != null)
                {
                    Lobby!.Background.Visible = false;
                    Lobby!.AnimatedBg.Visible = true;
                    Lobby!.AnimatedBg.SetFromSpriteSpecifier(lobbyBackground.AnimatedBackground);
                }
                else if (lobbyBackground.Background is { } background)
                {
                    Lobby!.AnimatedBg.Visible = false;
                    Lobby!.Background.Visible = true;
                    Lobby!.Background.Texture = _resourceCache.GetResource<TextureResource>(background);
                }
                else
                {
                    Lobby!.AnimatedBg.Visible = false;
                    Lobby!.Background.Visible = false;
                }

                var name = string.IsNullOrEmpty(lobbyBackground.Name)
                    ? Loc.GetString("lobby-state-background-unknown-title")
                    : lobbyBackground.Name;

                var artist = string.IsNullOrEmpty(lobbyBackground.Artist)
                    ? Loc.GetString("lobby-state-background-unknown-artist")
                    : lobbyBackground.Artist;

                var markup = Loc.GetString("lobby-state-background-text",
                    ("backgroundName", name),
                    ("backgroundArtist", artist));

                Lobby!.LobbyBackground.SetMarkup(markup);

                return;
            }

            _sawmill.Warning("_gameTicker.LobbyBackground was null! No lobby background selected.");
            Lobby!.AnimatedBg.Visible = false;
            Lobby!.Background.Visible = true;
            Lobby!.Background.Texture = null;
            Lobby!.LobbyBackground.SetMarkup(Loc.GetString("lobby-state-background-no-background-text"));
        }

        private void SetReady(bool newReady)
        {
            if (_gameTicker.IsGameStarted)
                return;

            _consoleHost.ExecuteCommand($"toggleready {newReady}");
        }
    }
}
