using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.NpcSquad;

/// <summary>
/// Crescent: standing orders a player can give the faction soldiers following them.
/// </summary>
/// <remarks>
/// The HTN reads this straight off the blackboard through <c>HasOrdersPrecondition</c>, so the names here are
/// also what the soldier HTN yaml refers to as <c>enum.NpcSquadOrder.*</c>.
/// </remarks>
[Serializable, NetSerializable]
public enum NpcSquadOrder : byte
{
    /// <summary>
    /// Stay on the leader and fight anything that comes close to them.
    /// </summary>
    Follow,

    /// <summary>
    /// Dig in where the leader stood when the order was given and hold that ground.
    /// </summary>
    Defend,

    /// <summary>
    /// Go loud: engage anything in sight and push onto it, leader or not.
    /// </summary>
    Attack,

    /// <summary>
    /// Stay on the leader and don't start anything.
    /// </summary>
    HoldFire,
}

[Serializable, NetSerializable]
public enum NpcSquadUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum NpcSquadMemberCondition : byte
{
    Healthy,
    Wounded,
    Critical,
    Dead,
}

/// <summary>
/// Opens the squad command window from the leader's hotbar.
/// </summary>
public sealed partial class NpcSquadMenuActionEvent : InstantActionEvent;

[Serializable, NetSerializable]
public sealed class NpcSquadMemberState
{
    public NetEntity Entity;
    public string Name = string.Empty;
    public NpcSquadOrder Order;
    public NpcSquadMemberCondition Condition;

    /// <summary>
    /// 1 when unhurt, 0 at the critical threshold.
    /// </summary>
    public float Health;

    /// <summary>
    /// Spare magazines still carried, or null for a weapon that doesn't take any.
    /// </summary>
    public int? SpareMagazines;

    /// <summary>
    /// Distance from the leader in metres, or null when they are somewhere else entirely.
    /// </summary>
    public float? Distance;

    public bool InCombat;
}

[Serializable, NetSerializable]
public sealed class NpcSquadBuiState : BoundUserInterfaceState
{
    public readonly List<NpcSquadMemberState> Members;
    public readonly int MaxMembers;

    public NpcSquadBuiState(List<NpcSquadMemberState> members, int maxMembers)
    {
        Members = members;
        MaxMembers = maxMembers;
    }
}

/// <summary>
/// Gives an order to one member, or to the whole squad when <see cref="Member"/> is null.
/// </summary>
[Serializable, NetSerializable]
public sealed class NpcSquadOrderMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity? Member;
    public readonly NpcSquadOrder Order;

    public NpcSquadOrderMessage(NetEntity? member, NpcSquadOrder order)
    {
        Member = member;
        Order = order;
    }
}

/// <summary>
/// Releases one member, or the whole squad when <see cref="Member"/> is null.
/// </summary>
[Serializable, NetSerializable]
public sealed class NpcSquadDismissMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity? Member;

    public NpcSquadDismissMessage(NetEntity? member)
    {
        Member = member;
    }
}

/// <summary>
/// Crescent: a soldier NPC putting up a barricade. The spot and facing live on the NPC, server side.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NpcBuildBarricadeDoAfterEvent : SimpleDoAfterEvent;
