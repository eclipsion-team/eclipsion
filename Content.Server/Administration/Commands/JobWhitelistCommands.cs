using System.Linq;
using Content.Server.Database;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

/// <summary>
/// </summary>
[AdminCommand(AdminFlags.Ban)]
public sealed class GetJobWhitelistCommand : LocalizedCommands
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IPlayerLocator _playerLocator = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public override string Command => "jobwhitelistget";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteError("This command needs at least one argument.");
            shell.WriteLine(Help);
            return;
        }

        var player = string.Join(' ', args).Trim();
        var data = await _playerLocator.LookupIdByNameAsync(player);
        if (data != null)
        {
            var guid = data.UserId;
            var whitelists = await _db.GetJobWhitelists(guid);
            if (whitelists.Count == 0)
            {
                shell.WriteLine(Loc.GetString("cmd-jobwhitelistget-whitelisted-none", ("player", player)));
                return;
            }

            shell.WriteLine(Loc.GetString("cmd-jobwhitelistget-whitelisted-for",
                ("player", player),
                ("jobs", string.Join(", ", whitelists))));
            return;
        }

        shell.WriteError(Loc.GetString("cmd-jobwhitelist-player-not-found", ("player", player)));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                _players.Sessions.Select(s => s.Name),
                Loc.GetString("cmd-jobwhitelist-hint-player"));
        }

        return CompletionResult.Empty;
    }
}
