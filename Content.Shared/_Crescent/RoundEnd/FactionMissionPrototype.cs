using Content.Shared.Whitelist;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.RoundEnd;

/// <summary>
/// A single faction objective that a round-end computer can accept. The faction gathers the listed items, feeds
/// them into their computer, and turns the mission in for a reward (handled elsewhere) plus a sector-wide
/// announcement. Modelled on the cargo bounty: each entry is an item whitelist plus a required amount.
/// </summary>
[Prototype, Serializable, NetSerializable]
public sealed partial class FactionMissionPrototype : IPrototype
{
    /// <inheritdoc/>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Player-facing mission title.</summary>
    [DataField]
    public LocId Name = string.Empty;

    /// <summary>Player-facing flavour / briefing shown in the console.</summary>
    [DataField]
    public LocId Description = string.Empty;

    /// <summary>The sector-wide announcement broadcast to everyone when this mission is turned in.</summary>
    [DataField(required: true)]
    public LocId Announcement = string.Empty;

    /// <summary>Who the announcement is signed by. Defaults to the console's own sender if left empty.</summary>
    [DataField]
    public LocId? AnnouncementSender;

    /// <summary>Colour of the sector-wide announcement text.</summary>
    [DataField]
    public Color AnnouncementColor = Color.Gold;

    /// <summary>The items that must all be present in the console for the mission to be completable.</summary>
    [DataField(required: true)]
    public List<FactionMissionItemEntry> Entries = new();

    /// <summary>
    /// Placeholder reward hook: entities spawned at the console on completion. Left empty for now — the reward
    /// pass fills this in (or replaces it with a richer reward) later. Completion also raises
    /// <see cref="FactionMissionCompletedEvent"/> so a dedicated reward system can react instead.
    /// </summary>
    [DataField]
    public List<EntProtoId> RewardSpawns = new();

    /// <summary>
    /// Game rules started on completion, for rewards that act on the whole sector rather than the console's tile
    /// (e.g. the CMM finale bringing the Analiesse back into play).
    /// </summary>
    [DataField]
    public List<EntProtoId> RewardGameRules = new();
}

/// <summary>One required item of a <see cref="FactionMissionPrototype"/>. Mirrors the cargo bounty entry.</summary>
[DataDefinition, Serializable, NetSerializable]
public readonly partial record struct FactionMissionItemEntry()
{
    /// <summary>What items satisfy this entry.</summary>
    [DataField(required: true)]
    public EntityWhitelist Whitelist { get; init; } = default!;

    /// <summary>Items matching this are excluded even if they pass the whitelist.</summary>
    [DataField]
    public EntityWhitelist? Blacklist { get; init; } = null;

    /// <summary>How much of the item must be present (stacks count by their size).</summary>
    [DataField]
    public int Amount { get; init; } = 1;

    /// <summary>Player-facing label for the required item.</summary>
    [DataField]
    public LocId Name { get; init; } = string.Empty;
}
