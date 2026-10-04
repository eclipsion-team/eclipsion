namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Scales the damage and stamina damage of every fight while the rule is active, so a Great Hunt variant can make
/// fights last longer. Great Hunt: Blades halves both.
/// </summary>
[RegisterComponent]
public sealed partial class GreatHuntCombatScaleRuleComponent : Component
{
    /// <summary>Multiplier on the damage of every melee and thrown weapon.</summary>
    [DataField]
    public float DamageMultiplier = 1f;

    /// <summary>
    /// Multiplier on stamina damage dealt by someone else: melee and stun hits, shoves, parried blows, bolas.
    /// Stamina worked out from blunt damage is left alone, as <see cref="DamageMultiplier"/> has already scaled it.
    /// Stamina spent on one's own swings and sprinting is never touched.
    /// </summary>
    [DataField]
    public float StaminaDamageMultiplier = 1f;
}
