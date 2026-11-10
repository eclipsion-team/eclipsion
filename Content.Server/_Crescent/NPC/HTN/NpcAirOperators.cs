using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Pathfinding;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: met when the NPC's tanks are spent and it knows where to get a fresh one. With
/// <see cref="Urgent"/>, only once it has nothing at all left to breathe in air it can't breathe.
/// </summary>
public sealed partial class NpcNeedsAirPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Urgent;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcStartInternalsSystem>().WantsAir(owner, Urgent);
    }
}

/// <summary>
/// Crescent: points the move at the loose tank or vending machine the NPC means to get air from. Each new
/// source is pathfound once; one it can't reach is left alone for a while.
/// </summary>
public sealed partial class NpcPickAirSourceOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcStartInternalsSystem _internals = default!;
    private PathfindingSystem _pathfind = default!;

    [DataField]
    public string TargetKey = "AirSourceCoordinates";

    [DataField]
    public string RangeKey = "AirSourceRange";

    [DataField]
    public float Range = 1.2f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _internals = sysManager.GetEntitySystem<NpcStartInternalsSystem>();
        _pathfind = sysManager.GetEntitySystem<PathfindingSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_internals.TryGetAirSource(owner, out var source, out var reachable))
            return (false, null);

        if (!reachable)
        {
            var path = await _pathfind.GetPath(owner, source, Range, cancelToken, _pathfind.GetFlags(blackboard));

            if (cancelToken.IsCancellationRequested)
                return (false, null);

            reachable = path.Result == PathResult.Path;
            _internals.SetAirSourceReachable(owner, reachable);

            if (!reachable)
                return (false, null);
        }

        return (true, new Dictionary<string, object>
        {
            { TargetKey, new EntityCoordinates(source, Vector2.Zero) },
            { RangeKey, Range },
        });
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);
        _internals.StartAirSourceTrip(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}

/// <summary>
/// Crescent: swaps the NPC's spent tank for the one it walked over to.
/// </summary>
public sealed partial class NpcTakeAirOperator : HTNOperator
{
    private NpcStartInternalsSystem _internals = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _internals = sysManager.GetEntitySystem<NpcStartInternalsSystem>();
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _internals.TryTakeAir(owner) ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }
}
