using Content.Server.Medical.Components;

namespace Content.Server._Crescent.NPC;

public sealed partial class NpcTacticalSystem
{
    /// <summary>
    /// How far away the leader may be for a squadmate to break off and go to them.
    /// </summary>
    private const float MedicReach = 20f;

    /// <summary>
    /// Once it has started on the leader, the medic keeps going until they're under this fraction.
    /// </summary>
    private const float LeaderHealStopFraction = 0.1f;

    /// <summary>
    /// Whether this NPC should go and patch up its squad leader: they're hurt, it carries something that
    /// helps, and no other squadmate is already on it.
    /// </summary>
    public bool ShouldTendLeader(EntityUid npc, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(npc, ref comp, false) || !_mobState.IsAlive(npc))
            return false;

        if (!_squad.TryGetLeader(npc, out var leader) || _mobState.IsDead(leader))
            return false;

        var npcPos = _transform.GetMapCoordinates(npc);
        var leaderPos = _transform.GetMapCoordinates(leader);

        if (npcPos.MapId != leaderPos.MapId || (npcPos.Position - leaderPos.Position).Length() > MedicReach)
            return false;

        if (!_mobState.IsCritical(leader) &&
            (!TryGetDamageFraction(leader, out var fraction) || fraction < comp.LeaderHealThreshold))
        {
            return false;
        }

        if (!TryFindHealingItem(npc, leader, out _) && !CanUseMedipen(npc, leader, comp))
            return false;

        return _squad.TryClaimMedic(npc);
    }

    /// <summary>
    /// One step of looking after the leader: a medipen if they're in a bad way, then dressings.
    /// </summary>
    /// <returns>False once there's nothing more to do for them, or nothing to do it with.</returns>
    public bool TryTendLeader(EntityUid npc, EntityUid leader, NpcTacticalComponent comp)
    {
        if (TryUseMedipen(npc, leader, comp))
            return true;

        if (!_mobState.IsCritical(leader) &&
            (!TryGetDamageFraction(leader, out var fraction) || fraction < LeaderHealStopFraction))
        {
            return false;
        }

        return TryFindHealingItem(npc, leader, out var item) &&
               TryComp<HealingComponent>(item, out var healing) &&
               TryTreat(npc, leader, item.Value, healing);
    }
}
