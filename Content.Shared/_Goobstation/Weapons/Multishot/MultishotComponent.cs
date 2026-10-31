using Robust.Shared.GameStates;

namespace Content.Shared._Goobstation.Weapons.Multishot;

/// <summary>
/// Component that allows guns to be shooted with another weapon by holding it in second hand
/// </summary>
[RegisterComponent]
[NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MultishotComponent : Component
{
    /// <summary>
    /// Increasing spread when shooting with multiple hands
    /// </summary>
    [DataField, AutoNetworkedField]
    public float SpreadMultiplier = 1.5f;

    /// <summary>
    /// Fire rate divisor while dual wielding. Falls back to <see cref="SpreadMultiplier"/> when null.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float? FireRateMultiplier;

    [DataField, AutoNetworkedField]
    public float AngleIncreaseMultiplier = 1f;

    /// <summary>
    /// Flat spread in degrees added after the multiplier, so accurate guns still get worse.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float ExtraMinAngle;

    [DataField, AutoNetworkedField]
    public float ExtraMaxAngle;

    /// <summary>
    /// Uid of related weapon
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid? RelatedWeapon = default!;
}
