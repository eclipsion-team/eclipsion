using System.Linq;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Damage;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Events;
using Content.Shared.NPC.Systems;

namespace Content.Server._Crescent.NpcSquad;

/// <inheritdoc cref="NpcMercenaryComponent"/>
public sealed class NpcMercenarySystem : EntitySystem
{
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NpcSquadHostilitySystem _hostility = default!;
    [Dependency] private readonly NpcSquadSystem _squad = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcMercenaryComponent, NpcFactionAddedEvent>(OnFactionAdded);
        SubscribeLocalEvent<NpcMercenaryComponent, DamageChangedEvent>(OnMercenaryDamaged);
        SubscribeLocalEvent<NpcMercenaryEmployerComponent, DamageChangedEvent>(OnEmployerDamaged);
    }

    // a hardsuit adds IND, which would set it on every power's people
    private void OnFactionAdded(Entity<NpcMercenaryComponent> ent, ref NpcFactionAddedEvent args)
    {
        if (args.FactionID != ent.Comp.Faction)
            _npcFaction.RemoveFaction(ent.Owner, args.FactionID);
    }

    /// <summary>Puts a freshly arrived mercenary in <paramref name="employer"/>'s squad.</summary>
    public bool Hire(EntityUid employer, EntityUid mercenary)
    {
        if (!HasComp<NpcMercenaryComponent>(mercenary))
            return false;

        // nobody is its side, so it has to know its employer before the squad will take it
        EnsureComp<NpcIffComponent>(mercenary).SquadLeader = employer;
        if (!_squad.TryRecruit(employer, mercenary))
            return false;

        EnsureComp<NpcMercenaryEmployerComponent>(employer);
        return true;
    }

    private void OnMercenaryDamaged(Entity<NpcMercenaryComponent> ent, ref DamageChangedEvent args)
    {
        if (_squad.TryGetLeader(ent, out var leader))
            Answer(leader, args);
    }

    private void OnEmployerDamaged(Entity<NpcMercenaryEmployerComponent> ent, ref DamageChangedEvent args)
    {
        Answer(ent, args);
    }

    private void Answer(EntityUid leader, DamageChangedEvent args)
    {
        if (!args.DamageIncreased ||
            args.Origin is not { } attacker ||
            attacker == leader ||
            !HasComp<MobStateComponent>(attacker) ||
            _mobState.IsIncapacitated(attacker) ||
            CompOrNull<NpcIffComponent>(attacker)?.SquadLeader == leader)
        {
            return;
        }

        foreach (var member in _squad.GetActiveMembers(leader).ToArray())
        {
            if (!TryComp<NpcMercenaryComponent>(member, out var mercenary) || member == attacker)
                continue;

            _hostility.AddTarget(member, attacker, mercenary.RetaliationTime);
            _squad.ForceReplan(member);
        }
    }
}
