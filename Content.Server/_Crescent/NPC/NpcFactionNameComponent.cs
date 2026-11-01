using Content.Shared.Dataset;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.NPC;

// gives a faction soldier a name that fits its side, e.g. "Levyman Konrad Brandt", over the random humanoid one
[RegisterComponent]
public sealed partial class NpcFactionNameComponent : Component
{
    // one picked at random and put in front, empty for none
    [DataField]
    public List<LocId> Ranks = new();

    [DataField(required: true)]
    public ProtoId<DatasetPrototype> MaleFirst;

    [DataField(required: true)]
    public ProtoId<DatasetPrototype> FemaleFirst;

    [DataField(required: true)]
    public ProtoId<DatasetPrototype> Last;

    [DataField]
    public ProtoId<DatasetPrototype>? Nicknames;

    [DataField]
    public float NicknameChance = 0.5f;
}
