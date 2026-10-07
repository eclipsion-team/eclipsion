using Content.Shared.Crescent.Radar;
using Content.Shared.Shuttles.BUIStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.DroneControl;

[Serializable, NetSerializable]
public enum DroneConsoleUiKey : byte
{
    Key
}

/// <summary>
///     Per-drone readout shown on the console's status panel.
/// </summary>
[Serializable, NetSerializable]
public struct DroneStatusEntry
{
    /// <summary>The drone's control server, used as its identity for selection and orders.</summary>
    public NetEntity Server;

    /// <summary>The grid the server sits on, used for the radar label.</summary>
    public NetEntity Grid;

    /// <summary>Display name of the drone grid.</summary>
    public string Name;

    /// <summary>What the drone is currently doing.</summary>
    public AutoDroneMode Mode;

    /// <summary>Fraction (0..1) of the drone's original hull tiles still intact.</summary>
    public float HullIntegrity;

    /// <summary>Whether the drone is powered. Unpowered drones drift and can't be commanded.</summary>
    public bool Powered;

    /// <summary>Seconds left on a running self destruct, or null if none is armed.</summary>
    public float? SelfDestructIn;
}

/// <summary>
///     Vessel the console can produce, with its current billed price.
/// </summary>
[Serializable, NetSerializable]
public struct DroneSpawnEntry
{
    /// <summary>Vessel prototype id, echoed back in <see cref="DroneConsoleSpawnMessage"/>.</summary>
    public string VesselId;

    /// <summary>Human readable vessel name.</summary>
    public string Name;

    /// <summary>What producing it costs the treasury, or 0 if production is free.</summary>
    public int Price;

    /// <summary>Hangar slots a drone of this pattern takes up on the carrier.</summary>
    public int HangarCost;
}

[Serializable, NetSerializable]
public sealed class DroneConsoleBoundUserInterfaceState : BoundUserInterfaceState
{
    public NavInterfaceState NavState;
    public IFFInterfaceState IFFState;

    /// <summary>Status of every drone this carrier currently commands.</summary>
    public List<DroneStatusEntry> Drones;

    // Carrier controls. IsCarrier false => hide the carrier control panel.
    public bool IsCarrier;
    public DroneStance Stance;
    public DroneTargeting Targeting;
    public DroneFormation Formation;

    /// <summary>Hangar slots used up over the console's lifetime - this is what the production cap counts.</summary>
    public int ProducedCount;

    /// <summary>Hangar slots taken by the drones still alive and under command.</summary>
    public int AliveCount;

    /// <summary>Total hangar slots of the carrier.</summary>
    public int MaxDrones;

    /// <summary>Hangar slots of drones since destroyed or written off, waiting on a repair station restock.</summary>
    public int LostCount;

    /// <summary>Hangar slots still free to produce drones in before the hangar runs dry.</summary>
    public int HangarCount;

    public List<DroneSpawnEntry> SpawnableDrones;

    /// <summary>Funds the console can draw on to produce drones, or null if production is free.</summary>
    public int? Treasury;

    public DroneConsoleBoundUserInterfaceState(
        NavInterfaceState navState,
        IFFInterfaceState iffState,
        List<DroneStatusEntry> drones,
        bool isCarrier,
        DroneStance stance,
        DroneTargeting targeting,
        DroneFormation formation,
        int producedCount,
        int aliveCount,
        int maxDrones,
        int lostCount,
        int hangarCount,
        List<DroneSpawnEntry> spawnableDrones,
        int? treasury)
    {
        LostCount = lostCount;
        HangarCount = hangarCount;
        NavState = navState;
        IFFState = iffState;
        Drones = drones;
        IsCarrier = isCarrier;
        Stance = stance;
        Targeting = targeting;
        Formation = formation;
        ProducedCount = producedCount;
        AliveCount = aliveCount;
        MaxDrones = maxDrones;
        SpawnableDrones = spawnableDrones;
        Treasury = treasury;
    }
}

/// <summary>
///     Sent when the player picks the drones' targeting scope (enemies only / all non-friendly).
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleSetTargetingMessage : BoundUserInterfaceMessage
{
    public DroneTargeting Targeting;

    public DroneConsoleSetTargetingMessage(DroneTargeting targeting)
    {
        Targeting = targeting;
    }
}

/// <summary>
///     Sent when the player produces a drone of the given vessel prototype id from the carrier console.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleSpawnMessage : BoundUserInterfaceMessage
{
    public string VesselId;

    public DroneConsoleSpawnMessage(string vesselId)
    {
        VesselId = vesselId;
    }
}

/// <summary>
///     Sent when the player picks a combat stance for the carrier's drones.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleSetStanceMessage : BoundUserInterfaceMessage
{
    public DroneStance Stance;

    public DroneConsoleSetStanceMessage(DroneStance stance)
    {
        Stance = stance;
    }
}

/// <summary>
///     Sent when the player picks a formation for the carrier's drones.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleSetFormationMessage : BoundUserInterfaceMessage
{
    public DroneFormation Formation;

    public DroneConsoleSetFormationMessage(DroneFormation formation)
    {
        Formation = formation;
    }
}

/// <summary>
///     Sent when the player asks the carrier to deploy its docked drones.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleDeployMessage : BoundUserInterfaceMessage
{
}

/// <summary>
///     Sent when the player orders the selected drones to scuttle themselves. <see cref="Cancel"/> disarms a
///     countdown that is already running instead of starting one.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleSelfDestructMessage : BoundUserInterfaceMessage
{
    public HashSet<NetEntity> SelectedDrones;
    public bool Cancel;

    public DroneConsoleSelfDestructMessage(HashSet<NetEntity> selectedDrones, bool cancel)
    {
        SelectedDrones = selectedDrones;
        Cancel = cancel;
    }
}

/// <summary>
///     Sent when the client determines the click was in empty space.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleMoveMessage : BoundUserInterfaceMessage
{
    public HashSet<NetEntity> SelectedDrones;
    public NetCoordinates TargetCoordinates;

    public DroneConsoleMoveMessage(HashSet<NetEntity> selectedDrones, NetCoordinates targetCoordinates)
    {
        SelectedDrones = selectedDrones;
        TargetCoordinates = targetCoordinates;
    }
}

/// <summary>
///     Sent when the client determines the click hit a grid.
/// </summary>
[Serializable, NetSerializable]
public sealed class DroneConsoleTargetMessage : BoundUserInterfaceMessage
{
    public HashSet<NetEntity> SelectedDrones;
    public NetCoordinates TargetCoordinates;

    public DroneConsoleTargetMessage(HashSet<NetEntity> selectedDrones, NetCoordinates targetCoordinates)
    {
        SelectedDrones = selectedDrones;
        TargetCoordinates = targetCoordinates;
    }
}

/// <summary>
///     Constants for DeviceNetwork packet keys.
/// </summary>
public static class DroneConsoleConstants
{
    public const string CommandMove = "drone_cmd_move";
    public const string CommandTarget = "drone_cmd_target";
    public const string TargetCoords = "target";
}

public enum DroneOrderType
{
    Move,
    Target
}
