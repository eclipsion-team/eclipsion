using Content.Server.Maps;
using Content.Shared.Dataset;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Territory;

[RegisterComponent, Access(typeof(TerritoryDronePicketSystem))]
public sealed partial class TerritoryDronePicketsComponent : Component
{
    [DataField(required: true)]
    public List<ProtoId<GameMapPrototype>> Drones = new();

    [DataField]
    public int MinDrones = 1;

    [DataField]
    public int MaxDrones = 2;

    [DataField]
    public float Range = 500f;

    [DataField]
    public float Leash = 650f;

    // a drone further out than this breaks off and heads home
    [DataField]
    public float ReturnRange = 850f;

    [DataField]
    public float PatrolRange = 350f;

    [DataField]
    public float OrbitRange = 250f;

    [DataField]
    public float OrbitSpread = 60f;

    [DataField]
    public float AimScatter = 20f;

    // with no player body this close to the standard the pickets park and stop thinking
    [DataField]
    public float WakeRange = 1500f;

    [DataField]
    public Color IffColor = Color.FromHex("#9B59B6");

    [DataField]
    public ProtoId<DatasetPrototype> NamePrefixes = "TerritoryDroneNamePrefixes";

    [ViewVariables]
    public List<EntityUid> Pickets = new();

    [ViewVariables]
    public EntityUid? Target;

    [ViewVariables]
    public bool Dormant;

    // who the picket flies for, set when it goes up; null shoots everyone
    [ViewVariables]
    public string? Faction;
}

[RegisterComponent, Access(typeof(TerritoryDronePicketSystem))]
public sealed partial class TerritoryPicketDroneComponent : Component
{
    [ViewVariables]
    public EntityUid Standard;

    [ViewVariables]
    public int Slot;

    [ViewVariables]
    public bool Returning;

    // -1 left, 0 hold, 1 right
    [ViewVariables]
    public int Strafe;

    [ViewVariables]
    public float StrafeLeft;
}
