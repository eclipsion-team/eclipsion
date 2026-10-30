using System.Linq;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

public abstract partial class PlaytimeCommandBase : LocalizedCommands
{
    [Dependency] private readonly EuiManager _eui = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public override string Description => Loc.GetString("cmd-playtime-desc");
    public override string Help => Loc.GetString("cmd-playtime-help", ("command", Command));

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        var ui = new PlaytimeEui();
        _eui.OpenEui(ui, player);

        if (args.Length > 0)
            ui.SelectPlayer(string.Join(' ', args));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                _players.Sessions.Select(s => s.Name),
                Loc.GetString("cmd-playtime-hint-player"));
        }

        return CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class PlaytimeAddOverallCommand : PlaytimeCommandBase
{
    public override string Command => "playtime_addoverall";
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class PlayerTimeCommand : PlaytimeCommandBase
{
    public override string Command => "playertime";
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class TimeAddCommand : PlaytimeCommandBase
{
    public override string Command => "timeadd";
}
