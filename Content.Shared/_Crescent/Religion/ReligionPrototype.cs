using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Religion;

/// <summary>
///     A faith a character can hold (Sector_Crescent_Religion_Update.pdf). Picked in the character editor next to
///     nationality, and changed in round at that faith's altar.
/// </summary>
[Prototype]
public sealed partial class ReligionPrototype : IPrototype
{
    /// <summary>
    ///     Held by anyone whose profile has no faith, or whose faith is not allowed for the role they spawned as.
    /// </summary>
    public static readonly ProtoId<ReligionPrototype> Default = "Unaffiliated";

    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public LocId Description;

    [DataField]
    public Color Color = Color.White;

    /// <summary>
    ///     Sorts the editor dropdown, higher first.
    /// </summary>
    [DataField]
    public int Weight;

    /// <summary>
    ///     Factions whose members may hold this faith. These are matched both against the profile's
    ///     <c>FactionPrototype</c> id and against the in-round <c>HullrotFaction</c> id, which is why this is plain
    ///     strings: IND only exists as the latter. Empty means the faith is open to everyone.
    /// </summary>
    [DataField]
    public HashSet<string> Factions = new();

    /// <summary>
    ///     Jobs that may take this faith whatever their faction. This is how spacers get access to every faith in the
    ///     character editor, where the profile faction is never IND.
    /// </summary>
    [DataField]
    public HashSet<ProtoId<JobPrototype>> Jobs = new();

    /// <summary>
    ///     If set, joining at an altar needs a <c>ReligiousLeader</c> of this faith standing near the altar to accept
    ///     the convert. The Covenant uses this so nobody enters it without the Prophet.
    /// </summary>
    [DataField]
    public bool RequiresLeader;

    /// <summary>
    ///     Context menu text on this faith's altar.
    /// </summary>
    [DataField]
    public LocId JoinVerb = "religion-verb-join";

    /// <summary>
    ///     Recited one line per step while joining at an altar. Interrupting any step aborts the rite.
    /// </summary>
    [DataField]
    public List<ReligionRiteLine> Rite = new();

    [DataField]
    public TimeSpan RiteStepDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     Shown to the convert once the rite completes.
    /// </summary>
    [DataField]
    public LocId? JoinedMessage;

    /// <summary>
    ///     Said by a faith figure using the religious call action, one picked at random.
    /// </summary>
    [DataField]
    public List<LocId> CallLines = new();

    /// <summary>
    ///     Said by every believer within <see cref="CallRange"/> who hears a call, one picked at random each.
    ///     Whoever stays silent does not hold the faith, or is hiding something.
    /// </summary>
    [DataField]
    public List<LocId> ResponseLines = new();

    [DataField]
    public float CallRange = 10f;

    /// <summary>
    ///     Whispered by a believer praying at this faith's altar. Each believer works through the whole list in a
    ///     random order before any line comes up again.
    /// </summary>
    [DataField]
    public List<LocId> PrayerLines = new();

    /// <summary>
    ///     How many lines one prayer whispers, one per <see cref="PrayerStepDelay"/>.
    /// </summary>
    [DataField]
    public int PrayerSteps = 3;

    [DataField]
    public TimeSpan PrayerStepDelay = TimeSpan.FromSeconds(3.5);

    public bool IsOpenTo(string? faction, string? job)
    {
        if (Factions.Count == 0)
            return true;

        if (!string.IsNullOrEmpty(faction) && Factions.Contains(faction))
            return true;

        return !string.IsNullOrEmpty(job) && Jobs.Contains(job);
    }
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ReligionRiteLine
{
    [DataField(required: true)]
    public LocId Line;

    [DataField]
    public ReligionRiteSpeaker Speaker = ReligionRiteSpeaker.Convert;
}

[Serializable, NetSerializable]
public enum ReligionRiteSpeaker : byte
{
    Convert,

    /// <summary>
    ///     The leader who accepted the convert. Falls back to the convert for faiths without one.
    /// </summary>
    Officiant,
}
