using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using System.ComponentModel.DataAnnotations;

namespace Content.Shared.Roles;

[Prototype("faction")]
public sealed partial class FactionPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;

    [DataField("name", required: true)] public string Name = default!;

    /// <summary>
    ///     Used to add certain conditions to the faction like spawn restrictions. Text is red.
    /// </summary>
    [DataField("descriptionPrefix")] public string DescriptionPrefix = default!;

    /// <summary>
    ///     Used to set the color of the faction button. Default is dark gray.
    /// </summary>
    [DataField("buttonColor")] public Color FactionButtonColor = Color.DarkSlateGray;

    /// <summary>
    ///     Eclipsion - accent the client UI takes on while the local player belongs to this faction.
    ///     Kept separate from <see cref="FactionButtonColor"/>, which is too dark to read on the HUD
    ///     (several factions use Black). Null leaves the neutral accent.
    /// </summary>
    [DataField("uiAccent")] public Color? UiAccent;

    [DataField("description", required: true)] public string Description = default!;

    [DataField("icon", required: true)] public SpriteSpecifier Icon = SpriteSpecifier.Invalid;

    /// <summary>
    ///     A color representing this department to use for text.
    /// </summary>
    [DataField("color", required: true)]
    public Color Color = default!;

    /// <summary>
    /// Departments with a higher weight sorted before other departments in UI.
    /// </summary>
    [DataField("weight")]
    public int Weight { get; private set; } = 0;

    /// <summary>
    /// Frontier - whether or not to show this faction. Defaults to no.
    /// </summary>
    [DataField("enabled")]
    public bool Enabled = false;

    /// <summary>
    /// Eclipsion - the faction can be picked but only spawns through midround events or specific gamemodes.
    /// The faction selector warns the player about it so they do not wait on a round-start slot that never comes.
    /// </summary>
    [DataField("limitedAvailability")]
    public bool LimitedAvailability;

    /// <summary>
    /// How this faction's join slots scale with the server population. Defaults to unrestricted.
    /// </summary>
    [DataField("balanceMode")]
    public FactionBalanceMode BalanceMode = FactionBalanceMode.None;

    /// <summary>
    /// Relative pull inside the parity group, only read for <see cref="FactionBalanceMode.Parity"/>.
    /// Two factions on 1.0 are held level with each other; a faction on 0.5 is allowed half as many players.
    /// </summary>
    [DataField("balanceWeight")]
    public float BalanceWeight = 1f;

    /// <summary>
    /// Fraction of the whole tracked population this faction may hold, only read for
    /// <see cref="FactionBalanceMode.Share"/>. 0.25 means at most a quarter of everyone playing.
    /// </summary>
    [DataField("balanceShare")]
    public float BalanceShare = 0.25f;
}

/// <summary>
/// How a faction takes part in population-scaled join caps.
/// </summary>
[Serializable, NetSerializable]
public enum FactionBalanceMode : byte
{
    /// <summary>
    /// Not capped and not counted. Midround-only and retired factions sit here.
    /// </summary>
    None,

    /// <summary>
    /// Held level against the other parity factions by <see cref="FactionPrototype.BalanceWeight"/>.
    /// This is the group the war is fought between, so it only ever measures itself.
    /// </summary>
    Parity,

    /// <summary>
    /// Capped at <see cref="FactionPrototype.BalanceShare"/> of the whole tracked population, and never
    /// constrains anyone else. Support factions belong here: an empty one must not lock out the war.
    /// In a round played without any parity faction these become the war group and are held level
    /// against each other by their shares instead, or they would cap each other out of the round.
    /// </summary>
    Share,
}

/// <summary>
/// Sorts <see cref="FactionPrototype"/> appropriately for display in the UI,
/// respecting their <see cref="FactionPrototype.Weight"/>.
/// </summary>
public sealed class FactionUIComparer : IComparer<FactionPrototype>
{
    public static readonly FactionUIComparer Instance = new();

    public int Compare(FactionPrototype? x, FactionPrototype? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (ReferenceEquals(null, y))
            return 1;
        if (ReferenceEquals(null, x))
            return -1;

        var cmp = -x.Weight.CompareTo(y.Weight);
        if (cmp != 0)
            return cmp;
        return string.Compare(x.ID, y.ID, StringComparison.Ordinal);
    }
}
