using System.Linq;
using Content.Server._Crescent.Factions;
using Content.Server._Crescent.NpcSquad;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Mech.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent - lets soldier NPCs see mechs and fight them.
/// </summary>
/// <remarks>
/// <para>
/// A mech carries no MobState and, bar the faction combat mechs, no NPC faction of its own, while its pilot
/// sits inside a container. The faction lookup did hand the pilot over, but TargetAccessibleCon threw them out
/// for being in a container and TargetIsAliveCon threw out the chassis for not being a mob, so a soldier stood
/// and watched an enemy mech walk straight through its line.
/// </para>
/// <para>
/// Here the cockpit is scanned instead: the pilot's own side, any grudge the soldier holds against them, and the
/// faction ID they wear checked against the live diplomacy matrix - the same credential an anti-boarder gun
/// trusts. If whoever is in there is an enemy, the chassis itself becomes the target, since that is what is
/// actually standing there to be shot.
/// </para>
/// </remarks>
public sealed partial class NpcMechTargetingSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly FactionIdCardSystem _factionIds = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NpcIffSystem _iff = default!;
    [Dependency] private readonly NpcSquadSystem _squad = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<MechComponent> _mechQuery;
    private EntityQuery<NpcFactionMemberComponent> _factionQuery;

    private readonly HashSet<Entity<MechComponent>> _mechs = new();

    public override void Initialize()
    {
        base.Initialize();

        _mechQuery = GetEntityQuery<MechComponent>();
        _factionQuery = GetEntityQuery<NpcFactionMemberComponent>();
    }

    /// <summary>
    /// Adds every working mech within <paramref name="range"/> of <paramref name="npc"/> that has an enemy of
    /// it at the controls - or, for a faction mech, is an enemy itself.
    /// </summary>
    public void AddNearbyHostileMechs(EntityUid npc, float range, ICollection<EntityUid> into)
    {
        _mechs.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(npc), range, _mechs, LookupFlags.Uncontained);

        foreach (var mech in _mechs)
        {
            if (mech.Owner != npc && IsHostileMech(npc, mech))
                into.Add(mech.Owner);
        }
    }

    /// <summary>
    /// Whether <paramref name="target"/> is still worth shooting: a living mob, or a mech that still works and,
    /// unless it is a faction mech that fights on its own, still has a conscious pilot in it.
    /// </summary>
    public bool IsActiveTarget(EntityUid target)
    {
        if (!_mechQuery.TryComp(target, out var mech))
            return _mobState.IsAlive(target);

        if (mech.Broken)
            return false;

        if (_factionQuery.HasComp(target))
            return true;

        return mech.PilotSlot.ContainedEntity is { } pilot && !_mobState.IsIncapacitated(pilot);
    }

    /// <summary>
    /// Whether <paramref name="target"/> is a mech that has been knocked out or abandoned. Anything that isn't a
    /// mech is left to the caller's own mob state checks.
    /// </summary>
    public bool IsMechOutOfAction(EntityUid target)
    {
        return _mechQuery.HasComp(target) && !IsActiveTarget(target);
    }

    private bool IsHostileMech(EntityUid npc, Entity<MechComponent> mech)
    {
        if (!IsActiveTarget(mech))
            return false;

        // Faction combat mechs are a side in their own right, crewed or not.
        if (_factionQuery.HasComp(mech))
            return IsHostile(npc, mech);

        return mech.Comp.PilotSlot.ContainedEntity is { } pilot && IsHostile(npc, pilot);
    }

    private bool IsHostile(EntityUid npc, EntityUid target)
    {
        // Squad, NPC factions and the pilot's Hullrot allegiance - unless the soldier has a grudge against them.
        if (_iff.IsFriendly(npc, target) || _npcFaction.IsIgnored(npc, target))
            return false;

        // Someone it has been set on specifically: a friend who shot it, a hated faction, a pointed-out target.
        if (_npcFaction.GetHostiles(npc).Contains(target))
            return true;

        if (!_factionQuery.TryComp(npc, out var own))
            return false;

        // Read the card in the pilot's ID slot. An allied card gets them waved through, one from a faction at war
        // (or a spacer's, which no treaty covers) marks them as an enemy whatever NPC faction they carry - or
        // don't carry at all.
        switch (_factionIds.ReadCredential(npc, target))
        {
            case FactionCredentialStanding.Allied:
                return false;
            case FactionCredentialStanding.Hostile:
                return true;
        }

        // Under kill-all, anyone neither of its side nor allied. Otherwise the same test NearbyHostilesQuery puts
        // a mob on foot through.
        return _squad.IsKillAll(npc) || _npcFaction.IsMemberOfAny(target, own.HostileFactions);
    }
}
