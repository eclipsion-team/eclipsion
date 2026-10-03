using Content.Server._Crescent.Factions;
using Content.Server._Crescent.NPC;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.Popups;

namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: rules of engagement - who a faction soldier goes for of its own accord. Its NPC faction's hostile
/// list, always, and under kill-all (<see cref="NpcKillAllComponent"/>) every person who is neither of its side nor
/// wearing an allied faction's ID. Either way someone wearing an ally's ID is left be unless it has been set on
/// them specifically.
/// </summary>
public sealed partial class NpcSquadSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly FactionIdCardSystem _factionIds = default!;

    private readonly HashSet<Entity<HumanoidAppearanceComponent>> _people = new();

    private void OnKillAllMessage(Entity<NpcSquadLeaderComponent> ent, ref NpcSquadKillAllMessage args)
    {
        SetSquadKillAll(ent, args.Enabled);
    }

    /// <summary>
    /// Puts the whole squad under kill-all rules of engagement, or back to engaging only its faction's enemies.
    /// </summary>
    public void SetSquadKillAll(Entity<NpcSquadLeaderComponent> ent, bool enabled)
    {
        if (ent.Comp.KillAll == enabled)
            return;

        ent.Comp.KillAll = enabled;

        var acknowledged = false;
        foreach (var npc in ent.Comp.Members.ToArray())
        {
            // Pick up - or drop - the targets this changes right away.
            ForceReplan(npc);

            if (!acknowledged)
                acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
        }

        _popup.PopupEntity(Loc.GetString(enabled ? "npc-squad-killall-given-on" : "npc-squad-killall-given-off"), ent, ent, PopupType.Small);
        UpdateUi(ent, ent.Comp);
    }

    /// <summary>
    /// Sets a soldier's own rules of engagement, the ones it keeps whenever it isn't following a squad.
    /// </summary>
    public void SetKillAll(EntityUid npc, bool enabled)
    {
        EnsureComp<NpcKillAllComponent>(npc).Enabled = enabled;
    }

    /// <summary>
    /// Whether <paramref name="npc"/> is under kill-all rules of engagement: its squad's, if it follows one,
    /// otherwise its own.
    /// </summary>
    public bool IsKillAll(EntityUid npc)
    {
        if (TryComp<NpcSquadMemberComponent>(npc, out var member) &&
            TryComp<NpcSquadLeaderComponent>(member.Leader, out var leader))
        {
            return leader.KillAll;
        }

        return TryComp<NpcKillAllComponent>(npc, out var killAll) && killAll.Enabled;
    }

    /// <summary>
    /// Whether <paramref name="target"/> wears the ID of <paramref name="npc"/>'s own faction or of an ally.
    /// </summary>
    public bool IsAllied(EntityUid npc, EntityUid target)
    {
        return _factionIds.ReadCredential(npc, target) == FactionCredentialStanding.Allied;
    }

    /// <summary>
    /// Whether <paramref name="npc"/> would go for <paramref name="target"/> of its own accord, leaving aside its
    /// orders, its leash and whether the target is still standing.
    /// </summary>
    public bool IsEnemy(EntityUid npc, EntityUid target)
    {
        if (_iff.IsFriendly(npc, target))
            return false;

        // Set on them specifically: a grudge, a pointed-out target, their faction hated.
        if (_npcFaction.GetHostiles(npc).Contains(target))
            return true;

        if (IsAllied(npc, target))
            return false;

        if (TryComp<NpcFactionMemberComponent>(npc, out var own) &&
            _npcFaction.IsMemberOfAny(target, own.HostileFactions))
        {
            return true;
        }

        return IsKillAll(npc) && HasComp<HumanoidAppearanceComponent>(target);
    }

    /// <summary>
    /// Adds every living person within <paramref name="range"/> that <paramref name="npc"/> goes for under
    /// kill-all, if it is under it: anyone who is neither of its side nor wearing an allied faction's ID.
    /// </summary>
    public void AddKillAllTargets(EntityUid npc, float range, ICollection<EntityUid> into)
    {
        if (!IsKillAll(npc))
            return;

        _people.Clear();
        // Mech pilots are the mech targeting's business: the chassis is what stands there to be shot.
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(npc), range, _people, LookupFlags.Uncontained);

        foreach (var person in _people)
        {
            var uid = person.Owner;
            if (uid == npc || into.Contains(uid) ||
                !TryComp<MobStateComponent>(uid, out var mobState) || !_mobState.IsAlive(uid, mobState))
            {
                continue;
            }

            if (_iff.IsFriendly(npc, uid) || _npcFaction.IsIgnored(npc, uid) || IsAllied(npc, uid))
                continue;

            into.Add(uid);
        }
    }
}
