using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Crescent.Commands;

/// <summary>
/// The curated list of commands this fork adds, and the printing of it.
///
/// Vanilla <c>help</c> lists every command the engine and content have between them, several hundred of them,
/// with nothing marking which handful are ours. Admins were finding these by word of mouth. The names below are
/// curated; the descriptions are read back out of the console host, so they cannot drift from the commands.
/// </summary>
public static class HullrotHelpListing
{
    /// <summary>
    /// One listed command. A bare name is a server command: it is only printed if something is actually
    /// registered under it, so deleting a command does not leave a lie in this list. A name paired with a
    /// description is a client-side command — the server's console host has never heard of those, so they would
    /// silently vanish from the list without a description of their own to fall back on.
    /// </summary>
    private readonly record struct Entry(string Name, string? ClientDescription = null)
    {
        public static implicit operator Entry(string name) => new(name);
    }

    /// <summary>
    /// Add new fork commands here. Grouped by the job an admin is doing, not by which folder the code lives in —
    /// nobody looking for "how do I stop the round ending" knows it lives in _Crescent/RoundEnd.
    /// </summary>
    private static readonly (string Category, Entry[] Commands)[] Categories =
    {
        ("Round & objectives", new Entry[]
        {
            "roundtimer",
            "unionfall_skipgrace",
            "greathunt_skipgrace",
            "planetfall_releasebarrier",
        }),
        ("Diplomacy & factions", new Entry[]
        {
            "resetdiplomacy",
            "getfactionrelations",
            "changefactionrelations",
            "changeifffaction",
        }),
        ("Economy", new Entry[]
        {
            "stockmarket",
        }),
        ("Ships", new Entry[]
        {
            "shieldentity",
            "unshieldentity",
            "repairgrid",
            "snapshotgrid",
            "pc_genranges",
            new("pc_showranges", "Toggles the cannon safety-range overlay."),
        }),
        ("World", new Entry[]
        {
            "sb_genchunks",
        }),
        ("Atmosphere & flavour", new Entry[]
        {
            "adminvoice",
            "gridmusic",
            "gridflash",
            "dnadb",
        }),
    };

    public static void Write(IConsoleShell shell, IConsoleHost conHost, string? filter)
    {
        var found = false;

        foreach (var (category, commands) in Categories)
        {
            var matches = commands
                .Select(entry => (entry.Name, Description: Describe(conHost, entry)))
                .Where(m => m.Description != null
                            && (filter == null
                                || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || m.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
                continue;

            shell.WriteLine($"== {category} ==");
            foreach (var (name, description) in matches)
            {
                shell.WriteLine($"  {name,-26} {description}");
                found = true;
            }
        }

        if (!found)
        {
            shell.WriteLine(filter == null
                ? "No Hullrot commands are registered."
                : $"No Hullrot command matches '{filter}'.");
            return;
        }

        shell.WriteLine("");
        shell.WriteLine("Use 'help <command>' for full usage of any of these.");
    }

    public static CompletionResult Complete(IConsoleHost conHost, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        return CompletionResult.FromHintOptions(
            Categories.SelectMany(c => c.Commands)
                .Where(e => Describe(conHost, e) != null)
                .Select(e => e.Name),
            "search term");
    }

    /// <summary>
    /// The description to print for an entry, or null if it should be dropped: a server command nothing is
    /// registered under no longer exists and must not be advertised.
    /// </summary>
    private static string? Describe(IConsoleHost conHost, Entry entry)
    {
        if (conHost.AvailableCommands.TryGetValue(entry.Name, out var command))
            return command.Description;

        return entry.ClientDescription is { } clientDescription
            ? $"[client] {clientDescription}"
            : null;
    }
}

/// <summary>
/// Prints <see cref="HullrotHelpListing"/>. Exists as a base class only because the command is registered under
/// two names; the console host wants a distinct type per name, and neither should be able to drift from the other.
/// </summary>
public abstract class HullrotHelpCommandBase : IConsoleCommand
{
    public abstract string Command { get; }
    public abstract string Description { get; }
    public abstract string Help { get; }

    // Injected on the derived types too: IoC walks the whole class hierarchy for [Dependency] fields.
    [Dependency] private readonly IConsoleHost _conHost = default!;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError($"Expected at most one search term.\n{Help}");
            return;
        }

        HullrotHelpListing.Write(shell, _conHost, args.Length > 0 ? args[0] : null);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return HullrotHelpListing.Complete(_conHost, args);
    }
}

/// <summary>
/// Lists the commands this fork adds, grouped by what they are for.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class HullrotHelpCommand : HullrotHelpCommandBase
{
    public const string CommandName = "hullrothelp";

    public override string Command => CommandName;
    public override string Description => "Lists the Hullrot-specific admin commands.";

    public override string Help => $"""
        Usage: {CommandName} [search term]

        With no argument, lists every command this fork adds on top of vanilla SS14.
        With one, keeps only the entries whose name or description contains it.
        """;
}

/// <summary>
/// The old name for <see cref="HullrotHelpCommand"/>, kept registered so anything that already points admins at
/// <c>crescenthelp</c> — notes, pinned messages, muscle memory — keeps working.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class CrescentHelpCommand : HullrotHelpCommandBase
{
    public override string Command => "crescenthelp";
    public override string Description => $"Alias of '{HullrotHelpCommand.CommandName}'. Lists the Hullrot-specific admin commands.";
    public override string Help => $"Usage: {Command} [search term]\nSee 'help {HullrotHelpCommand.CommandName}'.";
}
