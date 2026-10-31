using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.CaptureFlag;
using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.Territory;

/// <summary>
/// The major powers allowed to participate in persistent territory control. The faction prototype still uses the
/// legacy <c>TFSC</c> ID for the Taypani Free Companies Federation, which is shown to players as TFCF through
/// <see cref="FactionDisplay"/>.
/// </summary>
public static class PersistentTerritoryFactions
{
    private static readonly string[] Ids = ["DSM", "NCWL", "TFSC", "SHI"];

    /// <summary>
    /// Every faction that may hold territory, in a fixed order suitable for admin console hints and help text.
    /// </summary>
    public static IReadOnlyList<string> Supported => Ids;

    public static bool IsSupported(string? faction)
    {
        return faction is "DSM" or "NCWL" or "TFSC" or "SHI";
    }

    public static string? GetTeam(IEntityManager entMan, EntityUid uid)
    {
        if (entMan.TryGetComponent(uid, out CaptureTeamComponent? captureTeam) &&
            !string.IsNullOrWhiteSpace(captureTeam.Team))
        {
            return captureTeam.Team;
        }

        if (entMan.TryGetComponent(uid, out HullrotFactionComponent? faction) &&
            !string.IsNullOrWhiteSpace(faction.Faction))
        {
            return faction.Faction;
        }

        return null;
    }

    public static bool Holds(IEntityManager entMan, EntityUid user, PersistentPoiGridComponent territory)
    {
        return territory.Owner != null &&
               string.Equals(GetTeam(entMan, user), territory.Owner, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes an owner prefix from a radar name, whichever spelling wrote it. Names saved or mapped before a
    /// faction's abbreviation changed still carry the old one, so both have to be recognized or the prefix stacks.
    /// </summary>
    public static string StripOwnerPrefix(string name)
    {
        foreach (var faction in Ids)
        {
            foreach (var spelling in FactionDisplay.Spellings(faction))
            {
                var prefix = $"{spelling} ";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return name[prefix.Length..];
            }
        }

        return name;
    }
}

/// <summary>
/// Turns a <see cref="Content.Shared.CaptureFlag.CaptureFlagComponent"/> into a manually captured freeplay territory
/// whose owner survives round and server restarts. The flag must sit on the grid that represents the territory on
/// radar.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PersistentCaptureRegionComponent : Component
{
    /// <summary>
    /// Stable save key. It must remain unique across every persistent region and must not be changed when its map is
    /// renamed, moved or remapped.
    /// </summary>
    [DataField(required: true)]
    public string RegionId = string.Empty;

    /// <summary>
    /// Faction-independent radar name. Ownership is displayed as "FACTION BaseName".
    /// If omitted, the grid's name at first application is used.
    /// </summary>
    [DataField]
    public string? BaseName;

    /// <summary>
    /// Preferred radar colours for factions that capture this region. Missing factions use their faction prototype's
    /// button colour.
    /// </summary>
    [DataField]
    public Dictionary<string, Color> FactionColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DSM"] = Color.FromHex("#C677A5"),
        ["NCWL"] = Color.Orange,
        ["SHI"] = Color.White,
        ["TFSC"] = Color.IndianRed,
    };

    /// <summary>Radar colour used while nobody owns the region.</summary>
    [DataField]
    public Color NeutralColor = Color.Gold;

    /// <summary>Flag sprite states keyed by faction ID.</summary>
    [DataField, AutoNetworkedField]
    public Dictionary<string, string> TeamStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether holding this region periodically benefits the owner's listed faction stock.</summary>
    [DataField]
    public bool StockRewardEnabled = true;

    /// <summary>Seconds between passive stock rewards. Ten minutes keeps one region's effect deliberately small.</summary>
    [DataField]
    public float StockRewardInterval = 600f;

    /// <summary>
    /// Relative stock pressure per reward. 0.001 is +0.1% raw pressure before the market's normal rise damping.
    /// </summary>
    [DataField]
    public float StockRewardMagnitude = 0.001f;

    /// <summary>Market ticks over which one reward is applied.</summary>
    [DataField]
    public int StockRewardDuration = 4;

    [DataField]
    public float ProtectedCoreRadius = 15f;

    [DataField]
    public int LevelsLostOnNeutralize;

    /// <summary>
    /// Last owner whose territory effects were applied. Runtime-only: persisted ownership lives in the system save.
    /// </summary>
    [ViewVariables]
    public string? AppliedOwner;

    /// <summary>Next passive reward time. Runtime-only and reset when the map loads or ownership changes.</summary>
    [ViewVariables]
    public TimeSpan NextStockReward;

    /// <summary>The player currently working the standard. Runtime-only; prevents simultaneous capture attempts.</summary>
    [ViewVariables]
    public EntityUid? Capturer;

    /// <summary>Start and end times used to present the active do-after's progress when the flag is examined.</summary>
    [ViewVariables]
    public TimeSpan CaptureStartedAt;

    [ViewVariables]
    public TimeSpan CaptureEndsAt;

    [ViewVariables]
    public TimeSpan NextAttackAlert;

    /// <summary>False when map validation rejected this region ID. Runtime-only.</summary>
    [ViewVariables]
    public bool ValidRegion = true;
}

/// <summary>
/// Marks a machine or anti-boarder turret as controlled by a persistent capture region.
/// </summary>
[RegisterComponent]
public sealed partial class CaptureRegionDeviceComponent : Component
{
    /// <summary>
    /// Optional explicit region key. Empty binds the device to the persistent region flag on its current grid.
    /// Set this when a territory spans multiple grids.
    /// </summary>
    [DataField]
    public string RegionId = string.Empty;

    /// <summary>Reassign a FactionMachine component when control changes.</summary>
    [DataField]
    public bool UpdateMachineFaction = true;

    /// <summary>Replace an NpcFactionMember component's factions when control changes.</summary>
    [DataField]
    public bool UpdateNpcFaction = true;

    /// <summary>
    /// Prefix the device's name with the owning faction, so a boarder can tell whose guns they have walked into
    /// without a console in front of them.
    /// </summary>
    /// <remarks>
    /// Opt-in rather than on by default: a targeting console groups its cannons by entity name, so renaming a
    /// hardpoint-mounted gun mid-round would silently split its group away from the one the console already
    /// holds. It is safe on the capturable console and anti-boarder turret, neither of which is grouped that way.
    /// </remarks>
    [DataField]
    public bool UpdateName;

    /// <summary>
    /// The device's name with no owner prefix, captured the first time ownership is applied. Runtime-only.
    /// </summary>
    [ViewVariables]
    public string? BaseName;
}

/// <summary>
/// Gives a capturable targeting console an automatic guard mode when nobody is operating its UI.
/// </summary>
[RegisterComponent]
public sealed partial class TerritoryAutoDefenseComponent : Component
{
    /// <summary>
    /// Maximum hostile-grid acquisition distance in world tiles. It defaults to the 512-tile radar range of the
    /// console this sits on, so the automatic mode can never engage anything an operator at the same console
    /// could not see and order a shot at by hand. Raising it past what the mounted guns can actually reach only
    /// buys a wider lookup and rounds that despawn short of the target.
    /// </summary>
    [DataField]
    public float Range = 512f;

    /// <summary>
    /// The exclusion zone held against non-allied traffic that is not in a declared war. Only used when
    /// <see cref="WarOnly"/> is off: hulls inside it are engaged, hulls between it and <see cref="Range"/> get a
    /// radio warning quoting this distance.
    /// </summary>
    [DataField]
    public float NeutralEngagementRange = 384f;

    /// <summary>Seconds between target searches and console-to-cannon link refreshes.</summary>
    [DataField]
    public float ScanInterval = 1f;

    /// <summary>
    /// If true, only factions in a declared war are engaged, and everything else is ignored outright - no
    /// exclusion zone and no radio warnings, because the battery would never make good on them. Turning it off
    /// engages every non-ally that crosses <see cref="NeutralEngagementRange"/>.
    /// </summary>
    [DataField]
    public bool WarOnly = true;

    [ViewVariables]
    public EntityUid? Target;

    [ViewVariables]
    public EntityUid? TargetGrid;

    [ViewVariables]
    public TimeSpan NextScan;
}
