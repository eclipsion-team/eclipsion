using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.GameTicking.Prototypes;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server.GameTicking.Commands
{
    [AdminCommand(AdminFlags.Round)]
    sealed partial class SetLobbyBackgroundCommand : IConsoleCommand
    {
        [Dependency] private IEntityManager _entManager = default!;
        [Dependency] private IPrototypeManager _prototypeManager = default!;

        public string Command => "setlobbybackground";
        public string Description => "Sets the lobby background for all players.";
        public string Help => $"Usage: {Command} [lobbyBackground prototype id]\nCycles to the next background if no argument is provided.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var ticker = _entManager.System<GameTicker>();

            if (args.Length > 1)
            {
                shell.WriteLine("Need zero or one arguments.");
                return;
            }

            LobbyBackgroundPrototype? background;
            if (args.Length == 1)
            {
                if (!_prototypeManager.TryIndex(args[0], out background))
                {
                    shell.WriteLine($"No lobbyBackground prototype with id '{args[0]}'.");
                    return;
                }
            }
            else
            {
                var backgrounds = ticker.LobbyBackgrounds;
                if (backgrounds.Count == 0)
                {
                    shell.WriteLine("There are no lobby backgrounds loaded.");
                    return;
                }

                var index = ticker.LobbyBackground == null
                    ? 0
                    : (backgrounds.ToList().IndexOf(ticker.LobbyBackground) + 1) % backgrounds.Count;
                background = backgrounds[index];
            }

            ticker.SetLobbyBackground(background);
            shell.WriteLine($"Lobby background set to '{background.ID}'.");
        }

        public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
        {
            if (args.Length == 1)
            {
                var options = _prototypeManager
                    .EnumeratePrototypes<LobbyBackgroundPrototype>()
                    .Select(p => new CompletionOption(p.ID, p.Name))
                    .OrderBy(p => p.Value);

                return CompletionResult.FromHintOptions(options, "<lobbyBackground>");
            }

            return CompletionResult.Empty;
        }
    }
}
