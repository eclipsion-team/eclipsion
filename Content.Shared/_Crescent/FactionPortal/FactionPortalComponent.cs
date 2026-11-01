using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.FactionPortal;

/// <summary>
/// A portal on its faction's network. Members pick another open portal of the same faction on the same map,
/// from the portal's menu or by stepping in, and are sent there. The owner comes from
/// <see cref="Content.Shared._Crescent.Factions.FactionMachineComponent"/>.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class FactionPortalComponent : Component
{
    /// <summary>
    /// Shown after the grid name in destination lists. Set it when a grid carries more than one portal.
    /// </summary>
    [DataField]
    public string? Label;

    /// <summary>
    /// Territory portals stay shut while their territory is below this level. Zero skips the territory check.
    /// </summary>
    [DataField]
    public int MinTerritoryLevel;

    [DataField]
    public SoundSpecifier DepartureSound = new SoundPathSpecifier("/Audio/Effects/teleport_departure.ogg");

    [DataField]
    public SoundSpecifier ArrivalSound = new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg");

    [ViewVariables]
    public bool Active;

    [ViewVariables]
    public List<FactionPortalDestination>? LastSent;

    [ViewVariables]
    public bool LastSentOpen;
}

[Serializable, NetSerializable]
public enum FactionPortalUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum FactionPortalVisuals : byte
{
    Active,
}

[Serializable, NetSerializable]
public enum FactionPortalVisualLayers : byte
{
    Portal,
}

[Serializable, NetSerializable]
public readonly record struct FactionPortalDestination(NetEntity Portal, string Name);

[Serializable, NetSerializable]
public sealed class FactionPortalBuiState(bool open, List<FactionPortalDestination> destinations) : BoundUserInterfaceState
{
    public readonly bool Open = open;
    public readonly List<FactionPortalDestination> Destinations = destinations;
}

[Serializable, NetSerializable]
public sealed class FactionPortalSelectMessage(NetEntity destination) : BoundUserInterfaceMessage
{
    public readonly NetEntity Destination = destination;
}
