using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Crescent.NpcSquad;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: met when this NPC should go and patch up its squad leader, and makes it the squad's medic.
/// </summary>
public sealed partial class NpcShouldTendLeaderPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcTacticalSystem>().ShouldTendLeader(owner);
    }
}

/// <summary>
/// Crescent: met when another squadmate is patching the leader up, so this one should stand watch.
/// </summary>
public sealed partial class NpcShouldGuardLeaderPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcTacticalSystem>().ShouldGuardLeader(owner);
    }
}

/// <summary>
/// Crescent: met when this NPC should put up a barricade - defending with enemies about, or its leader hurt.
/// </summary>
public sealed partial class NpcWantsToFortifyPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var vision = blackboard.GetValueOrDefault<float>(NpcPickCoverOperator.VisionRadiusKey, _entManager);
        return _entManager.System<NpcTacticalSystem>().WantsToFortify(owner, vision);
    }
}

/// <summary>
/// Crescent: puts the squad leader's position on the blackboard, for moving over to them.
/// </summary>
public sealed partial class NpcPickLeaderOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "LeaderCoordinates";

    /// <summary>
    /// How close to get. Close enough to reach them with a dressing.
    /// </summary>
    [DataField]
    public float Range = 1f;

    [DataField]
    public string RangeKey = "LeaderRange";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.System<NpcSquadSystem>().TryGetLeader(owner, out var leader))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, new EntityCoordinates(leader, Vector2.Zero) },
            { RangeKey, Range },
        });
    }
}

/// <summary>
/// Crescent: patches up the squad leader - medipen if they're in a bad way, then dressings - until they're
/// back on their feet or the NPC runs out of anything that helps.
/// </summary>
public sealed partial class NpcTendLeaderOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcSquadSystem _squad = default!;
    private NpcTacticalSystem _tactical = default!;

    /// <summary>
    /// If the leader gets further than this away, go after them again rather than treating from range.
    /// </summary>
    private const float MaxReach = 1.6f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _squad = sysManager.GetEntitySystem<NpcSquadSystem>();
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<NpcTacticalComponent>(owner, out var comp) ||
            !_squad.TryGetLeader(owner, out var leader) ||
            !_squad.TryClaimMedic(owner))
        {
            return HTNOperatorStatus.Failed;
        }

        if (_tactical.IsTreating(owner))
            return HTNOperatorStatus.Continuing;

        var transform = _entManager.System<SharedTransformSystem>();
        var ownPos = transform.GetMapCoordinates(owner);
        var leaderPos = transform.GetMapCoordinates(leader);

        if (ownPos.MapId != leaderPos.MapId || (ownPos.Position - leaderPos.Position).Length() > MaxReach)
            return HTNOperatorStatus.Failed;

        return _tactical.TryTendLeader(owner, leader, comp)
            ? HTNOperatorStatus.Continuing
            : HTNOperatorStatus.Finished;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);

        if (status == HTNOperatorStatus.Finished)
            _squad.ReleaseMedic(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}

/// <summary>
/// Crescent: picks where this NPC stands watch while a squadmate patches the leader up, see
/// <see cref="NpcTacticalSystem.TryPickGuardSpot"/>.
/// </summary>
public sealed partial class NpcPickGuardSpotOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "GuardCoordinates";

    /// <summary>
    /// How close to the spot counts as being on it.
    /// </summary>
    [DataField]
    public float Range = 0.6f;

    [DataField]
    public string RangeKey = "GuardRange";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.System<NpcTacticalSystem>().TryPickGuardSpot(owner, out var spot))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, spot.Value },
            { RangeKey, Range },
        });
    }
}

/// <summary>
/// Crescent: stands watch over the leader while a squadmate patches them up, looking out and away from them.
/// Finishes once the treatment is over or the leader has moved off; an enemy showing up is the combat
/// branches' business, and they take over from this on their own.
/// </summary>
public sealed partial class NpcGuardOperator : HTNOperator
{
    private NpcTacticalSystem _tactical = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);
        _tactical.BeginGuard(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _tactical.UpdateGuard(owner, frameTime)
            ? HTNOperatorStatus.Continuing
            : HTNOperatorStatus.Finished;
    }
}

/// <summary>
/// Crescent: picks where to put a barricade, see <see cref="NpcTacticalSystem.TryPickBarricadeSpot"/>.
/// </summary>
public sealed partial class NpcPickBarricadeSpotOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "BarricadeCoordinates";

    [DataField]
    public string RotationKey = "BarricadeRotation";

    /// <summary>
    /// How close to the spot to stand while building. Next to it, not on it.
    /// </summary>
    [DataField]
    public float Range = 1.3f;

    [DataField]
    public string RangeKey = "BarricadeRange";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var vision = blackboard.GetValueOrDefault<float>(NpcPickCoverOperator.VisionRadiusKey, _entManager);

        if (!_entManager.System<NpcTacticalSystem>().TryPickBarricadeSpot(owner, vision, out var spot, out var rotation))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, spot.Value },
            { RotationKey, rotation },
            { RangeKey, Range },
        });
    }
}

/// <summary>
/// Crescent: puts up the barricade picked by <see cref="NpcPickBarricadeSpotOperator"/>.
/// </summary>
public sealed partial class NpcBuildBarricadeOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    [DataField]
    public string TargetKey = "BarricadeCoordinates";

    [DataField]
    public string RotationKey = "BarricadeRotation";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (blackboard.TryGetValue<EntityCoordinates>(TargetKey, out var spot, _entManager) &&
            blackboard.TryGetValue<Angle>(RotationKey, out var rotation, _entManager))
        {
            _tactical.TryStartBarricade(owner, spot, rotation);
        }
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (_tactical.IsBarricadeFinished(owner))
            return HTNOperatorStatus.Finished;

        return _tactical.IsBusy(owner) ? HTNOperatorStatus.Continuing : HTNOperatorStatus.Failed;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _tactical.EndBarricade(owner);
        blackboard.Remove<EntityCoordinates>(TargetKey);
        blackboard.Remove<Angle>(RotationKey);
    }
}
