using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Swaps mapped entities for other prototypes once the round's maps are in, so a Great Hunt variant can reuse the
/// regular Great Hunt maps with different fixtures. Great Hunt: Blades uses it to put gunless armories and statues
/// in place of the normal ones.
/// </summary>
[RegisterComponent]
public sealed partial class GreatHuntReplaceEntitiesRuleComponent : Component
{
    /// <summary>Prototype found on the map → prototype spawned in its place, same position and rotation.</summary>
    [DataField(required: true)]
    public Dictionary<EntProtoId, EntProtoId> Replacements = new();

    /// <summary>Set once the swap has run, so it only ever runs once per round.</summary>
    [ViewVariables]
    public bool Done;
}
