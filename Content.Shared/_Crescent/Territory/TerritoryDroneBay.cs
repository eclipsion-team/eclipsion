using Content.Shared._Crescent.Economy;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Serializable, NetSerializable]
public enum TerritoryDroneBayUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum TerritoryDroneOrder : byte
{
    Defend,
    Attack,
    Hold,
    Offline,
}

[Serializable, NetSerializable]
public enum TerritoryDroneCommand : byte
{
    Defend,
    Attack,
    Hold,
    PowerDown,
    PowerUp,
    Scuttle,
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneBayHull
{
    public string Id = string.Empty;
    public string Name = string.Empty;
    public int Price;
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneEntry
{
    public NetEntity Drone;
    public string Name = string.Empty;
    public bool Bought;
    public TerritoryDroneOrder Order;

    public string? Target;
    public bool Engaging;
    public bool Returning;
    public bool Unpowered;

    public int Distance;
    public int Integrity = 100;
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneTarget
{
    public NetEntity Target;
    public string Name = string.Empty;
    public string? Faction;
    public int Distance;
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneBayState : BoundUserInterfaceState
{
    public string? Owner;
    public int Level;
    public bool Powered;

    public bool CanCommand;

    // holder, but not ranked high enough in the faction
    public bool NoRank;

    public int Bought;
    public int Limit;

    // pickets still flying for a previous holder
    public bool OldPickets;

    public int AttackRange;

    public List<TerritoryDroneBayHull> Hulls = new();
    public List<TerritoryDroneEntry> Drones = new();
    public List<TerritoryDroneTarget> Targets = new();

    public TerritoryPaymentState? Payment;
}

[Serializable, NetSerializable]
public sealed class TerritoryDroneBayLaunchMessage(string hull, PaymentSource source) : BoundUserInterfaceMessage
{
    public string Hull { get; private set; } = hull;

    public PaymentSource Source { get; private set; } = source;
}

// drone null = every drone
[Serializable, NetSerializable]
public sealed class TerritoryDroneBayCommandMessage(NetEntity? drone, TerritoryDroneCommand command, NetEntity? target)
    : BoundUserInterfaceMessage
{
    public NetEntity? Drone { get; private set; } = drone;

    public TerritoryDroneCommand Command { get; private set; } = command;

    public NetEntity? Target { get; private set; } = target;
}
