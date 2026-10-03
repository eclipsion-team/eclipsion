using Content.Shared.DeviceLinking;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.DeviceNetwork;

[Serializable, NetSerializable]
public sealed class NetworkConfiguratorUserInterfaceState : BoundUserInterfaceState
{
    public readonly HashSet<(string address, string name)> DeviceList;

    public NetworkConfiguratorUserInterfaceState(HashSet<(string, string)> deviceList)
    {
        DeviceList = deviceList;
    }
}

[Serializable, NetSerializable]
public sealed class DeviceListUserInterfaceState : BoundUserInterfaceState
{
    public readonly HashSet<(string address, string name)> DeviceList;

    public DeviceListUserInterfaceState(HashSet<(string address, string name)> deviceList)
    {
        DeviceList = deviceList;
    }
}

/// <summary>
/// A device stored in the configurators link buffer.
/// A device can be a source, a sink or both.
/// </summary>
[Serializable, NetSerializable]
public sealed class DeviceLinkBufferEntry
{
    public readonly NetEntity Entity;
    public readonly string Name;
    public readonly string Address;

    /// <summary>
    /// The source ports of this device or null if it isn't a source
    /// </summary>
    public readonly List<ProtoId<SourcePortPrototype>>? SourcePorts;

    /// <summary>
    /// The sink ports of this device or null if it isn't a sink
    /// </summary>
    public readonly List<ProtoId<SinkPortPrototype>>? SinkPorts;

    public DeviceLinkBufferEntry(NetEntity entity, string name, string address,
        List<ProtoId<SourcePortPrototype>>? sourcePorts, List<ProtoId<SinkPortPrototype>>? sinkPorts)
    {
        Entity = entity;
        Name = name;
        Address = address;
        SourcePorts = sourcePorts;
        SinkPorts = sinkPorts;
    }
}

/// <summary>
/// An existing link between a source port and a sink port
/// </summary>
[Serializable, NetSerializable]
public sealed class DeviceLinkEntry
{
    public readonly NetEntity Source;
    public readonly string SourceName;
    public readonly ProtoId<SourcePortPrototype> SourcePort;
    public readonly NetEntity Sink;
    public readonly string SinkName;
    public readonly ProtoId<SinkPortPrototype> SinkPort;

    public DeviceLinkEntry(NetEntity source, string sourceName, ProtoId<SourcePortPrototype> sourcePort,
        NetEntity sink, string sinkName, ProtoId<SinkPortPrototype> sinkPort)
    {
        Source = source;
        SourceName = sourceName;
        SourcePort = sourcePort;
        Sink = sink;
        SinkName = sinkName;
        SinkPort = sinkPort;
    }
}

[Serializable, NetSerializable]
public sealed class DeviceLinkUserInterfaceState : BoundUserInterfaceState
{
    /// <summary>
    /// Every device in the link buffer
    /// </summary>
    public readonly List<DeviceLinkBufferEntry> Devices;

    /// <summary>
    /// Every link from or to a device in the link buffer, including links to devices outside of it
    /// </summary>
    public readonly List<DeviceLinkEntry> Links;

    public DeviceLinkUserInterfaceState(List<DeviceLinkBufferEntry> devices, List<DeviceLinkEntry> links)
    {
        Devices = devices;
        Links = links;
    }
}
