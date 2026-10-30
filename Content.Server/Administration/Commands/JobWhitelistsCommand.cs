using System.Linq;
using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Server.Player;

namespace Content.Server.Administration.Commands;

/// <summary>
/// </summary>
[AdminCommand(AdminFlags.Whitelist)]
public sealed class JobWhitelistsCommand : LocalizedCommands
{
    [Dependency] private readonly EuiManager _eui = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public override string Command => "jobwhitelists";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not {} player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        if (args.Length == 0)
        {
            shell.WriteError(Loc.GetString("shell-need-minimum-one-argument"));
            shell.WriteLine(Help);
            return;
        }

        var playerarg = string.Join(' ', args).Trim();
        var located = await _locator.LookupIdByNameOrIdAsync(playerarg);
        if (located is null)
        {
            shell.WriteError(Loc.GetString("cmd-jobwhitelists-player-err"));
            return;
        }

        var ui = new JobWhitelistsEui(located.UserId, located.Username);
        ui.LoadWhitelists();
        _eui.OpenEui(ui, player);
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
