using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.DegradeableArmor;

/// <summary>
/// This is used for...
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DegradeableArmorComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public float armorMaxHealth = 500f;
    /// <summary>
    /// Leave at 0 for it to get automatically set to armor max health(unless you want it hardset at lower or higher)
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public float armorHealth;

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public Dictionary<string, float> armorDamageCoefficients = new Dictionary<string, float>()
    {
        {"Blunt", 1.4f},
        {"Slash", 1.7f},
        {"Piercing", 1f},
        {"Heat", 1f},
        {"Caustic", 10f},
        {"Radiation", 0.1f}
    };

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public ArmorDegradation armorType = ArmorDegradation.Metallic;

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public ArmorRepairMaterial armorRepair = ArmorRepairMaterial.PlasteelPlate;

    /// <summary>
    /// Sound played when the armor is fully broken (armorHealth <= 0)
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public SoundSpecifier? breakSound = new SoundPathSpecifier("/Audio/_Crescent/Effects/armor_break.ogg");

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public DamageModifierSet initialModifiers = default!;

    /// <summary>
    ///  This is shitty but there is no wizden implementation for getting the wearer of a piece of clothing
    /// </summary>
    ///
    [ViewVariables(VVAccess.ReadWrite)]
    public EntityUid wearer = EntityUid.Invalid;

}
[Serializable, NetSerializable]
public enum ArmorDegradation
{
    Ceramic = 1, // Resists armor penetration, but cracks fast and protection falls off quadratically. Impact becomes stamina damage.
    Metallic = 1<<1, // Weak to armor penetration, wears slowly with a protection floor. Impact becomes blunt trauma.
}

