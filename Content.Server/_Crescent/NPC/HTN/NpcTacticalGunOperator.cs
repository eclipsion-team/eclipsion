using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: <see cref="Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged.GunOperator"/> for
/// soldiers. Shoots the same way, but doesn't stay rooted to one spot until the fight is over: it finishes
/// early - so the plan starts over and picks a new position - when the target has been out of sight for a
/// while, a friendly keeps getting in the way, the NPC has drifted off its squad leash, or it has simply
/// been fighting from the same place for long enough.
/// </summary>
public sealed partial class NpcTacticalGunOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    [DataField]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.TaskFinished;

    [DataField(required: true)]
    public string TargetKey = default!;

    /// <summary>
    /// Minimum damage state that the target has to be in for us to consider attacking.
    /// </summary>
    [DataField]
    public MobState TargetState = MobState.Alive;

    /// <summary>
    /// The chance that an NPC will aim to hit targets that are laying down.
    /// </summary>
    [DataField]
    public float DirectTargetChance = 0.5f;

    /// <summary>
    /// Whether the NPC steps out to a neighbouring tile every so often to shoot, and ducks back. For fighting
    /// from cover; out in the open it has nothing to duck back behind.
    /// </summary>
    [DataField]
    public bool Peek = true;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
            return (false, null);

        if (_entManager.TryGetComponent<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState > TargetState)
        {
            return (false, null);
        }

        return (true, null);
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        Angle? rotationSpeed = blackboard.TryGetValue<float>(NPCBlackboard.RotateSpeed, out var rotSpeed, _entManager)
            ? new Angle(rotSpeed)
            : null;

        _tactical.StartFiring(owner, blackboard.GetValue<EntityUid>(TargetKey), DirectTargetChance, rotationSpeed);
        _tactical.BeginEngagement(owner, Peek);
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _tactical.EndEngagement(owner);
        _entManager.System<SharedCombatModeSystem>().SetInCombatMode(owner, false);
        _entManager.RemoveComponent<NPCRangedCombatComponent>(owner);
        blackboard.Remove<EntityUid>(TargetKey);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        base.Update(blackboard, frameTime);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<NPCRangedCombatComponent>(owner, out var combat) ||
            !blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
        {
            return HTNOperatorStatus.Failed;
        }

        combat.Target = target;

        if (_entManager.TryGetComponent<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState > TargetState)
        {
            return HTNOperatorStatus.Finished;
        }

        switch (combat.Status)
        {
            case CombatStatus.Normal:
            case CombatStatus.NotInSight:
                break;
            default:
                return HTNOperatorStatus.Failed;
        }

        var moving = _entManager.TryGetComponent<NPCSteeringComponent>(owner, out var steering) &&
                     steering.Status == SteeringStatus.Moving;

        if (!_tactical.UpdateEngagement(owner, combat.Status == CombatStatus.NotInSight, moving, target))
        {
            _tactical.UpdatePeek(owner, target, moving, combat.Status == CombatStatus.NotInSight);
            return HTNOperatorStatus.Continuing;
        }

        // Plan the next position now rather than whenever the replan timer next comes round: the NPC doesn't
        // shoot at all between one plan ending and the next starting.
        if (_entManager.TryGetComponent<HTNComponent>(owner, out var htn))
            _entManager.System<HTNSystem>().Replan(htn);

        return HTNOperatorStatus.Finished;
    }
}
