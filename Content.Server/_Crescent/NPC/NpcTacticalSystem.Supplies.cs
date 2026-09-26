using System.Diagnostics.CodeAnalysis;
using Content.Server.Chemistry.EntitySystems;
using Content.Shared._Crescent.HardsuitInjection;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;

namespace Content.Server._Crescent.NPC;

public sealed partial class NpcTacticalSystem
{
    [Dependency] private readonly HypospraySystem _hypospray = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly NpcGunHandlingSystem _npcGun = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;

    private const string OuterClothingSlot = "outerClothing";

    /// <summary>
    /// The reagent that marks a medipen as the one for pulling someone back from critical.
    /// </summary>
    private const string StabilizerReagent = "Epinephrine";

    private static readonly string[] InjectorSlots =
    [
        HardsuitInjectorComponent.SlotOneId,
        HardsuitInjectorComponent.SlotTwoId,
    ];

    /// <summary>
    /// Stows the NPC's extra kit and loads its hardsuit's injectors.
    /// </summary>
    private void HandOutSupplies(Entity<NpcTacticalComponent> ent)
    {
        var coords = Transform(ent).Coordinates;

        foreach (var proto in ent.Comp.Supplies)
        {
            var item = Spawn(proto, coords);

            if (!_npcGun.TryStow(ent, item))
                Del(item);
        }

        if (ent.Comp.InjectorSupplies.Count == 0)
            return;

        _inventory.TryGetSlotEntity(ent, OuterClothingSlot, out var suit);

        for (var i = 0; i < ent.Comp.InjectorSupplies.Count; i++)
        {
            var pen = Spawn(ent.Comp.InjectorSupplies[i], coords);

            // A suit without injector slots, or more pens than slots: carry the rest instead.
            if (suit != null && i < InjectorSlots.Length &&
                _itemSlots.TryInsert(suit.Value, InjectorSlots[i], pen, null, excludeUserAudio: true))
            {
                continue;
            }

            if (!_npcGun.TryStow(ent, pen))
                Del(pen);
        }
    }

    /// <summary>
    /// A loaded medipen the NPC carries - one with a stabilizer in it first if <paramref name="critical"/>.
    /// Pens loaded into the hardsuit's injector don't count: the suit uses those itself.
    /// </summary>
    public bool TryFindMedipen(EntityUid carrier, bool critical, [NotNullWhen(true)] out Entity<HyposprayComponent>? pen)
    {
        pen = null;

        foreach (var candidate in EnumerateCarried(carrier))
        {
            if (!TryComp<HyposprayComponent>(candidate, out var hypospray) ||
                !_solutions.TryGetSolution(candidate, hypospray.SolutionName, out _, out var solution) ||
                solution.Volume <= 0)
            {
                continue;
            }

            if (!critical || solution.ContainsPrototype(StabilizerReagent))
            {
                pen = (candidate, hypospray);
                return true;
            }

            pen ??= (candidate, hypospray);
        }

        return pen != null;
    }

    /// <summary>
    /// Whether <paramref name="npc"/> would put a medipen into <paramref name="patient"/> right now.
    /// </summary>
    public bool CanUseMedipen(EntityUid npc, EntityUid patient, NpcTacticalComponent comp)
    {
        if (_timing.CurTime < comp.NextMedipen)
            return false;

        var critical = _mobState.IsCritical(patient);

        if (!critical && (!TryGetDamageFraction(patient, out var fraction) || fraction < comp.MedipenThreshold))
            return false;

        return TryFindMedipen(npc, critical, out _);
    }

    /// <summary>
    /// Puts a medipen into <paramref name="patient"/> if it's hurt badly enough and one hasn't gone in too
    /// recently. The squad leader has their own cooldown shared across the squad, so two squadmates don't
    /// both overdose them.
    /// </summary>
    public bool TryUseMedipen(EntityUid npc, EntityUid patient, NpcTacticalComponent comp)
    {
        if (!CanUseMedipen(npc, patient, comp) ||
            !TryFindMedipen(npc, _mobState.IsCritical(patient), out var pen))
        {
            return false;
        }

        if (patient != npc && !_squad.CanMedipenLeader(patient))
            return false;

        if (!_hypospray.TryDoInject(pen.Value, patient, npc))
            return false;

        // Only once it's in: a failed injection shouldn't lock the rest of the squad out.
        if (patient != npc)
            _squad.StartLeaderMedipenCooldown(patient, comp.MedipenCooldown);

        comp.NextMedipen = _timing.CurTime + comp.MedipenCooldown;
        return true;
    }
}
