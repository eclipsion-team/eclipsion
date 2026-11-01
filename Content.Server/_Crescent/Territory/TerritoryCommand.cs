using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.Territory;
using Content.Shared.Stacks;
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
        "Usage: territory <list|set|clear|forget|level|capability|name|silo|fund|profile|reset|resetfaction|resetall>\n" +
        "Every gamemode plays its own territory campaign; everything below acts on this round's, except resetall.\n" +
        "A <regionId> of 'here' means the territory you are standing on.\n" +
        "  list                          - every region in the save or on the current map\n" +
        "  set <regionId> <faction>      - hand a region to one of the campaign's factions\n" +
        "  clear <regionId>              - return a region to nobody\n" +
        "  forget <regionId>             - drop the save row, so the map's own owner applies next load\n" +
        "  level <regionId> <level>      - set a region's development level, discarding deposits toward the next\n" +
        "  capability <regionId> <id>    - retool a region's production, or 'none'\n" +
        "  name <regionId> [name]        - name a held region as its holder would (DSM-name), or reset it\n" +
        "  silo <regionId> [show]        - what the region's material silo holds\n" +
        "  silo <regionId> fill [amount] - top every material the silo takes up to amount (default 1000)\n" +
        "  silo <regionId> add <material> <amount> - put one material in (a stack ID such as Steel or Plasma)\n" +
        "  silo <regionId> clear         - empty the silo\n" +
        "  fund <regionId>               - deposit everything the region's next level asks for\n" +
        "  profile                       - this round's campaign, who fights over it, and every campaign on disk\n" +
        "  reset <regionId>              - start a region over: no holder or name, level 1, no line, and its\n" +
        "                                  persistent grid back to the map file next round\n" +
        "  resetfaction <faction>        - take every region a faction holds off it (levels stay with the ground)\n" +
        "  resetall <campaign>           - reset every region of a campaign; type its name to confirm. Its files\n" +
        "                                  are set aside under territories/, not deleted";

    [Dependency] private readonly IEntityManager _entMan = default!;
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

        // the rest take a region; 'here' is the one under the admin's feet
        if (args.Length > 1 && args[1].Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            if (FindHere(shell) is not { } here)
            {
                shell.WriteError("You are not standing on a territory.");
                return;
            }

            args[1] = here;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "silo":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory silo <regionId> [show|fill [amount]|add <material> <amount>|clear]");
                    return;
                }

                Silo(shell, args);
                return;

            case "fund":
                if (args.Length < 2)
                {
                    shell.WriteError("Usage: territory fund <regionId>");
                    return;
                }

                shell.WriteLine(_systems.GetEntitySystem<PersistentPoiProgressionSystem>().FundNextLevel(args[1])
                    ? $"Region '{args[1]}' has everything its next level asks for. Raise it at its administration console, or with 'territory level'."
                    : $"Region '{args[1]}' is already fully developed.");
                return;

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

    private string? FindHere(IConsoleShell shell)
    {
        if (shell.Player?.AttachedEntity is not { } admin ||
            _entMan.GetComponent<TransformComponent>(admin).GridUid is not { } grid ||
            !_entMan.TryGetComponent(grid, out PersistentPoiGridComponent? poi))
        {
            return null;
        }

        return poi.RegionId;
    }

    private void Silo(IConsoleShell shell, string[] args)
    {
        var silos = _systems.GetEntitySystem<PersistentPoiSiloSystem>();

        EntityUid? grid = null;
        var query = _entMan.EntityQueryEnumerator<PersistentPoiGridComponent>();
        while (query.MoveNext(out var uid, out var poi))
        {
            if (string.Equals(poi.RegionId, args[1].Trim(), StringComparison.OrdinalIgnoreCase))
                grid = uid;
        }

        if (grid == null)
        {
            shell.WriteError($"Region '{args[1]}' is not in the sector this round.");
            return;
        }

        if (!PersistentPoiSilo.TryGetSilo(_entMan, grid.Value, out var silo))
        {
            shell.WriteError($"Region '{args[1]}' has no material silo. Build one, or spawn PersistentPoiMaterialSilo there.");
            return;
        }

        var action = args.Length > 2 ? args[2].ToLowerInvariant() : "show";
        switch (action)
        {
            case "show":
                break;

            case "fill":
                var amount = 1000;
                if (args.Length > 3 && (!int.TryParse(args[3], out amount) || amount <= 0))
                {
                    shell.WriteError("The amount must be a positive whole number.");
                    return;
                }

                foreach (var material in silos.AcceptedMaterials)
                {
                    silo.Comp.Stored[material] = Math.Max(silo.Comp.Stored.GetValueOrDefault(material), amount);
                }

                _entMan.Dirty(silo);
                break;

            case "add":
                if (args.Length < 5 || !int.TryParse(args[4], out var count) || count == 0)
                {
                    shell.WriteError("Usage: territory silo <regionId> add <material> <amount>");
                    return;
                }

                if (!_proto.HasIndex<StackPrototype>(args[3]))
                {
                    shell.WriteError($"'{args[3]}' is not a stack. The silo takes: {string.Join(", ", silos.AcceptedMaterials.Select(m => m.Id).OrderBy(m => m))}.");
                    return;
                }

                var total = Math.Max(0, silo.Comp.Stored.GetValueOrDefault(args[3]) + count);
                if (total > 0)
                    silo.Comp.Stored[args[3]] = total;
                else
                    silo.Comp.Stored.Remove(args[3]);

                _entMan.Dirty(silo);
                break;

            case "clear":
                silo.Comp.Stored.Clear();
                _entMan.Dirty(silo);
                break;

            default:
                shell.WriteError("Usage: territory silo <regionId> [show|fill [amount]|add <material> <amount>|clear]");
                return;
        }

        if (silo.Comp.Stored.Count == 0)
        {
            shell.WriteLine($"The silo of '{args[1]}' is empty.");
            return;
        }

        shell.WriteLine($"The silo of '{args[1]}' holds: " +
                        string.Join(", ", silo.Comp.Stored.OrderBy(pair => pair.Key.Id).Select(pair => $"{pair.Value} {pair.Key.Id}")));
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
                ["list", "set", "clear", "forget", "level", "capability", "name", "silo", "fund", "profile", "reset", "resetfaction", "resetall"],
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

        if (args.Length == 2 && action is "set" or "clear" or "forget" or "level" or "capability" or "name" or "reset" or "silo" or "fund")
        {
            var regions = _systems.GetEntitySystem<PersistentCaptureRegionSystem>()
                .GetRegions()
                .Select(region => region.RegionId)
                .Prepend("here");

            return CompletionResult.FromHintOptions(regions, "region ID");
        }

        if (args.Length == 3 && action == "set")
            return CompletionResult.FromHintOptions(Factions, "faction");

        if (args.Length == 3 && action == "silo")
            return CompletionResult.FromHintOptions(["show", "fill", "add", "clear"], "silo action");

        if (args.Length == 4 && action == "silo" && args[2].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            return CompletionResult.FromHintOptions(
                _systems.GetEntitySystem<PersistentPoiSiloSystem>().AcceptedMaterials.Select(m => m.Id).OrderBy(m => m),
                "material");
        }

        if (args.Length == 4 && action == "silo" && args[2].Equals("fill", StringComparison.OrdinalIgnoreCase))
            return CompletionResult.FromHint("amount (default 1000)");

        return CompletionResult.Empty;
    }
}
