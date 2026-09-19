using Content.Shared.Crescent.Psionics;
using Content.Shared._Crescent.ShieldBelt;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Clothing;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Popups;
using Content.Shared.StatusEffect;

namespace Content.Server._Crescent.ShieldBelt;

/// <summary>
/// Hangs psionic insulation off a wearable shield's barrier: up means insulated, down means wide open.
/// Everything about the barrier itself - damage, charge, recharge delay, and the hotbar action that raises it -
/// is left to the shared clothing shield stack, so the counterplay is simply to shoot it down.
/// </summary>
public sealed class ShieldBeltSystem : EntitySystem
{
    /// <summary>Chems and similar effects hang insulation off this rather than off an item.</summary>
    private const string InsulatedStatusEffect = "PsionicallyInsulated";

    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PsionicNullifiedSystem _nullified = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShieldBeltComponent, ClothingGotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<ShieldBeltComponent, ClothingGotUnequippedEvent>(OnUnequipped);
        SubscribeLocalEvent<ShieldBeltComponent, ItemToggledEvent>(OnToggled);
        SubscribeLocalEvent<ShieldBeltComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnEquipped(Entity<ShieldBeltComponent> ent, ref ClothingGotEquippedEvent args)
    {
        ent.Comp.Wearer = args.Wearer;

        if (_toggle.IsActivated(ent.Owner))
            SetInsulated(ent, true);
    }

    private void OnUnequipped(Entity<ShieldBeltComponent> ent, ref ClothingGotUnequippedEvent args)
    {
        SetInsulated(ent, false);
        ent.Comp.Wearer = null;
    }

    private void OnToggled(Entity<ShieldBeltComponent> ent, ref ItemToggledEvent args)
    {
        // Also fires on map init, before anyone is wearing this.
        if (ent.Comp.Wearer is not { } wearer)
            return;

        SetInsulated(ent, args.Activated);

        _popup.PopupEntity(
            Loc.GetString(args.Activated ? "shield-belt-barrier-raised" : "shield-belt-barrier-collapsed"),
            wearer,
            wearer,
            args.Activated ? PopupType.Medium : PopupType.LargeCaution);
    }

    private void OnShutdown(Entity<ShieldBeltComponent> ent, ref ComponentShutdown args)
    {
        SetInsulated(ent, false);
    }

    private void SetInsulated(Entity<ShieldBeltComponent> ent, bool insulated)
    {
        if (ent.Comp.Wearer is not { } wearer || ent.Comp.Insulating == insulated)
            return;

        ent.Comp.Insulating = insulated;

        if (insulated)
        {
            var insulation = EnsureComp<PsionicInsulationComponent>(wearer);
            insulation.Passthrough = ent.Comp.Passthrough;
            return;
        }

        // Something else may be holding the wearer's insulation up, and yanking the component would silently
        // cancel theirs along with ours.
        if (!HasOtherInsulationSource(wearer, ent.Owner))
            RemComp<PsionicInsulationComponent>(wearer);
    }

    private bool HasOtherInsulationSource(EntityUid wearer, EntityUid ignore)
    {
        if (_statusEffects.HasStatusEffect(wearer, InsulatedStatusEffect))
            return true;

        // A null field needs the insulation under it. Born with one, it was never ours to strip; worn, the
        // nullifier takes the insulation over and strips it itself.
        if (_nullified.ClaimsInsulation(wearer))
            return true;

        var slots = _inventory.GetSlotEnumerator(wearer);
        while (slots.NextItem(out var item))
        {
            if (item == ignore)
                continue;

            if (TryComp<TinfoilHatComponent>(item, out var tinfoil) && tinfoil.IsActive)
                return true;

            if (TryComp<ShieldBeltComponent>(item, out var otherBelt) && otherBelt.Insulating)
                return true;
        }

        return false;
    }
}
