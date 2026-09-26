using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: opens fire on the target and keeps the NPC shooting through the rest of the plan - on its way
/// to cover, while closing in - instead of only once it gets there. Finishes straight away; the shooting
/// stops when the plan does.
/// </summary>
/// <remarks>
/// Without this a soldier went quiet every time it moved, and it moves a lot: to cover, to a new angle when
/// it lost sight of its target, and every several seconds just to stay unpredictable. That is most of what
/// read as NPCs "sometimes shooting and sometimes not".
/// </remarks>
public sealed partial class NpcFireOnTheMoveOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    [DataField]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.PlanFinished;

    [DataField(required: true)]
    public string TargetKey = default!;

    /// <summary>
    /// The chance that an NPC will aim to hit targets that are laying down.
    /// </summary>
    [DataField]
    public float DirectTargetChance = 0.5f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
            return;

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        Angle? rotationSpeed = blackboard.TryGetValue<float>(NPCBlackboard.RotateSpeed, out var rotSpeed, _entManager)
            ? new Angle(rotSpeed)
            : null;

        _tactical.StartFiring(owner, target, DirectTargetChance, rotationSpeed);
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _entManager.RemoveComponent<NPCRangedCombatComponent>(owner);
    }
}
