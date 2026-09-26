using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: met when the NPC has a lead on an enemy - where it last saw one, where it was shot from, or
/// what a friend called out - that it may go and follow up.
/// </summary>
public sealed partial class NpcHasLeadPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcTacticalSystem>().HasLeadToSearch(owner);
    }
}

/// <summary>
/// Crescent: puts the NPC's lead on the blackboard to move to, see <see cref="NpcTacticalSystem.HasLeadToSearch"/>.
/// </summary>
public sealed partial class NpcPickSearchSpotOperator : HTNOperator
{
    private NpcTacticalSystem _tactical = default!;

    [DataField]
    public string TargetKey = "SearchCoordinates";

    [DataField]
    public string RangeKey = "SearchRange";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_tactical.TryGetSearchSpot(owner, out var spot))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, spot },
            { RangeKey, NpcTacticalSystem.SearchArriveRange },
        });
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);
        _tactical.BeginSearch(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}

/// <summary>
/// Crescent: stands and looks around for a while, turning to face a new open direction every second or two.
/// Anyone it spots is the combat branches' business; they take over from this on their own.
/// </summary>
public sealed partial class NpcLookAroundOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    /// <summary>
    /// Blackboard key holding how long to look around for, in seconds. Without one it uses the NPC's search
    /// time.
    /// </summary>
    [DataField]
    public string? DurationKey;

    /// <summary>
    /// Whether looking around is the end of a search, so the lead is used up once it's done.
    /// </summary>
    [DataField]
    public bool ClearLead;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var duration = DurationKey != null && blackboard.TryGetValue<float>(DurationKey, out var seconds, _entManager)
            ? TimeSpan.FromSeconds(seconds)
            : _tactical.RandomSearchTime(owner);

        _tactical.BeginLookAround(owner, duration);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _tactical.UpdateLookAround(owner, frameTime)
            ? HTNOperatorStatus.Continuing
            : HTNOperatorStatus.Finished;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);

        // Same as WaitOperator: a better plan may still want the value.
        if (DurationKey != null && status != HTNOperatorStatus.BetterPlan)
            blackboard.Remove<float>(DurationKey);

        if (ClearLead && status == HTNOperatorStatus.Finished)
            _tactical.ClearLead(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}
