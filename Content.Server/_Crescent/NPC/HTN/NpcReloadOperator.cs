using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.Weapons.Ranged.Systems;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: reloads the NPC's empty gun outside of a firefight. In combat the ranged combat system already
/// does this as part of readying the gun; this covers a gun that ran dry on the last shot of a fight, which
/// upstream's HTN would otherwise throw on the floor.
/// </summary>
public sealed partial class NpcReloadOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private GunSystem _gun = default!;
    private NpcGunHandlingSystem _npcGun = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _gun = sysManager.GetEntitySystem<GunSystem>();
        _npcGun = sysManager.GetEntitySystem<NpcGunHandlingSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_gun.TryGetGun(owner, out var gunUid, out _) || gunUid == owner)
            return (false, null);

        return (_npcGun.CanReload(owner, gunUid), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_gun.TryGetGun(owner, out var gunUid, out _) || gunUid == owner)
            return HTNOperatorStatus.Failed;

        // Readying the gun is what drives the reload along: it starts it, waits it out and seats the
        // magazine, one step per call.
        if (!_npcGun.TryReadyGun(owner, gunUid))
            return HTNOperatorStatus.Continuing;

        return _npcGun.IsDry(gunUid) ? HTNOperatorStatus.Failed : HTNOperatorStatus.Finished;
    }
}
