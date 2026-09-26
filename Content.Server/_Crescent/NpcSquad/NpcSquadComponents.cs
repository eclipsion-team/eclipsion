using Content.Shared._Crescent.NpcSquad;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: lets a player of the same side right-click this NPC and take it into their squad.
/// </summary>
[RegisterComponent, Access(typeof(NpcSquadSystem))]
public sealed partial class NpcSquadRecruitableComponent : Component
{
    /// <summary>
    /// While following, how far from the leader an enemy may be for the NPC to go after it.
    /// </summary>
    [DataField]
    public float FollowEngageRange = 12f;

    /// <summary>
    /// While following, how far from the leader the NPC will go looking for cover.
    /// </summary>
    [DataField]
    public float FollowMoveRange = 7f;

    /// <summary>
    /// While defending, how far from its post an enemy may be for the NPC to go after it.
    /// </summary>
    [DataField]
    public float DefendEngageRange = 14f;

    /// <summary>
    /// While defending, how far from its post the NPC will move to fight.
    /// </summary>
    [DataField]
    public float DefendMoveRange = 5f;

    /// <summary>
    /// While another squadmate is patching the leader up, how far from the leader the NPC will move to fight.
    /// </summary>
    [DataField]
    public float GuardMoveRange = 4f;
}

/// <summary>
/// Crescent: a player leading a squad of faction NPCs.
/// </summary>
[RegisterComponent, Access(typeof(NpcSquadSystem))]
public sealed partial class NpcSquadLeaderComponent : Component
{
    [ViewVariables]
    public List<EntityUid> Members = new();

    [ViewVariables]
    public EntityUid? Action;

    /// <summary>
    /// The squadmate currently patching the leader up, so the whole squad doesn't drop everything at once.
    /// </summary>
    [ViewVariables]
    public EntityUid? Medic;

    /// <summary>
    /// The medic's claim lapses unless it keeps renewing it, so one that got pulled away - hurt itself, off
    /// fighting - doesn't keep everyone else from stepping in.
    /// </summary>
    [ViewVariables]
    public TimeSpan MedicUntil;

    /// <summary>
    /// Earliest time a squadmate may put another medipen into the leader.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextMedipen;
}

/// <summary>
/// Crescent: an NPC following a player's orders.
/// </summary>
[RegisterComponent, Access(typeof(NpcSquadSystem))]
public sealed partial class NpcSquadMemberComponent : Component
{
    [ViewVariables]
    public EntityUid Leader;

    [ViewVariables]
    public NpcSquadOrder Order = NpcSquadOrder.Follow;

    /// <summary>
    /// Where the NPC was told to hold, for <see cref="NpcSquadOrder.Defend"/>.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? DefendPoint;

    /// <summary>
    /// The order the NPC had before its leader went down and the squad closed in to hold around them. Given
    /// back when the leader is on their feet again, unless they've given a new one in the meantime.
    /// </summary>
    [ViewVariables]
    public NpcSquadOrder? OrderBeforeLeaderDown;

    /// <summary>
    /// What the leader last pointed out for the squad to shoot.
    /// </summary>
    [ViewVariables]
    public EntityUid? FocusTarget;
}
