namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// The Great Altar — the King of the Hill objective of the Great Hunt. One of <see cref="AllowedFactions"/> claims it by
/// clicking it and standing still for <see cref="CaptureTime"/> seconds; taking damage or moving cancels the attempt
/// with nothing kept, same feel as a conquest banner. The altar only records who holds it and since when — the hold
/// clock and the win itself live on <see cref="GreatHuntRuleComponent"/>, so a mapped altar outside the Great Hunt
/// is harmless.
/// </summary>
[RegisterComponent]
public sealed partial class GreatAltarComponent : Component
{
    /// <summary>Factions that may claim the altar. Anyone else gets a refusal popup.</summary>
    [DataField]
    public List<string> AllowedFactions = new() { "TAP", "SRM" };

    /// <summary>Seconds the claimant must stand at the altar after clicking it.</summary>
    [DataField]
    public float CaptureTime = 20f;

    /// <summary>How far the claimant may be from the altar before the attempt breaks, in tiles.</summary>
    [DataField]
    public float CaptureRange = 2f;

    /// <summary>Seconds after map init before the altar can be claimed at all. 0 = claimable immediately.</summary>
    [DataField]
    public float GracePeriod;

    /// <summary>Absolute time the grace period ends, baked from <see cref="GracePeriod"/> at map init. VV to adjust.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan UnlockTime;

    /// <summary>Faction currently holding the altar. Null while nobody has claimed it.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public string? HolderFaction;

    /// <summary>When <see cref="HolderFaction"/> took the altar. The hold clock counts from here.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan HeldSince;

    /// <summary>Set once the hunt is decided, so nobody can flip the altar during the restart countdown.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public bool Locked;

    /// <summary>Faction of the last mob to start a claim, for examine feedback. Transient.</summary>
    [ViewVariables]
    public string? ContestingFaction;

    /// <summary>When the in-progress claim would finish; examine only reports a contest before this time.</summary>
    [ViewVariables]
    public TimeSpan ContestUntil;

    /// <summary>
    /// Prayer lines (locale IDs) per faction, spoken aloud in order by whoever is performing the claim rite. The
    /// last line repeats if the rite outlasts the list.
    /// </summary>
    [DataField]
    public Dictionary<string, List<string>> ChantLines = new();

    /// <summary>Seconds between prayer lines during the claim rite.</summary>
    [DataField]
    public float ChantInterval = 4f;

    /// <summary>Everyone currently performing the claim rite at this altar. Transient.</summary>
    [ViewVariables]
    public Dictionary<EntityUid, GreatAltarChant> Chanters = new();

    /// <summary>Altar light colour per holding faction.</summary>
    [DataField]
    public Dictionary<string, Color> FactionColors = new();

    /// <summary>Altar light colour while nobody holds it.</summary>
    [DataField]
    public Color NeutralColor = Color.White;
}

/// <summary>One claimant's progress through their faction's prayer during the claim rite.</summary>
public sealed class GreatAltarChant
{
    public string Faction = string.Empty;
    public int NextLine;
    public TimeSpan NextLineAt;

    /// <summary>When the rite would finish; a chanter still listed past this (gibbed mid-rite) is dropped.</summary>
    public TimeSpan EndsAt;
}

/// <summary>
/// Broadcast whenever a Great Altar changes hands. <see cref="PreviousFaction"/> is null on the first claim.
/// </summary>
[ByRefEvent]
public readonly record struct GreatAltarCapturedEvent(EntityUid Altar, string Faction, string? PreviousFaction);
