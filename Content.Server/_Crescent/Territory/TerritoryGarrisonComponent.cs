using Content.Shared._Crescent.Territory;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// Puts the holder's soldiers on a territory at round start, as many as its level allows.
/// </summary>
[RegisterComponent, Access(typeof(TerritoryGarrisonSystem))]
public sealed partial class TerritoryGarrisonComponent : Component
{
    // what the recruitment console offers, in menu order; the round-start garrison is a random mix of them
    [DataField(required: true)]
    public List<ProtoId<TerritorySoldierRolePrototype>> Roles = new();

    // tiles from the standard a soldier may be put down on
    [DataField]
    public int SpawnRadius = 8;
}

/// <summary>
/// Who a drop pod is bringing in, and for whom.
/// </summary>
[RegisterComponent, Access(typeof(TerritoryDropPodSystem))]
public sealed partial class TerritoryDropPodPayloadComponent : Component
{
    [DataField]
    public EntProtoId Soldier;

    [DataField]
    public string Faction = string.Empty;

    [DataField]
    public string RegionId = string.Empty;

    [DataField]
    public bool Escort;

    // an escort joins this player's squad when it lands
    [ViewVariables]
    public EntityUid? Recruiter;
}
