using System.Linq;
using System.Text;
using Content.Shared._Crescent.Territory;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryProfileSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;

    public const string DefaultProfile = "Default";

    private static readonly ResPath Root = new("/territories");

    public string Current { get; private set; } = DefaultProfile;

    public IReadOnlySet<string>? Factions { get; private set; }

    public void SetProfile(string profile, IEnumerable<string>? factions)
    {
        profile = Sanitize(profile);

        var allowed = factions?
            .Where(PersistentTerritoryFactions.IsSupported)
            .ToHashSet(StringComparer.Ordinal);
        Factions = allowed is { Count: > 0 } ? allowed : null;

        if (profile == Current)
            return;

        Current = profile;
        Log.Info($"Territory campaign is now '{profile}' " +
                 $"({(Factions == null ? "all factions" : string.Join(", ", Factions))}).");

        var ev = new TerritoryProfileChangedEvent(profile);
        RaiseLocalEvent(ref ev);
    }

    public bool IsAllowed(string? faction)
    {
        // independents have no home station, so no campaign lists them, and every campaign has them
        return PersistentTerritoryFactions.IsIndependent(faction) ||
               PersistentTerritoryFactions.IsSupported(faction) && (Factions == null || Factions.Contains(faction!));
    }

    public ResPath DirectoryOf(string profile) => Root / Sanitize(profile);

    public ResPath PathFor(string file) => DirectoryOf(Current) / file;

    public List<string> KnownProfiles()
    {
        var profiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { Current };
        if (_resources.UserData.IsDir(Root))
        {
            foreach (var entry in _resources.UserData.DirectoryEntries(Root))
            {
                var name = entry.TrimEnd('/');
                if (name.Length > 0 && !name.Contains('.') && _resources.UserData.IsDir(Root / name))
                    profiles.Add(name);
            }
        }

        return profiles.ToList();
    }

    public bool ArchiveProfile(string profile)
    {
        var directory = DirectoryOf(profile);
        if (!_resources.UserData.IsDir(directory))
            return false;

        var archived = Root / $"{Sanitize(profile)}.reset-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        MoveTree(directory, archived);
        _resources.UserData.Delete(directory);
        Log.Info($"Territory campaign '{profile}' set aside as {archived}.");
        return true;
    }

    private void MoveTree(ResPath from, ResPath to)
    {
        var userData = _resources.UserData;
        userData.CreateDir(to);

        foreach (var entry in userData.DirectoryEntries(from).ToList())
        {
            var name = entry.Replace('\\', '/').TrimEnd('/');
            if (userData.IsDir(from / name))
                MoveTree(from / name, to / name);
            else
                userData.Rename(from / name, to / name);
        }
    }

    public static string Sanitize(string profile)
    {
        var builder = new StringBuilder(profile.Length);
        foreach (var c in profile.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                builder.Append(c);
        }

        return builder.Length > 0 ? builder.ToString() : DefaultProfile;
    }
}

[ByRefEvent]
public readonly record struct TerritoryProfileChangedEvent(string Profile);
