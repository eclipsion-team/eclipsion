using Content.Shared._Crescent.Economy;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Serializable, NetSerializable]
public enum TerritoryDroneBayUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneBayHull
{
    public string Id = string.Empty;
    public string Name = string.Empty;
    public int Price;
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneBayState : BoundUserInterfaceState
{
    public string? Owner;
    public int Level;
    public bool Powered;

    public int Drones;
    public int Limit;

    // pickets still flying for a previous holder
    public bool OldPickets;

    public List<TerritoryDroneBayHull> Hulls = new();

    public TerritoryPaymentState? Payment;
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneBayLaunchMessage(string hull, PaymentSource source) : BoundUserInterfaceMessage
{
    public string Hull { get; private set; } = hull;

    public PaymentSource Source { get; private set; } = source;
}
