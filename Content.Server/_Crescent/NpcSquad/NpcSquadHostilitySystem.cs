using Content.Server._Crescent.NPC;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Collections;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NpcSquad;

/// <inheritdoc cref="NpcSquadHostilityComponent"/>
public sealed class NpcSquadHostilitySystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NpcFriendlyFireRetaliationSystem _retaliation = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// How far around the NPC people of a hated faction are picked up. Anything further out is past what the
    /// target queries look at anyway.
    /// </summary>
    private const float ScanRange = 20f;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextUpdate;

    private readonly HashSet<Entity<MobStateComponent>> _nearby = new();
    private readonly List<string> _allegiances = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcSquadHostilityComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<NpcSquadHostilityComponent> ent, ref ComponentShutdown args)
    {
        foreach (var target in ent.Comp.Targets.Keys)
        {
            Release(ent, target);
        }

        ent.Comp.Targets.Clear();
    }

    /// <summary>
    /// Sets the NPC on <paramref name="target"/> until it is down or <paramref name="duration"/> runs out.
    /// </summary>
    public void AddTarget(EntityUid npc, EntityUid target, TimeSpan duration)
    {
        if (npc == target)
            return;

        var comp = EnsureComp<NpcSquadHostilityComponent>(npc);
        var until = _timing.CurTime + duration;

        if (!comp.Targets.TryGetValue(target, out var current) || current < until)
            comp.Targets[target] = until;

        _npcFaction.AggroEntity(npc, target);
    }

    /// <summary>
    /// Turns the NPC against everyone of <paramref name="faction"/> for <paramref name="duration"/>.
    /// </summary>
    public void AddFaction(EntityUid npc, string faction, TimeSpan duration)
    {
        var comp = EnsureComp<NpcSquadHostilityComponent>(npc);
        var until = _timing.CurTime + duration;

        if (!comp.Factions.TryGetValue(faction, out var current) || current < until)
            comp.Factions[faction] = until;
    }

    /// <summary>
    /// The sides <paramref name="uid"/> fights for. A player's Hullrot faction is the authoritative one; their
    /// NPC factions also carry the NanoTrasen every player mob ships with, so those only count for mobs that
    /// have no Hullrot faction at all.
    /// </summary>
    public void GetAllegiances(EntityUid uid, List<string> allegiances)
    {
        allegiances.Clear();

        if (TryComp<HullrotFactionComponent>(uid, out var hullrot) && !string.IsNullOrWhiteSpace(hullrot.Faction))
        {
            allegiances.Add(hullrot.Faction.Trim());
            return;
        }

        if (!TryComp<NpcFactionMemberComponent>(uid, out var member))
            return;

        foreach (var faction in member.Factions)
        {
            allegiances.Add(faction.Id);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;

        var query = EntityQueryEnumerator<NpcSquadHostilityComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            foreach (var (faction, until) in new ValueList<KeyValuePair<string, TimeSpan>>(comp.Factions))
            {
                if (now >= until)
                    comp.Factions.Remove(faction);
            }

            if (comp.Factions.Count > 0 && _mobState.IsAlive(uid))
                PickUpHated((uid, comp));

            foreach (var (target, until) in new ValueList<KeyValuePair<EntityUid, TimeSpan>>(comp.Targets))
            {
                if (now < until && !TerminatingOrDeleted(target) && !_mobState.IsIncapacitated(target))
                {
                    // Put back in case something else - a friendly-fire grudge ending - took it off meanwhile.
                    _npcFaction.AggroEntity(uid, target);
                    continue;
                }

                comp.Targets.Remove(target);
                Release(uid, target);
            }

            if (comp.Factions.Count == 0 && comp.Targets.Count == 0)
                RemCompDeferred(uid, comp);
        }
    }

    /// <summary>
    /// Sets the NPC on everyone nearby who belongs to a faction it has been turned against.
    /// </summary>
    private void PickUpHated(Entity<NpcSquadHostilityComponent> ent)
    {
        var npc = ent.Owner;
        TryComp<NpcIffComponent>(npc, out var iff);

        _nearby.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(npc), ScanRange, _nearby);

        foreach (var other in _nearby)
        {
            var uid = other.Owner;
            if (uid == npc || ent.Comp.Targets.ContainsKey(uid) || _mobState.IsIncapacitated(uid, other.Comp))
                continue;

            // Never its own leader or squadmates, whatever side they are on.
            if (iff?.SquadLeader is { } leader &&
                (uid == leader || CompOrNull<NpcIffComponent>(uid)?.SquadLeader == leader))
            {
                continue;
            }

            GetAllegiances(uid, _allegiances);

            TimeSpan? until = null;
            var ownSide = false;
            foreach (var faction in _allegiances)
            {
                if (_npcFaction.IsMember(npc, faction))
                {
                    ownSide = true;
                    break;
                }

                if (ent.Comp.Factions.TryGetValue(faction, out var factionUntil))
                    until = factionUntil;
            }

            if (ownSide || until is not { } expiry)
                continue;

            ent.Comp.Targets[uid] = expiry;
            _npcFaction.AggroEntity(npc, uid);
        }
    }

    private void Release(EntityUid npc, EntityUid target)
    {
        // Someone on its side who shot it stays a target until the grudge itself is settled.
        if (_retaliation.HasGrudge(npc, target))
            return;

        _npcFaction.DeAggroEntity(npc, target);
    }
}
