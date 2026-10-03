using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// A possible spot for the Great Altar. Map several of these on a grid: once the grid has initialized, exactly one
/// of them is picked (weighted by <see cref="Weight"/>) and the altar spawns there, facing the same way the marker
/// does. Every marker on the grid is then deleted, so the altar moves between the mapped spots from round to round.
/// </summary>
[RegisterComponent, Access(typeof(GreatAltarSpawnPointSystem))]
public sealed partial class GreatAltarSpawnPointComponent : Component
{
    /// <summary>What spawns at the chosen marker.</summary>
    [DataField]
    public EntProtoId Prototype = "GreatAltar";

    /// <summary>Relative chance of this marker being the one picked, against the other markers on the same grid.</summary>
    [DataField]
    public float Weight = 1f;
}
