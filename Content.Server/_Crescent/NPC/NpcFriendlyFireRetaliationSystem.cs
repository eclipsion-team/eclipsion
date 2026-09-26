using Content.Server._Crescent.NpcSquad;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Damage;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Robust.Shared.Collections;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcFriendlyFireRetaliationComponent"/>
public sealed class NpcFriendlyFireRetaliationSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NpcIffSystem _iff = default!;
    [Dependency] private readonly NpcSquadSystem _squad = default!;
    [Dependency] private readonly NpcTacticalSystem _tactical = default!;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcFriendlyFireRetaliationComponent, DamageChangedEvent>(OnDamageChanged);
    }

    /// <summary>
    /// Whether this NPC is currently fighting <paramref name="target"/> for having hurt it.
    /// </summary>
    public bool HasGrudge(EntityUid npc, EntityUid target)
    {
        return TryComp<NpcFriendlyFireRetaliationComponent>(npc, out var comp) && comp.Grudges.ContainsKey(target);
    }

    private void OnDamageChanged(Entity<NpcFriendlyFireRetaliationComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } attacker || attacker == ent.Owner)
            return;

        // Only people pick fights. A friendly soldier AI's stray round or splash is an accident.
        if (!HasComp<MobStateComponent>(attacker) || HasComp<NpcIffComponent>(attacker))
            return;

        // Already down: nothing to fight back against.
        if (_mobState.IsIncapacitated(attacker))
            return;

        var now = _timing.CurTime;
        if (ent.Comp.Grudges.ContainsKey(attacker))
        {
            ent.Comp.Grudges[attacker] = now;
            return;
        }

        // An enemy shooting it is just the fight it is already in.
        if (!_iff.IsFriendly(ent.Owner, attacker))
            return;

        ent.Comp.Grudges[attacker] = now;
        _npcFaction.AggroEntity(ent.Owner, attacker);

        // Turn on them now, not whenever the current plan happens to run out.
        _squad.ForceReplan(ent);
        _tactical.TryCallout(ent, NpcCalloutType.Engage, force: true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;

        var query = EntityQueryEnumerator<NpcFriendlyFireRetaliationComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Grudges.Count == 0)
                continue;

            var putDown = false;
            foreach (var (attacker, lastHit) in new ValueList<KeyValuePair<EntityUid, TimeSpan>>(comp.Grudges))
            {
                var down = TerminatingOrDeleted(attacker) || _mobState.IsIncapacitated(attacker);
                var forgotten = comp.ForgetAfter is { } forget && now - lastHit >= forget;
                if (!down && !forgotten)
                    continue;

                comp.Grudges.Remove(attacker);
                _npcFaction.DeAggroEntity(uid, attacker);
                putDown |= down;
            }

            // Otherwise a soldier that was beating them with the stock would carry on into the critical body.
            if (putDown)
                _squad.ForceReplan(uid);
        }
    }
}
