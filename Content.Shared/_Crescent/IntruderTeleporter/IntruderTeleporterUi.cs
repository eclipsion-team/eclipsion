using System;
using System.Collections.Generic;
using Content.Shared.Shuttles.BUIStates;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.IntruderTeleporter;

[Serializable, NetSerializable]
public enum IntruderTeleporterUiKey : byte
{
    Key,
}

/// <summary>
/// Why the console will or will not fire at whatever it is currently aimed at.
/// </summary>
[Serializable, NetSerializable]
public enum IntruderTargetStatus : byte
{
    /// <summary>Nothing picked yet.</summary>
    None,

    Valid,

    /// <summary>Picked, but it has since drifted or FTLed beyond the console's range.</summary>
    OutOfRange,

    /// <summary>Picked, but the console's grid is not at war with it.</summary>
    NotHostile,

    /// <summary>Picked, but it no longer exists or has left the map.</summary>
    Lost,
}

/// <summary>
/// One name on the roster.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntruderSquadMember
{
    public NetEntity Entity;
    public string Name;

    /// <summary>
    /// Whether this member is carrying the access the console wants for a launch order. The console shows
    /// them as the squad lead, since they are the only ones who can actually send it.
    /// </summary>
    public bool CanCommand;

    public IntruderSquadMember(NetEntity entity, string name, bool canCommand)
    {
        Entity = entity;
        Name = name;
        CanCommand = canCommand;
    }
}

[Serializable, NetSerializable]
public sealed class IntruderTeleporterBuiState : BoundUserInterfaceState
{
    /// <summary>
    /// Scanner feed, reusing the shuttle radar so the console draws real contacts rather than a list.
    /// </summary>
    public NavInterfaceState Nav;

    public List<IntruderSquadMember> Squad;
    public int MaxSquadSize;
    public float Range;
    public float WarningDelay;

    public NetEntity? SelectedTarget;
    public string? SelectedTargetName;
    public float SelectedTargetDistance;
    public IntruderTargetStatus SelectedTargetStatus;

    /// <summary>
    /// Null when nothing is in flight; otherwise when the squad lands.
    /// </summary>
    public TimeSpan? LaunchAt;
    public string? LaunchTargetName;

    /// <summary>
    /// Null when the console is ready; otherwise when it will be.
    /// </summary>
    public TimeSpan? CooldownEnd;

    public IntruderTeleporterBuiState(NavInterfaceState nav, List<IntruderSquadMember> squad)
    {
        Nav = nav;
        Squad = squad;
    }
}

/// <summary>
/// Sign on, or sign back off if already on the roster.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntruderTeleporterToggleJoinMessage : BoundUserInterfaceMessage;

/// <summary>
/// Aim the console at whatever grid sits under the point clicked on the scanner.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntruderTeleporterSelectTargetMessage : BoundUserInterfaceMessage
{
    public readonly NetCoordinates Coordinates;

    public IntruderTeleporterSelectTargetMessage(NetCoordinates coordinates)
    {
        Coordinates = coordinates;
    }
}

/// <summary>
/// Send the roster. Requires access.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntruderTeleporterLaunchMessage : BoundUserInterfaceMessage;

/// <summary>
/// Call an in-flight launch back before it lands. Requires access.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntruderTeleporterAbortMessage : BoundUserInterfaceMessage;
