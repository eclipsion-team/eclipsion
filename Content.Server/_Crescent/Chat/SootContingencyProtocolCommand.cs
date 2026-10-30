using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;

namespace Content.Server._Crescent.Chat;

[AdminCommand(AdminFlags.Admin)]
public sealed class SootContingencyProtocolCommand : LocalizedCommands
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;

    public override string Command => "sootcontingencyprotocol";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteLine(Help);
            return;
        }

        var mode = args[0].ToLowerInvariant();

        if (mode == "list")
        {
            List(shell);
            return;
        }

        if (args.Length < 2)
        {
            shell.WriteLine(Help);
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[1]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString("cmd-sootcontingencyprotocol-player-not-found", ("player", args[1])));
            return;
        }

        switch (mode)
        {
            case "activate" or "active" or "on":
                Activate(shell, located.UserId, located.Username, args);
                break;
            case "deactivate" or "deactive" or "off":
                Deactivate(shell, located.UserId, located.Username);
                break;
            case "status":
                Status(shell, located.UserId, located.Username);
                break;
            case "adminfun":
                AdminFun(shell, located.UserId, located.Username, args);
                break;
            default:
                shell.WriteLine(Help);
                break;
        }
    }

    private void Activate(IConsoleShell shell, NetUserId user, string name, string[] args)
    {
        uint minutes = 0;
        var reasonStart = 2;
        if (args.Length > 2 && uint.TryParse(args[2], out var parsed))
        {
            minutes = parsed;
            reasonStart = 3;
        }

        var reason = args.Length > reasonStart ? string.Join(' ', args.Skip(reasonStart)) : null;
        var admin = shell.Player?.Name ?? Loc.GetString("cmd-sootcontingencyprotocol-server");
        var now = DateTime.UtcNow;

        if (minutes >= (DateTime.MaxValue - now).TotalMinutes)
            minutes = 0;

        DateTime? expires = minutes > 0 ? now.AddMinutes(minutes) : null;

        // re-activating to change the time shouldn't switch adminfun off
        var adminFun = _chat.TryGetSootProtocol(user, out var existing) && existing.AdminFun;
        _chat.ActivateSootProtocol(user, new SootProtocolEntry(name, admin, reason, now, expires, adminFun));

        var duration = minutes > 0
            ? Loc.GetString("cmd-sootcontingencyprotocol-minutes", ("minutes", minutes))
            : Loc.GetString("cmd-sootcontingencyprotocol-permanent");

        if (_playerManager.TryGetSessionById(user, out var session))
        {
            _chat.DispatchServerMessage(session, Loc.GetString("soot-protocol-activated",
                ("duration", duration),
                ("reason", reason ?? Loc.GetString("cmd-sootcontingencyprotocol-no-reason"))));
        }

        _chat.SendAdminAnnouncement(Loc.GetString("cmd-sootcontingencyprotocol-admin-activated",
            ("admin", admin), ("player", name), ("duration", duration)));
        _adminLogger.Add(LogType.AdminCommands, LogImpact.High,
            $"{admin} activated the Soot Contingency Protocol on {name} ({user}) for {duration}. Reason: {reason ?? "none"}");

        shell.WriteLine(Loc.GetString("cmd-sootcontingencyprotocol-activated", ("player", name), ("duration", duration)));
    }

    private void Deactivate(IConsoleShell shell, NetUserId user, string name)
    {
        if (!_chat.DeactivateSootProtocol(user))
        {
            shell.WriteError(Loc.GetString("cmd-sootcontingencyprotocol-not-active", ("player", name)));
            return;
        }

        var admin = shell.Player?.Name ?? Loc.GetString("cmd-sootcontingencyprotocol-server");

        if (_playerManager.TryGetSessionById(user, out var session))
            _chat.DispatchServerMessage(session, Loc.GetString("soot-protocol-deactivated"));

        _chat.SendAdminAnnouncement(Loc.GetString("cmd-sootcontingencyprotocol-admin-deactivated",
            ("admin", admin), ("player", name)));
        _adminLogger.Add(LogType.AdminCommands, LogImpact.Medium,
            $"{admin} deactivated the Soot Contingency Protocol on {name} ({user})");

        shell.WriteLine(Loc.GetString("cmd-sootcontingencyprotocol-deactivated", ("player", name)));
    }

    private void AdminFun(IConsoleShell shell, NetUserId user, string name, string[] args)
    {
        if (!_chat.TryGetSootProtocol(user, out var entry))
        {
            shell.WriteError(Loc.GetString("cmd-sootcontingencyprotocol-adminfun-not-active", ("player", name)));
            return;
        }

        bool enabled;
        switch (args.Length > 2 ? args[2].ToLowerInvariant() : null)
        {
            case null:
                enabled = !entry.AdminFun;
                break;
            case "on" or "true" or "1":
                enabled = true;
                break;
            case "off" or "false" or "0":
                enabled = false;
                break;
            default:
                shell.WriteLine(Help);
                return;
        }

        _chat.SetSootProtocolAdminFun(user, enabled);

        var admin = shell.Player?.Name ?? Loc.GetString("cmd-sootcontingencyprotocol-server");
        var state = Loc.GetString(enabled ? "cmd-sootcontingencyprotocol-adminfun-on" : "cmd-sootcontingencyprotocol-adminfun-off");

        _chat.SendAdminAnnouncement(Loc.GetString("cmd-sootcontingencyprotocol-admin-adminfun",
            ("admin", admin), ("player", name), ("state", state)));
        _adminLogger.Add(LogType.AdminCommands, LogImpact.Medium,
            $"{admin} turned Soot Contingency Protocol adminfun {(enabled ? "on" : "off")} for {name} ({user})");

        shell.WriteLine(Loc.GetString("cmd-sootcontingencyprotocol-adminfun", ("player", name), ("state", state)));
    }

    private void Status(IConsoleShell shell, NetUserId user, string name)
    {
        if (!_chat.TryGetSootProtocol(user, out var entry))
        {
            shell.WriteLine(Loc.GetString("cmd-sootcontingencyprotocol-not-active", ("player", name)));
            return;
        }

        shell.WriteLine(Describe(entry));
    }

    private void List(IConsoleShell shell)
    {
        var entries = _chat.SootProtocolEntries;
        if (entries.Count == 0)
        {
            shell.WriteLine(Loc.GetString("cmd-sootcontingencyprotocol-list-empty"));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-sootcontingencyprotocol-list-header", ("count", entries.Count)));
        foreach (var entry in entries.Values.OrderBy(e => e.Started))
        {
            shell.WriteLine(Describe(entry));
        }
    }

    private string Describe(SootProtocolEntry entry)
    {
        var remaining = entry.Expires is { } expires
            ? Loc.GetString("cmd-sootcontingencyprotocol-minutes",
                ("minutes", Math.Max(1, (int) Math.Ceiling((expires - DateTime.UtcNow).TotalMinutes))))
            : Loc.GetString("cmd-sootcontingencyprotocol-permanent");

        return Loc.GetString("cmd-sootcontingencyprotocol-entry",
            ("player", entry.Name),
            ("admin", entry.Admin),
            ("remaining", remaining),
            ("reason", entry.Reason ?? Loc.GetString("cmd-sootcontingencyprotocol-no-reason")),
            ("adminfun", entry.AdminFun ? "true" : "false"));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(new CompletionOption[]
            {
                new("activate", Loc.GetString("cmd-sootcontingencyprotocol-hint-activate")),
                new("deactivate", Loc.GetString("cmd-sootcontingencyprotocol-hint-deactivate")),
                new("status", Loc.GetString("cmd-sootcontingencyprotocol-hint-status")),
                new("list", Loc.GetString("cmd-sootcontingencyprotocol-hint-list")),
                new("adminfun", Loc.GetString("cmd-sootcontingencyprotocol-hint-adminfun")),
            }, Loc.GetString("cmd-sootcontingencyprotocol-hint-mode"));
        }

        var mode = args[0].ToLowerInvariant();
        if (mode == "list")
            return CompletionResult.Empty;

        if (args.Length == 2)
        {
            var names = mode is "deactivate" or "deactive" or "off" or "adminfun"
                ? _chat.SootProtocolEntries.Values.Select(e => e.Name)
                : _playerManager.Sessions.Select(s => s.Name);

            return CompletionResult.FromHintOptions(names.OrderBy(n => n).ToArray(),
                Loc.GetString("cmd-sootcontingencyprotocol-hint-player"));
        }

        if (mode == "adminfun")
        {
            return args.Length == 3
                ? CompletionResult.FromHintOptions(new[] { "on", "off" }, Loc.GetString("cmd-sootcontingencyprotocol-hint-adminfun-state"))
                : CompletionResult.Empty;
        }

        if (mode is not ("activate" or "active" or "on"))
            return CompletionResult.Empty;

        if (args.Length == 3)
        {
            return CompletionResult.FromHintOptions(new CompletionOption[]
            {
                new("0", Loc.GetString("cmd-sootcontingencyprotocol-permanent")),
                new("15", Loc.GetString("cmd-sootcontingencyprotocol-minutes", ("minutes", 15))),
                new("30", Loc.GetString("cmd-sootcontingencyprotocol-minutes", ("minutes", 30))),
                new("60", Loc.GetString("cmd-sootcontingencyprotocol-minutes", ("minutes", 60))),
            }, Loc.GetString("cmd-sootcontingencyprotocol-hint-minutes"));
        }

        return CompletionResult.FromHint(Loc.GetString("cmd-sootcontingencyprotocol-hint-reason"));
    }
}
