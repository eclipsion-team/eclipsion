namespace Content.Server.PointCannons;

/// <summary>Structural damage from direct ship-weapon hits to the grid's current floor layers.</summary>
[RegisterComponent]
public sealed partial class ShipWeaponTileDamageComponent : Component
{
    public readonly Dictionary<Vector2i, float> Damage = new();
}
