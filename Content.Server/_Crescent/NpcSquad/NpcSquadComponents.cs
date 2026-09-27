using System.Numerics;
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

    /// <summary>
    /// How long the NPC stays set on someone its leader pointed out - and, unless they are of its own
    /// faction, on everyone of theirs.
    /// </summary>
    [DataField]
    public TimeSpan OrderedHostilityTime = TimeSpan.FromMinutes(2);
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

    [ViewVariables]
    public NpcSquadFormation Formation = NpcSquadFormation.Loose;

    /// <summary>
    /// Which way the leader is heading, in world terms: the way they last walked, so the formation doesn't
    /// swing round every time they turn to look at something.
    /// </summary>
    [ViewVariables]
    public Vector2? Heading;

    /// <summary>
    /// Where the leader was when <see cref="Heading"/> was last worked out.
    /// </summary>
    [ViewVariables]
    public MapCoordinates HeadingSample = MapCoordinates.Nullspace;

    /// <summary>
    /// The barricade ring the squad was last told to put up, if any.
    /// </summary>
    [ViewVariables]
    public NpcSquadFort? Fort;
}

/// <summary>
/// Crescent: a 3x3 square of floor round the leader for the squad to barricade in, see
/// <see cref="NpcSquadSystem"/>. Each slot is one outer edge of the square: a barricade on a border tile,
/// facing out. One edge in the middle of a side is left open as the way in.
/// </summary>
public sealed class NpcSquadFort
{
    public EntityUid Grid;

    /// <summary>
    /// The tile in the middle of the square.
    /// </summary>
    public Vector2i Centre;

    /// <summary>
    /// The side left open, as a unit grid offset from <see cref="Centre"/>.
    /// </summary>
    public Vector2i Entrance;

    public readonly List<NpcSquadFortSlot> Slots = new();

    /// <summary>
    /// Whether the leader has been told how it went yet.
    /// </summary>
    public bool Reported;
}

public sealed class NpcSquadFortSlot
{
    public Vector2i Tile;

    /// <summary>
    /// Which way the barricade faces, as a unit grid offset. It goes on that edge of the tile.
    /// </summary>
    public Vector2i Facing;

    /// <summary>
    /// The squadmate on it right now, and until when that claim holds unless it is renewed.
    /// </summary>
    public EntityUid? Builder;
    public TimeSpan ClaimedUntil;

    /// <summary>
    /// Tries that came to nothing - someone stood in the way, the NPC got pulled off it. The slot is given up
    /// after a few.
    /// </summary>
    public int Failures;
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
    /// The spot the NPC holds, for <see cref="NpcSquadOrder.Defend"/> and <see cref="NpcSquadOrder.HoldFire"/>.
    /// Grid-relative, so it stays put whoever walks off. The NPC only ever moves to get back onto it.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? DefendPoint;

    /// <summary>
    /// The post the NPC held before its leader went down and the squad closed in round them, given back with
    /// <see cref="OrderBeforeLeaderDown"/>.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? DefendPointBeforeLeaderDown;

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
