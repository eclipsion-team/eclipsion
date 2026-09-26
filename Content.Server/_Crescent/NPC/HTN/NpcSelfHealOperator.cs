using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: the NPC treats itself with the medical supplies it carries, one application after another,
/// until it is patched up or out of anything useful.
/// </summary>
public sealed partial class NpcSelfHealOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return (_tactical.NeedsHealing(owner), null);
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_tactical.IsTreating(owner))
            _tactical.TryStartSelfHeal(owner);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (_tactical.IsTreating(owner))
            return HTNOperatorStatus.Continuing;

        // One application done, or interrupted. Keep going while there's still work and supplies for it.
        if (!_tactical.NeedsHealing(owner))
        {
            _tactical.StopHealing(owner);
            return HTNOperatorStatus.Finished;
        }

        return _tactical.TryStartSelfHeal(owner)
            ? HTNOperatorStatus.Continuing
            : HTNOperatorStatus.Failed;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);

        // Pulled off it by something more urgent - pick it up again later, not immediately.
        if (status != HTNOperatorStatus.Finished)
            _tactical.StopHealing(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner), failed: true);
    }
}
