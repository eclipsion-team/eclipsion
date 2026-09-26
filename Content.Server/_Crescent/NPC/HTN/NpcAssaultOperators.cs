using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: met when the NPC's squad has been ordered to attack, so it pushes in on its target instead of
/// holding a position.
/// </summary>
public sealed partial class NpcAssaultingPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcTacticalSystem>().IsAssaulting(owner);
    }
}

/// <summary>
/// Crescent: picks the next spot for an attacking NPC to push up to, see
/// <see cref="NpcTacticalSystem.TryFindAdvanceSpot"/>. Fails when there's nowhere closer to go, so the plan
/// falls back to ordinary fighting from cover.
/// </summary>
public sealed partial class NpcPickAdvanceSpotOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    [DataField]
    public string TargetKey = "Target";

    /// <summary>
    /// Where the chosen spot goes.
    /// </summary>
    [DataField]
    public string CoverKey = "AdvanceCoordinates";

    /// <summary>
    /// The NPC's preferred minimum range, which the assault stops at.
    /// </summary>
    [DataField]
    public string MinRangeKey = "CombatRangeMin";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
            return (false, null);

        var stopRange = _tactical.GetAssaultStopRange(owner, blackboard.GetValueOrDefault<float>(MinRangeKey, _entManager));

        if (!_tactical.TryFindAdvanceSpot(owner, target, stopRange, out var spot))
            return (false, null);

        return (true, new Dictionary<string, object> { { CoverKey, spot.Value } });
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        if (blackboard.TryGetValue<EntityCoordinates>(CoverKey, out var spot, _entManager))
            _tactical.BeginAdvance(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner), spot);
    }
}
