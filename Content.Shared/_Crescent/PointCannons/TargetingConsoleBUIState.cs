using System.Numerics;
using Content.Shared.Crescent.Radar;
using Content.Shared.Shuttles.BUIStates;
using Robust.Shared.Serialization;

namespace Content.Shared.PointCannons;

[Serializable, NetSerializable]
public sealed class TargetingConsoleBoundUserInterfaceState : BoundUserInterfaceState
{
    public NavInterfaceState NavState;
    public IFFInterfaceState IFFState;
    public List<string>? CannonGroups;
    public List<NetEntity>? ControlledCannons;

    /// <summary>
    ///     KS14: which groups are currently selected on this console, so the UI can light them.
    ///         Group selection is a toggle set with its own rules on the server ("all" clears the
    ///         rest, pressing an active group drops it), and the console used to show none of it -
    ///         a click had no visible effect at all. Sent rather than mirrored client-side so the
    ///         highlight cannot drift from what the guns are actually doing.
    /// </summary>
    public List<string> ActiveGroups;

    public int? FireControlLimit;

    public int UncontrolledCannons;

    public TargetingConsoleBoundUserInterfaceState(
        NavInterfaceState navState,
        IFFInterfaceState iffState,
        List<string>? groups,
        List<NetEntity>? controlled,
        List<string> activeGroups,
        int? fireControlLimit = null,
        int uncontrolledCannons = 0)
    {
        NavState = navState;
        IFFState = iffState;
        CannonGroups = groups;
        ControlledCannons = controlled;
        ActiveGroups = activeGroups;
        FireControlLimit = fireControlLimit;
        UncontrolledCannons = uncontrolledCannons;
    }
}

[Serializable, NetSerializable]
public sealed class TargetingConsoleFireMessage : BoundUserInterfaceMessage
{
    public Vector2 Coordinates;

    public TargetingConsoleFireMessage(Vector2 coords)
    {
        Coordinates = coords;
    }
}

/// <summary>
///     Crescent - the trigger came up, drop the standing fire order now.
/// </summary>
/// <remarks>
///     Purely an optimisation over the order's own expiry, never the thing that is relied on. The server times a
///     fire order out on its own precisely because this message can go missing - the client can be disposed,
///     lose focus or drop the packet mid-drag - and the guns must not keep shooting when that happens.
/// </remarks>
[Serializable, NetSerializable]
public sealed class TargetingConsoleStopFireMessage : BoundUserInterfaceMessage
{

}

[Serializable, NetSerializable]
public sealed class FireControlConsoleRefreshServerMessage : BoundUserInterfaceMessage
{

}

[Serializable, NetSerializable]
public sealed class TargetingConsoleGroupChangedMessage : BoundUserInterfaceMessage
{
    public string GroupName;

    public TargetingConsoleGroupChangedMessage(string name)
    {
        GroupName = name;
    }
}

[Serializable, NetSerializable]
public enum TargetingConsoleUiKey : byte
{
    Key,
}
