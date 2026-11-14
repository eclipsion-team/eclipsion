using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Crescent.LagMonitor;

[AdminCommand(AdminFlags.Admin)]
public sealed partial class LagMonitorCommand : IConsoleCommand
{
    [Dependency] private readonly EuiManager _eui = default!;

    public string Command => "lagmonitor";
    public string Description => "Opens the lag monitor: what the server spends its time on and recent freezes.";
    public string Help => "lagmonitor";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteLine("Use lagreport from the server console.");
            return;
        }

        _eui.OpenEui(new LagMonitorEui(), shell.Player);
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class LagReportCommand : IConsoleCommand
{
    [Dependency] private readonly LagMonitorManager _monitor = default!;

    public string Command => "lagreport";
    public string Description => "Prints what the server spent its time on in the last 5 seconds and the last freezes.";
    public string Help => "lagreport";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        shell.WriteLine(_monitor.BuildReport());
    }
}
