using Content.Server.Maps;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Territory;

[RegisterComponent]
public sealed partial class TerritoryDroneBayConsoleComponent : Component
{
    // hull -> price. only shieldless hulls with 150cc guns or lighter belong here
    [DataField(required: true)]
    public Dictionary<ProtoId<GameMapPrototype>, int> Hulls = new();

    // most of the treasury one member may draw on in a round
    [DataField]
    public float TreasuryShare = 0.25f;
}
