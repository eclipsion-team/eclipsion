using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.Territory;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// Admin control over persistent freeplay territory. Ownership survives round restarts and server restarts by
/// design, so a region left in a state nobody can play their way out of - a mining outpost stuck under a faction
/// that no longer fields anyone, or a save row for a region ID a mapper has since renamed - would otherwise stay
/// that way forever. This is the way out, without hand-editing capture_regions.json on a live server.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class TerritoryCommand : IConsoleCommand
{
    public string Command => "territory";
    public string Description => "Inspects and overrides ownership of persistent capture regions.";

    public string Help =>
        "Usage: territory <list|set|clear|forget|level|capability|name|profile|reset|resetfaction|resetall>\n" +
        "Every gamemode plays its own territory campaign; everything below acts on this round's, except resetall.\n" +
        "  list                          - every region in the save or on the current map\n" +
        "  set <regionId> <faction>      - hand a region to one of the campaign's factions\n" +
        "  clear <regionId>              - return a region to nobody\n" +
        "  forget <regionId>             - drop the save row, so the map's own owner applies next load\n" +
        "  level <regionId> <level>      - set a region's development level, discarding deposits toward the next\n" +
        "  capability <regionId> <id>    - retool a region's production, or 'none'\n" +
        "  name <regionId> [name]        - name a held region as its holder would (DSM-name), or reset it\n" +
        "  profile                       - this round's campaign, who fights over it, and every campaign on disk\n" +
        "  reset <regionId>              - start a region over: no holder or name, level 1, no line, and its\n" +
        "                                  persistent grid back to the map file next round\n" +
        "  resetfaction <faction>        - take every region a faction holds off it (levels stay with the ground)\n" +
        "  resetall <campaign>           - reset every region of a campaign; type its name to confirm. Its files\n" +
        "                                  are set aside under territories/, not deleted";

    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private IEnumerable<string> Factions
    {
        get
        {
            var profiles = _systems.GetEntitySystem<TerritoryProfileSystem>();
            return PersistentTerritoryFactions.Supported
                .Where(profiles.IsAllowed)
                .Select(FactionDisplay.Abbreviation);
        }
    }

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteLine(Help);
            return;
        }

        var territory = _systems.GetEntitySystem<PersistentCaptureRegionSystem>();

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                ListRegions(shell, territory);
                return;

            case "set":
                if (args.Length < 3)
                {
                    shell.WriteError("Usage: territory set <regionId> <DSM|NCWL|TFCF|SHI>");
                    return;
                }

                if (!territory.SetOwner(args[1], args[2]))
                {
                    shell.WriteError($"'{args[2]}' cannot hold territory in campaign " +
                                     $"'{_systems.GetEntitySystem<TerritoryProfileSystem>().Current}'. " +
                                     $"Use one of: {string.Join(", ", Factions)}.");
                    return;
                }

                shell.WriteLine($"Region '{args[1]}' is now held by {FactionDisplay.Abbreviation(FactionDisplay.ResolveId(args[2].Trim())).ToUpperInvariant()}.");
                return;

            case "clear":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory clear <regionId>");
                    return;
                }

                territory.SetOwner(args[1], null);
                shell.WriteLine($"Region '{args[1]}' is now unclaimed.");
                return;

            case "forget":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory forget <regionId>");
                    return;
                }

                shell.WriteLine(territory.ForgetRegion(args[1])
                    ? $"Region '{args[1]}' dropped from the save."
                    : $"Region '{args[1]}' was not in the save.");
                return;

            case "level":
                if (args.Length < 3 || !int.TryParse(args[2], out var level))
                {
                    shell.WriteError("Usage: territory level <regionId> <level>");
                    return;
                }

                var progression = _systems.GetEntitySystem<PersistentPoiProgressionSystem>();
                progression.SetLevel(args[1], level);
                shell.WriteLine($"Region '{args[1]}' is now level {progression.GetLevel(args[1])}.");
                return;

            case "capability":
                if (args.Length < 3)
                {
                    shell.WriteError("Usage: territory capability <regionId> <capability|none>");
                    return;
                }

                var capability = args[2].Equals("none", StringComparison.OrdinalIgnoreCase) ? null : args[2];
                if (!_systems.GetEntitySystem<PersistentPoiProgressionSystem>().SetCapability(args[1], capability))
                {
                    shell.WriteError($"'{args[2]}' is not a production capability. Use one of: {string.Join(", ", Capabilities)}, none.");
                    return;
                }

                shell.WriteLine($"Region '{args[1]}' is now tooled for {capability ?? "nothing"}.");
                return;

            case "name":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory name <regionId> [name]");
                    return;
                }

                var name = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;
                if (!territory.TrySetCustomName(args[1], name, out var error))
                {
                    shell.WriteError(Loc.GetString(error));
                    return;
                }

                shell.WriteLine(name == null
                    ? $"Region '{args[1]}' goes by its own name again."
                    : $"Region '{args[1]}' renamed.");
                return;

            case "profile":
                ShowProfiles(shell);
                return;

            case "reset":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory reset <regionId>");
                    return;
                }

                ResetRegion(shell, territory, args[1]);
                return;

            case "resetfaction":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory resetfaction <faction>");
                    return;
                }

                var taken = territory.ClearFaction(args[1]);
                shell.WriteLine(taken.Count == 0
                    ? $"{args[1].ToUpperInvariant()} holds no territory in this campaign."
                    : $"{args[1].ToUpperInvariant()} lost {taken.Count} region(s): {string.Join(", ", taken)}.");
                return;

            case "resetall":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory resetall <campaign> - type the campaign's name to confirm. " +
                                     "See 'territory profile'.");
                    return;
                }

                ResetCampaign(shell, territory, args[1]);
                return;

            default:
                shell.WriteLine(Help);
                return;
        }
    }

    private IEnumerable<string> Capabilities =>
        _proto.EnumeratePrototypes<PersistentPoiCapabilityPrototype>().Select(c => c.ID).OrderBy(id => id);

    private void ShowProfiles(IConsoleShell shell)
    {
        var profiles = _systems.GetEntitySystem<TerritoryProfileSystem>();
        shell.WriteLine($"This round's campaign: {profiles.Current}, fought over by {string.Join(", ", Factions)}.");
        shell.WriteLine($"Campaigns on disk: {string.Join(", ", profiles.KnownProfiles())}.");
    }

    private void ResetRegion(IConsoleShell shell, PersistentCaptureRegionSystem territory, string regionId)
    {
        var progression = _systems.GetEntitySystem<PersistentPoiProgressionSystem>();
        var saves = _systems.GetEntitySystem<PersistentPoiSaveSystem>();

        var key = saves.FindKey(regionId);
        var known = territory.ResetRegion(regionId);
        known |= progression.ResetProgress(regionId);
        var grid = key != null && saves.Reset(key);

        if (!known && !grid)
        {
            shell.WriteError($"Campaign '{_systems.GetEntitySystem<TerritoryProfileSystem>().Current}' knows no region '{regionId}'.");
            return;
        }

        shell.WriteLine($"Region '{regionId}' starts over: unclaimed, level 1, no line of production." + (grid
            ? $" Its grid ({key}) goes back to the map file next round; this round is not saved over it."
            : string.Empty));
    }

    private void ResetCampaign(IConsoleShell shell, PersistentCaptureRegionSystem territory, string campaign)
    {
        var profiles = _systems.GetEntitySystem<TerritoryProfileSystem>();
        var name = TerritoryProfileSystem.Sanitize(campaign);

        if (!string.Equals(name, profiles.Current, StringComparison.OrdinalIgnoreCase))
        {
            shell.WriteLine(profiles.ArchiveProfile(name)
                ? $"Campaign '{name}' set aside; that gamemode starts its territory over the next time it is played."
                : $"Campaign '{name}' has no files. See 'territory profile'.");
            return;
        }

        profiles.ArchiveProfile(name);
        var regions = territory.ResetAll();
        _systems.GetEntitySystem<PersistentPoiProgressionSystem>().ResetAllProgress();
        var grids = _systems.GetEntitySystem<PersistentPoiSaveSystem>().ResetAll();

        shell.WriteLine($"Campaign '{name}' starts over: {regions} region(s) unclaimed at level 1, {grids} persistent " +
                        "grid(s) back to their map files next round. This round is not saved over them.");
    }

    private void ListRegions(IConsoleShell shell, PersistentCaptureRegionSystem territory)
    {
        var progression = _systems.GetEntitySystem<PersistentPoiProgressionSystem>();

        shell.WriteLine($"Campaign {_systems.GetEntitySystem<TerritoryProfileSystem>().Current}:");
        var regions = territory.GetRegions();
        if (regions.Count == 0)
        {
            shell.WriteLine("No persistent capture regions are saved or loaded.");
            return;
        }

        foreach (var region in regions)
        {
            var owner = FactionDisplay.Abbreviation(region.Owner);
            if (owner.Length == 0)
                owner = "-";

            shell.WriteLine(
                $"{region.RegionId,-28} {owner,-6} " +
                $"{(region.Loaded ? "loaded" : "saved "),-7} " +
                $"L{progression.GetLevel(region.RegionId)} {progression.GetCapability(region.RegionId) ?? "-",-14} {region.Name}");
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                ["list", "set", "clear", "forget", "level", "capability", "name", "profile", "reset", "resetfaction", "resetall"],
                "action");
        }

        var action = args[0].ToLowerInvariant();

        if (args.Length == 2 && action == "resetall")
        {
            return CompletionResult.FromHintOptions(
                _systems.GetEntitySystem<TerritoryProfileSystem>().KnownProfiles(), "campaign (type it to confirm)");
        }

        if (args.Length == 2 && action == "resetfaction")
            return CompletionResult.FromHintOptions(Factions, "faction");

        if (args.Length == 3 && action == "capability")
            return CompletionResult.FromHintOptions(Capabilities.Append("none"), "capability");

        if (args.Length == 3 && action == "level")
            return CompletionResult.FromHint("level");

        if (args.Length == 2 && action is "set" or "clear" or "forget" or "level" or "capability" or "name" or "reset")
        {
            var regions = _systems.GetEntitySystem<PersistentCaptureRegionSystem>()
                .GetRegions()
                .Select(region => region.RegionId);

            return CompletionResult.FromHintOptions(regions, "region ID");
        }

        if (args.Length == 3 && action == "set")
            return CompletionResult.FromHintOptions(Factions, "faction");

        return CompletionResult.Empty;
    }
}
