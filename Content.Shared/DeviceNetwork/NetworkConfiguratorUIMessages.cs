using Content.Shared.DeviceLinking;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.DeviceNetwork;

[Serializable, NetSerializable]
public enum NetworkConfiguratorUiKey
{
    List,
    Configure,
    Link
}

[Serializable, NetSerializable]
public enum NetworkConfiguratorButtonKey
{
    Set,
    Add,
    Edit,
    Clear,
    Copy,
    Show
}

/// <summary>
/// Message sent when the remove button for one device on the list was pressed
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorRemoveDeviceMessage : BoundUserInterfaceMessage
{
    public readonly string Address;

    public NetworkConfiguratorRemoveDeviceMessage(string address)
    {
        Address = address;
    }
}

/// <summary>
/// Message sent when the clear button was pressed
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorClearDevicesMessage : BoundUserInterfaceMessage
{
}

[Serializable, NetSerializable]
public sealed class NetworkConfiguratorButtonPressedMessage : BoundUserInterfaceMessage
{
    public readonly NetworkConfiguratorButtonKey ButtonKey;

    public NetworkConfiguratorButtonPressedMessage(NetworkConfiguratorButtonKey buttonKey)
    {
        ButtonKey = buttonKey;
    }
}

/// <summary>
/// Toggles the link between two ports for every selected source and sink pair.
/// If every pair is already linked the link gets removed from all of them, otherwise it gets added to the missing ones.
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorLinkPortsMessage : BoundUserInterfaceMessage
{
    public readonly List<NetEntity> Sources;
    public readonly List<NetEntity> Sinks;
    public readonly ProtoId<SourcePortPrototype> SourcePort;
    public readonly ProtoId<SinkPortPrototype> SinkPort;

    public NetworkConfiguratorLinkPortsMessage(List<NetEntity> sources, List<NetEntity> sinks,
        ProtoId<SourcePortPrototype> sourcePort, ProtoId<SinkPortPrototype> sinkPort)
    {
        Sources = sources;
        Sinks = sinks;
        SourcePort = sourcePort;
        SinkPort = sinkPort;
    }
}

/// <summary>
/// Adds the default links for every selected source and sink pair
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorLinkDefaultsMessage : BoundUserInterfaceMessage
{
    public readonly List<NetEntity> Sources;
    public readonly List<NetEntity> Sinks;

    public NetworkConfiguratorLinkDefaultsMessage(List<NetEntity> sources, List<NetEntity> sinks)
    {
        Sources = sources;
        Sinks = sinks;
    }
}

/// <summary>
/// Removes every link between the selected source and sink pairs
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorClearLinksMessage : BoundUserInterfaceMessage
{
    public readonly List<NetEntity> Sources;
    public readonly List<NetEntity> Sinks;

    public NetworkConfiguratorClearLinksMessage(List<NetEntity> sources, List<NetEntity> sinks)
    {
        Sources = sources;
        Sinks = sinks;
    }
}

/// <summary>
/// Removes a single existing link
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorRemoveLinkMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity Source;
    public readonly NetEntity Sink;
    public readonly ProtoId<SourcePortPrototype> SourcePort;
    public readonly ProtoId<SinkPortPrototype> SinkPort;

    public NetworkConfiguratorRemoveLinkMessage(NetEntity source, NetEntity sink,
        ProtoId<SourcePortPrototype> sourcePort, ProtoId<SinkPortPrototype> sinkPort)
    {
        Source = source;
        Sink = sink;
        SourcePort = sourcePort;
        SinkPort = sinkPort;
    }
}

/// <summary>
/// Removes a device from the link buffer
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorRemoveBufferedDeviceMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity Device;

    public NetworkConfiguratorRemoveBufferedDeviceMessage(NetEntity device)
    {
        Device = device;
    }
}

/// <summary>
/// Removes every device from the link buffer
/// </summary>
[Serializable, NetSerializable]
public sealed class NetworkConfiguratorClearLinkBufferMessage : BoundUserInterfaceMessage
{
}
