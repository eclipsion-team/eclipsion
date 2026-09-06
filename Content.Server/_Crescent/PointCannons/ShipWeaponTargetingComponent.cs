using Content.Shared.PointCannons;

namespace Content.Server.PointCannons;

/// <summary>Selection on a console, or the console supplying a gun's current fire order.</summary>
[RegisterComponent]
public sealed partial class ShipWeaponTargetingComponent : Component
{
    [DataField]
    public ShipWeaponTargetingMode Mode;

    public EntityUid? Console;
}
