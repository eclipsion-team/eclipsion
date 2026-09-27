using System.Threading;
using System.Threading.Tasks;
using Content.Server._Crescent.NpcSquad;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: met when this NPC follows its squad leader in a formation, see
/// <see cref="NpcSquadSystem.TryGetFormationSlot"/>.
/// </summary>
public sealed partial class NpcInFormationPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcSquadSystem>().TryGetFormationSlot(owner, out _, out _);
    }
}

/// <summary>
/// Crescent: keeps the NPC in its slot in the squad's formation for as long as the plan lasts. The slot moves
/// with the leader, so this never finishes on its own; a fight, or a new order, takes over from it.
/// </summary>
public sealed partial class NpcKeepFormationOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcSquadSystem _squad = default!;
    private NPCSteeringSystem _steering = default!;
    private SharedTransformSystem _transform = default!;

    /// <summary>
    /// Once the slot is this far from where the NPC is headed - the leader turned, or a wall got in the
    /// way - it heads for the new one. Any closer and it keeps on, so it isn't repathing every tick.
    /// </summary>
    private const float RetargetDistance = 0.75f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _squad = sysManager.GetEntitySystem<NpcSquadSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return (_squad.TryGetFormationSlot(owner, out _, out _), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_squad.TryGetFormationSlot(owner, out var slot, out var range))
            return HTNOperatorStatus.Failed;

        _entManager.TryGetComponent<NPCSteeringComponent>(owner, out var steering);

        // Can't get there from here: plan again, rather than asking for a path every tick.
        if (steering?.Status == SteeringStatus.NoPath)
            return HTNOperatorStatus.Failed;

        if (steering == null || NeedsRetarget(steering.Coordinates, slot))
            steering = _steering.Register(owner, slot, steering);

        steering.Range = range;
        return HTNOperatorStatus.Continuing;
    }

    private bool NeedsRetarget(EntityCoordinates current, EntityCoordinates slot)
    {
        if (!current.IsValid(_entManager))
            return true;

        var from = _transform.ToMapCoordinates(current);
        var to = _transform.ToMapCoordinates(slot);
        return from.MapId != to.MapId || (from.Position - to.Position).Length() > RetargetDistance;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);
        _steering.Unregister(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}

/// <summary>
/// Crescent: picks the edge of the squad's barricade ring this NPC builds next, see
/// <see cref="NpcSquadSystem.TryClaimFortSlot"/>.
/// </summary>
public sealed partial class NpcPickFortSlotOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "FortCoordinates";

    /// <summary>
    /// Where to stand while building it: on the tile, back from the edge it goes on.
    /// </summary>
    [DataField]
    public string StandKey = "FortStandCoordinates";

    [DataField]
    public string RotationKey = "BarricadeRotation";

    /// <summary>
    /// How close to <see cref="StandKey"/> counts as there.
    /// </summary>
    [DataField]
    public float Range = 0.4f;

    [DataField]
    public string RangeKey = "FortRange";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.System<NpcSquadSystem>().TryClaimFortSlot(owner, out var spot, out var stand, out var rotation))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, spot.Value },
            { StandKey, stand.Value },
            { RotationKey, rotation },
            { RangeKey, Range },
        });
    }
}
