using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.AlertConsole;

[NetworkedComponent, RegisterComponent, AutoGenerateComponentState(true)]
public sealed partial class AlertConsoleComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled = false;

    [DataField, AutoNetworkedField]
    public float DetectionRadius = 200f;

    [DataField, AutoNetworkedField]
    public string FactionChannel = "";

    /// <summary>
    /// Minimum grid linear velocity (m/s) to count as approaching.
    /// </summary>
    [DataField]
    public float MinDetectionVelocity = 0.5f;

    [DataField, AutoNetworkedField]
    public string StationAlertMessage = "{name} is approaching the station at {dist} meters!";

    [DataField, AutoNetworkedField]
    public bool BroadcastToShuttle = true;

    [DataField, AutoNetworkedField]
    public string ShuttleAlertMessage = "{name}, you have entered a secured zone. State your faction affiliation and purpose of visit.";

    /// <summary>
    /// Floor on how often the same shuttle can be warned about. Only reachable once the shuttle has left
    /// and come back - a shuttle that simply stays in range is warned about once, not once per cooldown.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float AlertCooldownSeconds = 60f;

    /// <summary>
    /// Multiple of <see cref="DetectionRadius"/> a shuttle has to get past before its approach is considered
    /// over. Pure hysteresis: without it a shuttle holding position on the edge of the radius would count as
    /// leaving and re-entering every time it drifted across the line.
    /// </summary>
    [DataField]
    public float ReArmDistanceFactor = 1.25f;

    [DataField]
    public float ScanInterval = 5f;

    [ViewVariables]
    public float ScanAccumulator = 0f;

    /// <summary>
    /// What this console remembers about each shuttle it has seen, keyed by grid. Runtime scan bookkeeping,
    /// so it is neither saved nor networked.
    /// </summary>
    [ViewVariables]
    public Dictionary<EntityUid, AlertTrackedShuttle> TrackedShuttles = new();
}

/// <summary>
/// One shuttle's alert state on an <see cref="AlertConsoleComponent"/>, carried between scans.
/// </summary>
public struct AlertTrackedShuttle
{
    /// <summary>
    /// When this shuttle was last warned about.
    /// </summary>
    public TimeSpan LastAlert;

    /// <summary>
    /// Set once the current approach has been warned about, cleared when the shuttle leaves the re-arm
    /// radius. This is what keeps one approach to one alert however long the shuttle loiters in range.
    /// </summary>
    public bool Alerted;
}

[Serializable, NetSerializable]
public enum AlertConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class AlertConsoleBuiState : BoundUserInterfaceState
{
    public readonly bool Enabled;
    public readonly float DetectionRadius;
    public readonly string FactionChannel;
    public readonly bool FactionChannelResolved;
    public readonly string StationAlertMessage;
    public readonly bool BroadcastToShuttle;
    public readonly string ShuttleAlertMessage;
    public readonly float AlertCooldownSeconds;

    public AlertConsoleBuiState(
        bool enabled,
        float detectionRadius,
        string factionChannel,
        bool factionChannelResolved,
        string stationAlertMessage,
        bool broadcastToShuttle,
        string shuttleAlertMessage,
        float alertCooldownSeconds)
    {
        Enabled = enabled;
        DetectionRadius = detectionRadius;
        FactionChannel = factionChannel;
        FactionChannelResolved = factionChannelResolved;
        StationAlertMessage = stationAlertMessage;
        BroadcastToShuttle = broadcastToShuttle;
        ShuttleAlertMessage = shuttleAlertMessage;
        AlertCooldownSeconds = alertCooldownSeconds;
    }
}

[Serializable, NetSerializable]
public sealed class AlertConsoleSaveSettingsMessage : BoundUserInterfaceMessage
{
    public readonly bool Enabled;
    public readonly float DetectionRadius;
    public readonly string StationAlertMessage;
    public readonly bool BroadcastToShuttle;
    public readonly string ShuttleAlertMessage;
    public readonly float AlertCooldownSeconds;

    public AlertConsoleSaveSettingsMessage(
        bool enabled,
        float detectionRadius,
        string stationAlertMessage,
        bool broadcastToShuttle,
        string shuttleAlertMessage,
        float alertCooldownSeconds)
    {
        Enabled = enabled;
        DetectionRadius = detectionRadius;
        StationAlertMessage = stationAlertMessage;
        BroadcastToShuttle = broadcastToShuttle;
        ShuttleAlertMessage = shuttleAlertMessage;
        AlertCooldownSeconds = alertCooldownSeconds;
    }
}
