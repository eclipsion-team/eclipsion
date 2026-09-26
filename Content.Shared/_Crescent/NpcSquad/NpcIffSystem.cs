using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Systems;

namespace Content.Shared._Crescent.NpcSquad;

/// <inheritdoc cref="NpcIffComponent"/>
public sealed class NpcIffSystem : EntitySystem
{
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;

    private EntityQuery<NpcIffComponent> _iffQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<HullrotFactionComponent> _hullrotQuery;

    public override void Initialize()
    {
        base.Initialize();

        _iffQuery = GetEntityQuery<NpcIffComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _hullrotQuery = GetEntityQuery<HullrotFactionComponent>();
    }

    /// <summary>
    /// Whether a round fired by <paramref name="shooter"/> should fly through <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Called for every projectile contact, so it bails out on the cheap checks first. Only mobs are ever
    /// let through: walls, windows and cover still stop a friendly round exactly as they would anyone's.
    /// </remarks>
    public bool ShouldPassThrough(EntityUid? shooter, EntityUid other)
    {
        if (shooter is not { } npc || npc == other)
            return false;

        if (!_iffQuery.TryComp(npc, out var iff) || !_mobQuery.HasComp(other))
            return false;

        return IsFriendly((npc, iff), other);
    }

    /// <summary>
    /// Whether <paramref name="other"/> is on the same side as <paramref name="npc"/>: its squad, its NPC
    /// factions, or a player whose Hullrot faction is one of them.
    /// </summary>
    public bool IsFriendly(Entity<NpcIffComponent?> npc, EntityUid other)
    {
        if (npc.Owner == other)
            return true;

        if (_iffQuery.Resolve(npc, ref npc.Comp, false) && npc.Comp.SquadLeader is { } leader)
        {
            if (other == leader)
                return true;

            if (_iffQuery.TryComp(other, out var otherIff) && otherIff.SquadLeader == leader)
                return true;
        }

        // HullrotFaction is the authoritative player allegiance and the NPC faction is only a mirror of it,
        // so check it directly too - same reasoning as the anti-boarder targeting in NPCUtilitySystem.
        if (_hullrotQuery.TryComp(other, out var hullrot)
            && !string.IsNullOrWhiteSpace(hullrot.Faction)
            && _npcFaction.IsMember(npc.Owner, hullrot.Faction.Trim()))
        {
            return true;
        }

        return _npcFaction.IsEntityFriendly(npc.Owner, other);
    }
}
