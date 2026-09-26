using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Damage;
using Robust.Shared.Collections;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcPassiveTargetComponent"/>
public sealed class NpcPassiveTargetSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly NpcIffSystem _iff = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcIffComponent, DamageChangedEvent>(OnDamageChanged);
    }

    /// <summary>
    /// Whether <paramref name="npc"/> should leave <paramref name="target"/> alone: it is a passive target
    /// that hasn't lately hurt the NPC or anyone on its side.
    /// </summary>
    public bool IsLeftAlone(EntityUid npc, EntityUid target)
    {
        if (!TryComp<NpcPassiveTargetComponent>(target, out var passive))
            return false;

        var now = _timing.CurTime;
        foreach (var (victim, lastHit) in passive.Victims)
        {
            if (now - lastHit >= passive.ForgetAfter || TerminatingOrDeleted(victim))
                continue;

            if (victim == npc || _iff.IsFriendly(npc, victim))
                return false;
        }

        return true;
    }

    private void OnDamageChanged(Entity<NpcIffComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased
            || args.Origin is not { } attacker
            || !TryComp<NpcPassiveTargetComponent>(attacker, out var passive))
        {
            return;
        }

        var now = _timing.CurTime;

        // Drop whoever it is long done with, so a turret that has been shooting all round doesn't pile them up.
        foreach (var (victim, lastHit) in new ValueList<KeyValuePair<EntityUid, TimeSpan>>(passive.Victims))
        {
            if (now - lastHit >= passive.ForgetAfter || TerminatingOrDeleted(victim))
                passive.Victims.Remove(victim);
        }

        passive.Victims[ent.Owner] = now;
    }
}
