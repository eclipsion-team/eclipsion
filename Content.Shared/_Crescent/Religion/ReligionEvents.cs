using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Content.Shared.Eui;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Religion;

/// <summary>
///     One step of a rite at an altar. Each completed step speaks the next line and queues the step after it.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class ReligionRiteDoAfterEvent : DoAfterEvent
{
    [DataField]
    public int Step;

    /// <summary>
    ///     The leader who accepted the convert, for faiths that need one.
    /// </summary>
    [DataField]
    public NetEntity? Officiant;

    public ReligionRiteDoAfterEvent()
    {
    }

    public ReligionRiteDoAfterEvent(int step, NetEntity? officiant)
    {
        Step = step;
        Officiant = officiant;
    }

    public override DoAfterEvent Clone() => this;
}

/// <summary>
///     One step of a believer praying at their own faith's altar. Each completed step whispers the next prayer line.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class ReligionPrayerDoAfterEvent : DoAfterEvent
{
    [DataField]
    public int Step;

    public ReligionPrayerDoAfterEvent()
    {
    }

    public ReligionPrayerDoAfterEvent(int step)
    {
        Step = step;
    }

    public override DoAfterEvent Clone() => this;
}

/// <summary>
///     One step of the Prophet forcing the Binding on someone standing on a Veiled Eye.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class VeiledBindingDoAfterEvent : DoAfterEvent
{
    [DataField]
    public int Step;

    [DataField]
    public NetEntity Eye;

    public VeiledBindingDoAfterEvent()
    {
    }

    public VeiledBindingDoAfterEvent(int step, NetEntity eye)
    {
        Step = step;
        Eye = eye;
    }

    public override DoAfterEvent Clone() => this;
}

[Serializable, NetSerializable]
public sealed partial class DrawVeiledEyeDoAfterEvent : DoAfterEvent
{
    [DataField]
    public NetCoordinates Coordinates;

    public DrawVeiledEyeDoAfterEvent()
    {
    }

    public DrawVeiledEyeDoAfterEvent(NetCoordinates coordinates)
    {
        Coordinates = coordinates;
    }

    public override DoAfterEvent Clone() => this;
}

[Serializable, NetSerializable]
public sealed partial class ScrubVeiledEyeDoAfterEvent : SimpleDoAfterEvent;

public sealed partial class DrawVeiledEyeActionEvent : WorldTargetActionEvent;

public sealed partial class OpenVeiledHostActionEvent : InstantActionEvent;

/// <summary>
///     A faith figure calling out to the believers around them, who answer it.
/// </summary>
public sealed partial class ReligiousCallActionEvent : InstantActionEvent
{
    [DataField(required: true)]
    public ProtoId<ReligionPrototype> Religion;
}

[Serializable, NetSerializable]
public enum VeiledHostMemberStatus : byte
{
    Alive,
    Critical,
    Dead,
}

[Serializable, NetSerializable]
public sealed class VeiledHostMember
{
    public NetEntity Entity { get; }
    public string Name { get; }

    /// <summary>
    ///     Bound on a Veiled Eye rather than accepted at the altar. Only these can be chastised.
    /// </summary>
    public bool Bound { get; }

    public VeiledHostMemberStatus Status { get; }

    /// <summary>
    ///     Null when the member is on another map, where the Prophet cannot sense them.
    /// </summary>
    public float? Distance { get; }

    public string? Direction { get; }

    public bool CanChastise { get; }

    public VeiledHostMember(
        NetEntity entity,
        string name,
        bool bound,
        VeiledHostMemberStatus status,
        float? distance,
        string? direction,
        bool canChastise)
    {
        Entity = entity;
        Name = name;
        Bound = bound;
        Status = status;
        Distance = distance;
        Direction = direction;
        CanChastise = canChastise;
    }
}

[Serializable, NetSerializable]
public sealed class VeiledHostEuiState : EuiStateBase
{
    public List<VeiledHostMember> Members { get; }

    public VeiledHostEuiState(List<VeiledHostMember> members)
    {
        Members = members;
    }
}

[Serializable, NetSerializable]
public sealed class VeiledHostChastiseMessage : EuiMessageBase
{
    public NetEntity Member { get; }

    public VeiledHostChastiseMessage(NetEntity member)
    {
        Member = member;
    }
}

[Serializable, NetSerializable]
public sealed class VeiledHostRefreshMessage : EuiMessageBase;
