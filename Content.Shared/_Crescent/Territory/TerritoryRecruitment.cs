using Content.Shared._Crescent.Economy;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Serializable, NetSerializable]
public enum TerritoryRecruitmentUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum TerritoryRecruitKind : byte
{
    // stands guard on the territory
    Garrison,

    // joins the caller's squad
    Escort,
}

/// <summary>
/// A kind of soldier a territory can call in, priced per soldier.
/// </summary>
[Prototype("territorySoldierRole")]
public sealed partial class TerritorySoldierRolePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public LocId? Description;

    // per soldier, pod included
    [DataField(required: true)]
    public int Price;

    // holder faction -> who comes down
    [DataField(required: true)]
    public Dictionary<string, EntProtoId> Soldiers = new();
}

[Serializable, NetSerializable]
public sealed class TerritoryRecruitmentState : BoundUserInterfaceState
{
    public string? Owner;
    public int Level;
    public bool Powered;

    // the kinds the holder can call in, in menu order
    public List<ProtoId<TerritorySoldierRolePrototype>> Roles = new();

    // added to the role's price for an escort
    public int EscortSurcharge;

    public int Garrison;
    public int GarrisonLimit;

    public int Escorts;
    public int EscortLimit;

    // an independent's console: mercenaries for their squad only, no garrison
    public bool Mercenaries;

    // how many more the viewer's own squad can take
    public int SquadRoom;

    public TerritoryPaymentState? Payment;
}

[Serializable, NetSerializable]
public sealed class TerritoryRecruitMessage(
    TerritoryRecruitKind kind,
    ProtoId<TerritorySoldierRolePrototype> role,
    int count,
    PaymentSource source) : BoundUserInterfaceMessage
{
    public TerritoryRecruitKind Kind { get; private set; } = kind;

    public ProtoId<TerritorySoldierRolePrototype> Role { get; private set; } = role;

    public int Count { get; private set; } = count;

    public PaymentSource Source { get; private set; } = source;
}

/// <summary>
/// A pod coming down with a soldier in it. The client draws the fall from <see cref="LandAt"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TerritoryDropPodComponent : Component
{
    [DataField, AutoNetworkedField]
    public TimeSpan LandAt;

    [DataField, AutoNetworkedField]
    public float FallTime = 4f;

    // tiles above the landing spot it comes in from
    [DataField]
    public float Height = 14f;

    [DataField, AutoNetworkedField]
    public bool Landed;

    [DataField]
    public SoundSpecifier? LandSound;

    // seconds the empty pod stays after landing
    [DataField]
    public float Linger = 20f;
}
