using Robust.Shared.Serialization;

namespace Content.Shared.PointCannons;

[Serializable, NetSerializable]
public enum ShipWeaponTargetingMode : byte
{
    Walls,
    TilesAndWalls,
}

[Serializable, NetSerializable]
public sealed class ShipWeaponTargetingModeMessage(ShipWeaponTargetingMode mode) : BoundUserInterfaceMessage
{
    public ShipWeaponTargetingMode Mode = mode;
}
