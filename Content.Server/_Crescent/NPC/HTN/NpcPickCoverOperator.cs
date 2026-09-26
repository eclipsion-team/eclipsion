using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: picks a tile for the NPC to move to - one to fight <see cref="TargetKey"/> from, or one to hide
/// from every enemy in. Fails when there is none, so the plan can fall back to something else.
/// </summary>
public sealed partial class NpcPickCoverOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NpcTacticalSystem _tactical = default!;

    // NPCBlackboard keeps its own constant for this private.
    public const string VisionRadiusKey = "VisionRadius";

    [DataField]
    public NpcCoverMode Mode = NpcCoverMode.Fight;

    /// <summary>
    /// Who to take cover from and keep a clear shot at. Not used when hiding.
    /// </summary>
    [DataField]
    public string TargetKey = "Target";

    /// <summary>
    /// Where the chosen tile goes.
    /// </summary>
    [DataField]
    public string CoverKey = "CoverCoordinates";

    /// <summary>
    /// The range band the NPC likes to fight in. A shotgunner wants to be close, a marksman far away.
    /// </summary>
    [DataField]
    public string MinRangeKey = "CombatRangeMin";

    [DataField]
    public string MaxRangeKey = "CombatRangeMax";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _tactical = sysManager.GetEntitySystem<NpcTacticalSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (Mode == NpcCoverMode.Hide)
        {
            var vision = blackboard.GetValueOrDefault<float>(VisionRadiusKey, _entManager);

            if (!_tactical.TryFindHidingSpot(owner, vision, out var spot))
                return (false, null);

            return (true, new Dictionary<string, object> { { CoverKey, spot.Value } });
        }

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
            return (false, null);

        var min = blackboard.GetValueOrDefault<float>(MinRangeKey, _entManager);
        var max = blackboard.GetValueOrDefault<float>(MaxRangeKey, _entManager);

        if (max <= 0f)
            max = MathF.Max(blackboard.GetValueOrDefault<float>(VisionRadiusKey, _entManager) - 2f, 4f);

        EntityCoordinates? cover;
        var found = Mode == NpcCoverMode.Firing
            ? _tactical.TryFindFiringSpot(owner, target, min, max, out cover)
            : _tactical.TryFindCover(owner, target, min, max, out cover);

        if (!found || cover is not { } picked)
            return (false, null);

        return (true, new Dictionary<string, object> { { CoverKey, picked } });
    }
}

public enum NpcCoverMode : byte
{
    /// <summary>
    /// Next to something solid, with a clear shot at the target.
    /// </summary>
    Fight,

    /// <summary>
    /// Out of sight of every enemy nearby.
    /// </summary>
    Hide,

    /// <summary>
    /// Anywhere the target can be seen and hit from, cover or not, looked for round the target rather than
    /// round the NPC - for when it is behind glass, in the next room, or on another grid.
    /// </summary>
    Firing,
}
