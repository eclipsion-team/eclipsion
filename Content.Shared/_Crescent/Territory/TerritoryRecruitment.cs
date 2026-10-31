using Content.Shared._Crescent.Economy;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
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

[Serializable, NetSerializable]
public sealed class TerritoryRecruitmentState : BoundUserInterfaceState
{
    public string? Owner;
    public int Level;
    public bool Powered;

    public int Garrison;
    public int GarrisonLimit;
    public int GarrisonPrice;

    public int Escorts;
    public int EscortLimit;
    public int EscortPrice;

    // how many more the viewer's own squad can take
    public int SquadRoom;

    public TerritoryPaymentState? Payment;
}

[Serializable, NetSerializable]
public sealed class TerritoryRecruitMessage(TerritoryRecruitKind kind, int count, PaymentSource source) : BoundUserInterfaceMessage
{
    public TerritoryRecruitKind Kind { get; private set; } = kind;

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
