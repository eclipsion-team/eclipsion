using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Crescent.Territory;

[AdminCommand(AdminFlags.Server)]
public sealed class PersistentPoiCommand : IConsoleCommand
{
    public string Command => "persistentpoi";
    public string Description => "Inspects and resets the saved state of persistent territories.";

    public string Help =>
        "Usage: persistentpoi <list|reset|rollback>\n" +
        "  list            - every territory with a save, and whether it is loaded this round\n" +
        "  reset <key>     - set the save aside, so the territory starts over from its map file next round\n" +
        "  rollback <key>  - put the previous round's save back in place of the latest one\n" +
        "A territory is keyed by the gameMap it spawns from. Changes take effect when the territory next loads. A\n" +
        "territory loaded right now plays on unchanged, but its round is not saved over the result when it ends.";

    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteLine(Help);
            return;
        }

        var saves = _systems.GetEntitySystem<PersistentPoiSaveSystem>();

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                List(shell, saves);
                return;

            case "reset":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: persistentpoi reset <key>");
                    return;
                }

                shell.WriteLine(saves.Reset(args[1])
                    ? $"Territory '{args[1]}' will start over from its map file the next time it loads."
                    : $"Territory '{args[1]}' has no save.");
                return;

            case "rollback":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: persistentpoi rollback <key>");
                    return;
                }

                shell.WriteLine(saves.Rollback(args[1])
                    ? $"Territory '{args[1]}' will load from the previous round's save the next time it loads."
                    : $"Territory '{args[1]}' has no previous save to roll back to.");
                return;

            default:
                shell.WriteLine(Help);
                return;
        }
    }

    private static void List(IConsoleShell shell, PersistentPoiSaveSystem saves)
    {
        var rows = saves.GetStatus();
        if (rows.Count == 0)
        {
            shell.WriteLine("No persistent territory is saved or loaded.");
            return;
        }

        foreach (var (key, entry, loaded) in rows)
        {
            var saved = entry == null
                ? "no save yet"
                : $"v{entry.MapVersion}, round {entry.RoundId}, {entry.SavedAt:yyyy-MM-dd HH:mm} UTC";

            var held = saves.IsHeld(key) ? " (this round will not be saved)" : string.Empty;
            shell.WriteLine($"{key,-32} {(loaded ? "loaded" : "-"),-7} {saved}{held}");
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(["list", "reset", "rollback"], "action");

        if (args.Length == 2 && args[0].ToLowerInvariant() is "reset" or "rollback")
        {
            var keys = _systems.GetEntitySystem<PersistentPoiSaveSystem>()
                .GetStatus()
                .Select(row => row.Key);

            return CompletionResult.FromHintOptions(keys, "territory");
        }

        return CompletionResult.Empty;
    }
}
