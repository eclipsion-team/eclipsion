using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.RepairStation;

[Serializable, NetSerializable]
public enum ShipRepairStationUiKey : byte
{
    Key
}

/// <summary>
/// One ship sitting in the station's docking clamps.
/// </summary>
[Serializable, NetSerializable]
public struct ShipRepairDockEntry
{
    public NetEntity Grid;
    public string Name;

    /// <summary>
    /// False when the ship carries no structural snapshot, so nothing can be costed or rebuilt.
    /// </summary>
    public bool HasBlueprint;
}

/// <summary>
/// Why a survey came back with nothing to do. Kept as an enum so the client picks the wording.
/// </summary>
[Serializable, NetSerializable]
public enum ShipRepairStatus : byte
{
    NoShip,
    NoBlueprint,
    Intact,
    Quoted,
    Repairing,
    Busy
}

[Serializable, NetSerializable]
public sealed class ShipRepairStationUiState : BoundUserInterfaceState
{
    public List<ShipRepairDockEntry> Docked = new();
    public NetEntity? Selected;
    public ShipRepairStatus Status;

    /// <summary>
    /// Tiles the blueprint records that are now open to space.
    /// </summary>
    public int MissingTiles;

    /// <summary>
    /// Tiles beaten down to the bare layer underneath - decking blown off its plating - which the
    /// slip covers back over.
    /// </summary>
    public int StrippedTiles;

    public int MissingParts;
    public int DamagedParts;

    /// <summary>
    /// Deck markings the hull has lost - the painted stripes and hazard blocks that went with the
    /// plating they were laid on.
    /// </summary>
    public int Decals;

    public int Spills;

    /// <summary>
    /// Pieces of loose wreckage to bin.
    /// </summary>
    public int Debris;

    public int Restocks;

    /// <summary>
    /// Quoted price with the yard's markup already applied.
    /// </summary>
    public int Quote;

    public int JobsTotal;
    public int JobsDone;

    /// <summary>
    /// When the running job started and when it is expected to finish, so the window can draw a
    /// smooth bar and count the ETA down without a state push every frame.
    /// </summary>
    public TimeSpan StartTime;

    public TimeSpan EndTime;

    /// <summary>
    /// Name of the ship being worked on, which may not be the one selected in the list.
    /// </summary>
    public string? RepairingName;

    /// <summary>
    /// The slip is doing the work for nothing, so the window drops the money out of the quote and
    /// stops asking whether the viewer can cover it.
    /// </summary>
    public bool Free;

    /// <summary>
    /// The slip may file a blueprint of the hull it is pointed at, so the window offers the button.
    /// </summary>
    public bool CanSnapshot;

    /// <summary>
    /// The hull in front of the slip has no blueprint on file, and the slip is working anyway - so the
    /// window says what that leaves out rather than the survey silently reading as a sound hull.
    /// </summary>
    public bool BlueprintMissing;
}

[Serializable, NetSerializable]
public sealed class ShipRepairSelectMessage : BoundUserInterfaceMessage
{
    public NetEntity Grid;

    public ShipRepairSelectMessage(NetEntity grid)
    {
        Grid = grid;
    }
}

[Serializable, NetSerializable]
public sealed class ShipRepairStartMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ShipRepairCancelMessage : BoundUserInterfaceMessage;

/// <summary>
/// Files the hull in front of the slip as it stands right now, so a hull that never came off a
/// shipyard - a station, a mapped-in fleet ship - has something to be restored to later.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipRepairSnapshotMessage : BoundUserInterfaceMessage;
