using Content.Shared.GameTicking.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Server.GameTicking;

public sealed partial class GameTicker
{
    [ViewVariables]
    public LobbyBackgroundPrototype? LobbyBackground { get; private set; }

    [ViewVariables]
    private List<LobbyBackgroundPrototype> _lobbyBackgrounds = [];

    private static readonly string[] WhitelistedBackgroundExtensions = new string[] {"png", "jpg", "jpeg", "webp"};

    private void InitializeLobbyBackground()
    {
        foreach (var prototype in _prototypeManager.EnumeratePrototypes<LobbyBackgroundPrototype>())
        {
            if (prototype.AnimatedBackground != null)
            {
                _lobbyBackgrounds.Add(prototype);
                continue;
            }

            if (prototype.Background is not { } background)
            {
                _sawmill.Warning($"Lobby background '{prototype.ID}' has neither a background nor an animated background and will be ignored.");
                continue;
            }

            if (!WhitelistedBackgroundExtensions.Contains(background.Extension))
            {
                _sawmill.Warning($"Lobby background '{prototype.ID}' has an invalid extension '{background.Extension}' and will be ignored.");
                continue;
            }

            _lobbyBackgrounds.Add(prototype);
        }

        RandomizeLobbyBackground();
    }

    private void RandomizeLobbyBackground() {
        LobbyBackground = _lobbyBackgrounds!.Any() ? _robustRandom.Pick(_lobbyBackgrounds!) : null;
    }

    // Crescent: used by the setlobbybackground admin command.
    public IReadOnlyList<LobbyBackgroundPrototype> LobbyBackgrounds => _lobbyBackgrounds;

    /// <summary>
    /// Forces the lobby background and pushes the change to all connected clients.
    /// </summary>
    public void SetLobbyBackground(LobbyBackgroundPrototype background)
    {
        LobbyBackground = background;
        SendStatusToAll();
    }
}
