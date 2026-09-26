using Content.Server.NPC;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.Weapons.Ranged.Systems;

namespace Content.Server._Crescent.NPC.HTN;

/// <summary>
/// Crescent: met when the NPC is hurt enough to stop and treat itself, and carries something to do it with.
/// </summary>
public sealed partial class NpcNeedsHealingPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NpcTacticalSystem>().NeedsHealing(owner);
    }
}

/// <summary>
/// Crescent: met when the NPC's gun is empty and it has a way to reload it.
/// </summary>
public sealed partial class NpcCanReloadPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.System<GunSystem>().TryGetGun(owner, out var gunUid, out _) || gunUid == owner)
            return false;

        return _entManager.System<NpcGunHandlingSystem>().CanReload(owner, gunUid);
    }
}
