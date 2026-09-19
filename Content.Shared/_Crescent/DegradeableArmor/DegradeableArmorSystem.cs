using Content.Shared.ArachnidChaos;
using Content.Shared.Armor;
using Content.Shared.Chasm;
using Content.Shared.Chat;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Popups;
using Content.Shared.Tools;
using Content.Shared.Tools.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._Crescent.DegradeableArmor;

[Serializable, NetSerializable]
public enum ArmorRepairMaterial
{
    PlasteelPlate = 1 << 0,
    NTPolymer = 1 << 1,
    CeramicPlate = 1 << 2,
    SteelPlate = 1 << 3,
    DuraThread = 1 << 4,
    PlasmaGlass = 1 << 5,
    Plastic = 1 << 6,
    HomelandAlloy = 1 << 7,
    Kevlar = 1 << 8,
    PlasteelEncasedKevlar = 1 << 9,
    NTCeramic = 1 << 10

}
[Serializable, NetSerializable]
public partial class ArmorRepairDoAfterEvent : SimpleDoAfterEvent
{

}
/// <summary>
/// This handles...
/// </summary>
public sealed class DegradeableArmorSystem : EntitySystem
{
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedDoAfterSystem _doing = default!;
    [Dependency] private readonly ClothingSystem _cloth = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedToolSystem _toolSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private const string conversionPrototype = "PiercingInducedBlunt";

    // Ceramic: breaks up armor-piercing rounds, but cracks fast and loses protection quadratically.
    private const float CeramicPenFactor = 0.5f;
    private const float CeramicWearFactor = 1.25f;
    // Metallic: armor-piercing goes straight through, but wears slowly and keeps a residual floor of protection.
    private const float MetallicPenFactor = 1f;
    private const float MetallicWearFactor = 0.85f;
    private const float MetallicProtectionFloor = 0.25f;

    /// <summary>
    /// Fraction of the flat reduction the armor still provides at its current health.
    /// </summary>
    private static float GetProtectionFactor(DegradeableArmorComponent component)
    {
        if (component.armorHealth <= 0 || component.armorMaxHealth <= 0)
            return 0f;

        var ratio = Math.Clamp(component.armorHealth / component.armorMaxHealth, 0f, 1f);
        return component.armorType switch
        {
            ArmorDegradation.Ceramic => ratio * ratio,
            _ => MetallicProtectionFloor + (1f - MetallicProtectionFloor) * ratio,
        };
    }
    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<DegradeableArmorComponent, MapInitEvent>(OnInit);
        SubscribeLocalEvent<DegradeableArmorComponent, InventoryRelayedEvent<DamageModifyEvent>>(OnDamageModify);
        SubscribeLocalEvent<DegradeableArmorComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<DegradeableArmorComponent, ClothingGotEquippedEvent>(afterEquipped);
        SubscribeLocalEvent<DegradeableArmorComponent, ClothingGotUnequippedEvent>(afterDeequip);
        SubscribeLocalEvent<DegradeableArmorComponent, ArmorRepairDoAfterEvent>(OnRepair);
        SubscribeLocalEvent<DegradeableArmorComponent, ExaminedEvent>(OnArmorExamine);
    }

    private void OnInteractUsing(Entity<DegradeableArmorComponent> owner, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;
        if (owner.Comp.armorHealth == owner.Comp.armorMaxHealth)
        {
            _popup.PopupEntity(Loc.GetString("degradeable-armor-not-damaged"), owner.Owner, args.User, PopupType.Medium);
            return;
        }

        if (_inventory.TryGetContainingSlot(owner.Owner, out var def))
        {
            _popup.PopupClient(Loc.GetString("degradeable-armor-cant-repair-worn"), args.User, args.User, PopupType.Medium);
            return;
        }

        args.Handled = _toolSystem.UseTool(args.Used, args.User, owner.Owner, 15, "Welding", new ArmorRepairDoAfterEvent(), 50);

    }

    private void OnRepair(Entity<DegradeableArmorComponent> owner, ref ArmorRepairDoAfterEvent args)
    {
        if(args.Cancelled)
            return;

        owner.Comp.armorHealth = owner.Comp.armorMaxHealth;
        if (TryComp<ToggleableClothingComponent>(owner.Owner, out var component))
        {
            foreach (var (ClothingUid, _) in component.ClothingUids)
            {
                if (TryComp<DegradeableArmorComponent>(ClothingUid, out var headwearArmor))
                {
                    headwearArmor.armorHealth = headwearArmor.armorMaxHealth;
                }
            }

        }
    }

    private void afterEquipped(EntityUid owner, DegradeableArmorComponent comp, ref ClothingGotEquippedEvent args)
    {
        comp.wearer = args.Wearer;
    }

    private void afterDeequip(EntityUid owner, DegradeableArmorComponent comp, ref ClothingGotUnequippedEvent args)
    {
        comp.wearer = EntityUid.Invalid;
    }
    private void OnArmorExamine(EntityUid owner, DegradeableArmorComponent component, ref ExaminedEvent args)
    {
        args.PushMessage(GetArmorExamine(component));
    }
    private FormattedMessage GetArmorExamine(DegradeableArmorComponent component)
    {
        var msg = new FormattedMessage();

        msg.AddMarkup(Loc.GetString("armor-examine"));

        foreach (var flatArmor in component.initialModifiers.FlatReduction)
        {
            msg.PushNewline();

            var armorType = Loc.GetString("armor-damage-type-" + flatArmor.Key.ToLowerInvariant());
            msg.AddMarkup(Loc.GetString("armor-reduction-value",
                ("type", armorType),
                ("value", (int)(flatArmor.Value * GetProtectionFactor(component)))
            ));
        }

        msg.PushNewline();
        var armorMaterial = Loc.GetString($"armor-degradation-{component.armorType.ToString().ToLowerInvariant()}");
        msg.AddMarkup(Loc.GetString("armor-material-examine", ("material", armorMaterial)));

        return msg;
    }
    private void OnInit(EntityUid uid, DegradeableArmorComponent component, ref MapInitEvent args)
    {
        if(component.armorHealth == 0)
            component.armorHealth = component.armorMaxHealth;
    }
    private void OnDamageModify(EntityUid uid, DegradeableArmorComponent component, InventoryRelayedEvent<DamageModifyEvent> args)
    {
        if (component.armorHealth <= 0)
            return;
        var armorDamage = 0f;
        var blockedAny = false;
        var protection = GetProtectionFactor(component);
        var (penFactor, wearFactor) = component.armorType switch
        {
            ArmorDegradation.Ceramic => (CeramicPenFactor, CeramicWearFactor),
            _ => (MetallicPenFactor, MetallicWearFactor),
        };

        var damageDictionary = args.Args.Damage.DamageDict;
        damageDictionary.TryAdd(conversionPrototype, 0);
        foreach (var (type, value) in damageDictionary)
        {
            if (!component.initialModifiers.FlatReduction.ContainsKey(type))
                continue;
            if (value < 0)
                continue;
            var trueReduction = component.initialModifiers.FlatReduction[type];
            if (trueReduction == 0)
                continue;

            trueReduction = Math.Clamp(trueReduction * protection - args.Args.HullrotArmorPen * penFactor, 0f, (float) value);
            if (trueReduction > 0)
                blockedAny = true;
            // Safe access: if a damage type isn't in the coefficients dict, default to 1.0 (full armor damage).
            var coeff = component.armorDamageCoefficients.TryGetValue(type, out var c) ? c : 1f;
            armorDamage += (float) value * coeff * wearFactor;
            damageDictionary[type] = Math.Max(0f, (float) value - trueReduction);
        }

        // The impact of a stopped round is transferred to the wearer once per hit, not once per damage type.
        if (blockedAny && args.Args.stoppingPower > 0)
        {
            switch (component.armorType)
            {
                // Ceramic shatters to absorb the impact, the shock is felt as stamina damage.
                case ArmorDegradation.Ceramic:
                    if (component.wearer != EntityUid.Invalid)
                        _stamina.TakeStaminaDamage(component.wearer, args.Args.stoppingPower);
                    break;
                // Metal deforms inward, the impact comes through as blunt trauma.
                case ArmorDegradation.Metallic:
                    damageDictionary[conversionPrototype] += args.Args.stoppingPower;
                    break;
            }
        }
        var healthBefore = component.armorHealth;
        component.armorHealth = Math.Max(0, component.armorHealth - armorDamage);
        if (healthBefore > 0 && component.armorHealth <= 0 && component.breakSound != null)
        {
            _audio.PlayPredicted(component.breakSound, uid, component.wearer != EntityUid.Invalid ? component.wearer : uid);
        }
        Dirty(uid, component);
    }

}
