using System.Linq;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.DeviceNetwork;
using Content.Shared.Popups;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.DeviceLinking;

public abstract class SharedDeviceLinkSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public const string InvokedPort = "link_port";

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<DeviceLinkSourceComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<DeviceLinkSourceComponent, ComponentStartup>(OnSourceStartup);
        SubscribeLocalEvent<DeviceLinkSinkComponent, ComponentStartup>(OnSinkStartup);
        SubscribeLocalEvent<DeviceLinkSourceComponent, ComponentRemove>(OnSourceRemoved);
        SubscribeLocalEvent<DeviceLinkSinkComponent, ComponentRemove>(OnSinkRemoved);
        SubscribeLocalEvent<BeforeSerializationEvent>(OnBeforeSave);
    }

    #region Link Validation

    private void OnInit(EntityUid uid, DeviceLinkSourceComponent component, ComponentInit args)
    {
        // Populate the output dictionary.
        foreach (var (sinkUid, links) in component.LinkedPorts)
        {
            foreach (var link in links)
            {
                component.Outputs.GetOrNew(link.source).Add(sinkUid);
            }
        }
    }

    /// <summary>
    /// Removes invalid links where the saved sink doesn't exist/have a sink component for example
    /// </summary>
    private void OnSourceStartup(EntityUid sourceUid, DeviceLinkSourceComponent sourceComponent, ComponentStartup args)
    {
        List<EntityUid> invalidSinks = new();
        foreach (var sinkUid  in sourceComponent.LinkedPorts.Keys)
        {
            if (!TryComp<DeviceLinkSinkComponent>(sinkUid, out var sinkComponent))
            {
                invalidSinks.Add(sinkUid);
                foreach (var savedSinks in sourceComponent.Outputs.Values)
                {
                    savedSinks.Remove(sinkUid);
                }

                continue;
            }

            sinkComponent.LinkedSources.Add(sourceUid);
        }

        foreach (var invalidSink in invalidSinks)
        {
            sourceComponent.LinkedPorts.Remove(invalidSink);
        }
    }

    /// <summary>
    /// Same with <see cref="OnSourceStartup"/> but also checks that the saved ports are present on the sink
    /// </summary>
    private void OnSinkStartup(EntityUid sinkUid, DeviceLinkSinkComponent sinkComponent, ComponentStartup args)
    {
        List<EntityUid> invalidSources = new();
        foreach (var sourceUid in sinkComponent.LinkedSources)
        {
            if (!TryComp<DeviceLinkSourceComponent>(sourceUid, out var sourceComponent))
            {
                invalidSources.Add(sourceUid);
                continue;
            }

            if (!sourceComponent.LinkedPorts.TryGetValue(sinkUid, out var linkedPorts))
            {
                foreach (var savedSinks in sourceComponent.Outputs.Values)
                {
                    savedSinks.Remove(sinkUid);
                }
                continue;
            }

            if (sinkComponent.Ports == null)
                continue;

            List<(string, string)> invalidLinks = new();
            foreach (var link in linkedPorts)
            {
                if (!sinkComponent.Ports.Contains(link.sink))
                    invalidLinks.Add(link);
            }

            foreach (var invalidLink in invalidLinks)
            {
                linkedPorts.Remove(invalidLink);
                sourceComponent.Outputs.GetValueOrDefault(invalidLink.Item1)?.Remove(sinkUid);
            }
        }

        foreach (var invalidSource in invalidSources)
        {
            sinkComponent.LinkedSources.Remove(invalidSource);
        }
    }
    #endregion

    /// <summary>
    /// Ensures that its links get deleted when a source gets removed
    /// </summary>
    private void OnSourceRemoved(EntityUid uid, DeviceLinkSourceComponent component, ComponentRemove args)
    {
        var query = GetEntityQuery<DeviceLinkSinkComponent>();
        // Eclipsion: iterate a copy, the removal below mutates LinkedPorts. Sinks that are already gone still need
        // their entry dropped, otherwise the source keeps a dangling EntityUid that breaks map saving.
        foreach (var sinkUid in component.LinkedPorts.Keys.ToArray())
        {
            if (query.TryGetComponent(sinkUid, out var sink))
                RemoveSinkFromSourceInternal(uid, sinkUid, component, sink);
            else
                ClearDanglingLink(component, sinkUid);
        }
    }

    /// <summary>
    /// Ensures that its links get deleted when a sink gets removed
    /// </summary>
    private void OnSinkRemoved(EntityUid sinkUid, DeviceLinkSinkComponent sinkComponent, ComponentRemove args)
    {
        var query = GetEntityQuery<DeviceLinkSourceComponent>();
        // Eclipsion: this used to query sinkUid instead of linkedSource, so deleting a sink (shutter, conveyor, door)
        // never unlinked it from its source (button, lever). The source kept a reference to the deleted entity and
        // the map could no longer be saved. Iterate a copy, the removal below mutates LinkedSources.
        foreach (var linkedSource in sinkComponent.LinkedSources.ToArray())
        {
            if (query.TryGetComponent(linkedSource, out var source))
                RemoveSinkFromSourceInternal(linkedSource, sinkUid, source, sinkComponent);
            else
                sinkComponent.LinkedSources.Remove(linkedSource);
        }
    }

    /// <summary>
    /// Drops every trace of a link to <paramref name="sinkUid"/> from a source, without touching the sink.
    /// Used when the sink no longer exists and can't be cleaned up the normal way.
    /// </summary>
    private static void ClearDanglingLink(DeviceLinkSourceComponent source, EntityUid sinkUid)
    {
        source.LinkedPorts.Remove(sinkUid);
        foreach (var outputs in source.Outputs.Values)
        {
            outputs.Remove(sinkUid);
        }
    }

    /// <summary>
    /// Last line of defence for map saving: strips links that point at entities which aren't part of the save.
    /// Serializing a reference to a deleted or off-map entity makes the whole save fail, so a mapper deleting one
    /// half of a linked pair (a lever without its conveyor, a button without its shutter) would otherwise be stuck.
    /// </summary>
    private void OnBeforeSave(BeforeSerializationEvent ev)
    {
        var xformQuery = GetEntityQuery<TransformComponent>();
        var toRemove = new List<EntityUid>();

        var sources = AllEntityQuery<DeviceLinkSourceComponent, TransformComponent>();
        while (sources.MoveNext(out var uid, out var source, out var xform))
        {
            if (!ev.MapIds.Contains(xform.MapID))
                continue;

            foreach (var sinkUid in source.LinkedPorts.Keys)
            {
                if (!xformQuery.TryGetComponent(sinkUid, out var sinkXform) || !ev.MapIds.Contains(sinkXform.MapID))
                    toRemove.Add(sinkUid);
            }

            foreach (var sinkUid in toRemove)
            {
                Log.Warning($"Dropping link from {ToPrettyString(uid)} to {sinkUid}, it is not part of the save.");
                ClearDanglingLink(source, sinkUid);

                if (TryComp<DeviceLinkSinkComponent>(sinkUid, out var sink))
                    sink.LinkedSources.Remove(uid);
            }

            toRemove.Clear();
        }

        var sinks = AllEntityQuery<DeviceLinkSinkComponent, TransformComponent>();
        while (sinks.MoveNext(out var uid, out var sink, out var xform))
        {
            if (!ev.MapIds.Contains(xform.MapID))
                continue;

            foreach (var sourceUid in sink.LinkedSources)
            {
                if (!xformQuery.TryGetComponent(sourceUid, out var sourceXform) || !ev.MapIds.Contains(sourceXform.MapID))
                    toRemove.Add(sourceUid);
            }

            foreach (var sourceUid in toRemove)
            {
                Log.Warning($"Dropping link from {sourceUid} to {ToPrettyString(uid)}, it is not part of the save.");
                sink.LinkedSources.Remove(sourceUid);

                if (TryComp<DeviceLinkSourceComponent>(sourceUid, out var source))
                    ClearDanglingLink(source, uid);
            }

            toRemove.Clear();
        }
    }

    #region Ports
    /// <summary>
    /// Convenience function to add several ports to an entity
    /// </summary>
    public void EnsureSourcePorts(EntityUid uid, params string[] ports)
    {
        if (ports.Length == 0)
            return;

        var comp = EnsureComp<DeviceLinkSourceComponent>(uid);
        comp.Ports ??= new HashSet<ProtoId<SourcePortPrototype>>();

        foreach (var port in ports)
        {
            DebugTools.Assert(_prototypeManager.HasIndex<SourcePortPrototype>(port));
            comp.Ports?.Add(port);
        }
    }

    /// <summary>
    /// Convenience function to add several ports to an entity.
    /// </summary>
    public void EnsureSinkPorts(EntityUid uid, params string[] ports)
    {
        if (ports.Length == 0)
            return;

        var comp = EnsureComp<DeviceLinkSinkComponent>(uid);
        comp.Ports ??= new HashSet<ProtoId<SinkPortPrototype>>();

        foreach (var port in ports)
        {
            DebugTools.Assert(_prototypeManager.HasIndex<SinkPortPrototype>(port));
            comp.Ports?.Add(port);
        }
    }

    /// <summary>
    /// Retrieves the available ports from a source
    /// </summary>
    /// <returns>A list of source port prototypes</returns>
    public List<SourcePortPrototype> GetSourcePorts(EntityUid sourceUid, DeviceLinkSourceComponent? sourceComponent = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent) || sourceComponent.Ports == null)
            return new List<SourcePortPrototype>();

        var sourcePorts = new List<SourcePortPrototype>();
        foreach (var port in sourceComponent.Ports)
        {
            sourcePorts.Add(_prototypeManager.Index<SourcePortPrototype>(port));
        }

        return sourcePorts;
    }

    /// <summary>
    /// Retrieves the available ports from a sink
    /// </summary>
    /// <returns>A list of sink port prototypes</returns>
    public List<SinkPortPrototype> GetSinkPorts(EntityUid sinkUid, DeviceLinkSinkComponent? sinkComponent = null)
    {
        if (!Resolve(sinkUid, ref sinkComponent) || sinkComponent.Ports == null)
            return new List<SinkPortPrototype>();

        var sinkPorts = new List<SinkPortPrototype>();
        foreach (var port in sinkComponent.Ports)
        {
            sinkPorts.Add(_prototypeManager.Index<SinkPortPrototype>(port));
        }

        return sinkPorts;
    }

    /// <summary>
    /// Convenience function to retrieve the name of a port prototype
    /// </summary>
    public string PortName<TPort>(string port) where TPort : DevicePortPrototype, IPrototype
    {
        if (!_prototypeManager.TryIndex<TPort>(port, out var proto))
            return port;

        return Loc.GetString(proto.Name);
    }
    #endregion

    #region Links
    /// <summary>
    /// Returns the links of a source
    /// </summary>
    /// <returns>A list of sink and source port ids that are linked together</returns>
    public HashSet<(ProtoId<SourcePortPrototype> source, ProtoId<SinkPortPrototype> sink)> GetLinks(EntityUid sourceUid, EntityUid sinkUid, DeviceLinkSourceComponent? sourceComponent = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent) || !sourceComponent.LinkedPorts.TryGetValue(sinkUid, out var links))
            return new HashSet<(ProtoId<SourcePortPrototype>, ProtoId<SinkPortPrototype>)>();

        return links;
    }

    /// <summary>
    /// Returns true if the given source port is linked to the given sink port of the sink
    /// </summary>
    public bool IsLinked(DeviceLinkSourceComponent source, EntityUid sinkUid,
        ProtoId<SourcePortPrototype> sourcePort, ProtoId<SinkPortPrototype> sinkPort)
    {
        return source.LinkedPorts.TryGetValue(sinkUid, out var ports) && ports.Contains((sourcePort, sinkPort));
    }

    public bool HasPort(DeviceLinkSourceComponent source, ProtoId<SourcePortPrototype> port)
    {
        return source.Ports?.Contains(port) == true;
    }

    public bool HasPort(DeviceLinkSinkComponent sink, ProtoId<SinkPortPrototype> port)
    {
        return sink.Ports?.Contains(port) == true;
    }

    /// <summary>
    /// Returns the default links for the given list of source port prototypes
    /// </summary>
    /// <param name="sources">The list of source port prototypes to get the default links for</param>
    /// <returns>A list of sink and source port ids</returns>
    public List<(string source, string sink)> GetDefaults(List<SourcePortPrototype> sources)
    {
        var defaults = new List<(string, string)>();
        foreach (var source in sources)
        {
            if (source.DefaultLinks == null)
                return new List<(string, string)>();

            foreach (var defaultLink in source.DefaultLinks)
            {
                defaults.Add((source.ID, defaultLink));
            }
        }

        return defaults;
    }

    /// <summary>
    /// Links the given source and sink by their default links
    /// </summary>
    /// <param name="userId">Optinal user uid for displaying popups</param>
    /// <param name="sourceUid">The source uid</param>
    /// <param name="sinkUid">The sink uid</param>
    /// <param name="sourceComponent"></param>
    /// <param name="sinkComponent"></param>
    public void LinkDefaults(
        EntityUid? userId,
        EntityUid sourceUid,
        EntityUid sinkUid,
        DeviceLinkSourceComponent? sourceComponent = null,
        DeviceLinkSinkComponent? sinkComponent = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent) || !Resolve(sinkUid, ref sinkComponent))
            return;

        if (userId != null)
            _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"{ToPrettyString(userId.Value):actor} is linking defaults between {ToPrettyString(sourceUid):source} and {ToPrettyString(sinkUid):sink}");
        else
            _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"linking defaults between {ToPrettyString(sourceUid):source} and {ToPrettyString(sinkUid):sink}");

        var sourcePorts = GetSourcePorts(sourceUid, sourceComponent);
        var defaults = GetDefaults(sourcePorts);
        SaveLinks(userId, sourceUid, sinkUid, defaults, sourceComponent, sinkComponent);

        if (userId != null)
            _popupSystem.PopupCursor(Loc.GetString("signal-linking-verb-success", ("machine", sourceUid)), userId.Value);
    }


    /// <summary>
    /// Saves multiple links between a source and a sink device.
    /// Ignores links where either the source or sink port aren't present
    /// </summary>
    /// <param name="userId">Optinal user uid for displaying popups</param>
    /// <param name="sourceUid">The source uid</param>
    /// <param name="sinkUid">The sink uid</param>
    /// <param name="links">List of source and sink ids to link</param>
    /// <param name="sourceComponent"></param>
    /// <param name="sinkComponent"></param>
    public void SaveLinks(
        EntityUid? userId,
        EntityUid sourceUid,
        EntityUid sinkUid,
        List<(string source, string sink)> links,
        DeviceLinkSourceComponent? sourceComponent = null,
        DeviceLinkSinkComponent? sinkComponent = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent) || !Resolve(sinkUid, ref sinkComponent))
            return;

        if (sourceComponent.Ports == null || sinkComponent.Ports == null)
            return;

        // if (!InRange(sourceUid, sinkUid, sourceComponent.Range)) //hullrot edit: infinite link distance
        // {
        //     if (userId != null)
        //         _popupSystem.PopupCursor(Loc.GetString("signal-linker-component-out-of-range"), userId.Value);

        //     return;
        // }

        RemoveSinkFromSource(sourceUid, sinkUid, sourceComponent);
        foreach (var (source, sink) in links)
        {
            DebugTools.Assert(_prototypeManager.HasIndex<SourcePortPrototype>(source));
            DebugTools.Assert(_prototypeManager.HasIndex<SinkPortPrototype>(sink));

            if (!sourceComponent.Ports.Contains(source) || !sinkComponent.Ports.Contains(sink))
                continue;

            if (!CanLink(userId, sourceUid, sinkUid, source, sink, false, sourceComponent))
                continue;

            sourceComponent.Outputs.GetOrNew(source).Add(sinkUid);
            sourceComponent.LinkedPorts.GetOrNew(sinkUid).Add((source, sink));

            SendNewLinkEvent(userId, sourceUid, source, sinkUid, sink);
        }

        if (links.Count > 0)
            sinkComponent.LinkedSources.Add(sourceUid);
    }

    /// <summary>
    /// Removes every link from the given sink
    /// </summary>
    public void RemoveAllFromSink(EntityUid sinkUid, DeviceLinkSinkComponent? sinkComponent = null)
    {
        if (!Resolve(sinkUid, ref sinkComponent))
            return;

        foreach (var sourceUid in sinkComponent.LinkedSources)
        {
            RemoveSinkFromSource(sourceUid, sinkUid, null, sinkComponent);
        }
    }

    /// <summary>
    /// Removes every link from the given sink
    /// </summary>
    public void RemoveAllFromSource(EntityUid sourceUid, DeviceLinkSourceComponent? sourceComponent = null, Predicate<EntityUid>? filter = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent))
            return;

        foreach (var sinkUid in sourceComponent.LinkedPorts.Where(sinkUid => filter == null || filter.Invoke(sinkUid.Key)))
        {
            RemoveSinkFromSource(sourceUid, sinkUid.Key, sourceComponent);
        }
    }

    /// <summary>
    /// Removes all links between a source and a sink
    /// </summary>
    public void RemoveSinkFromSource(
        EntityUid sourceUid,
        EntityUid sinkUid,
        DeviceLinkSourceComponent? sourceComponent = null,
        DeviceLinkSinkComponent? sinkComponent = null)
    {
        if (Resolve(sourceUid, ref sourceComponent, false) && Resolve(sinkUid, ref sinkComponent, false))
        {
            RemoveSinkFromSourceInternal(sourceUid, sinkUid, sourceComponent, sinkComponent);
            return;
        }

        if (sourceComponent == null && sinkComponent == null)
        {
            // Both were deleted?
            return;
        }

        if (sourceComponent == null)
        {
            Log.Error($"Attempted to remove link between {ToPrettyString(sourceUid)} and {ToPrettyString(sinkUid)}, but the source component was missing.");
            sinkComponent!.LinkedSources.Remove(sourceUid);
        }
        else
        {
            Log.Error($"Attempted to remove link between {ToPrettyString(sourceUid)} and {ToPrettyString(sinkUid)}, but the sink component was missing.");
            sourceComponent.LinkedPorts.Remove(sinkUid);
        }
    }

    private void RemoveSinkFromSourceInternal(
        EntityUid sourceUid,
        EntityUid sinkUid,
        DeviceLinkSourceComponent sourceComponent,
        DeviceLinkSinkComponent sinkComponent)
    {
        // This function gets called on component removal. Beware that TryComp & Resolve may return false.

        if (sourceComponent.LinkedPorts.TryGetValue(sinkUid, out var ports))
        {
            foreach (var (sourcePort, sinkPort) in ports)
            {
                RaiseLocalEvent(sourceUid, new PortDisconnectedEvent(sourcePort, sinkUid));
                RaiseLocalEvent(sinkUid, new PortDisconnectedEvent(sinkPort, sourceUid));
            }
        }

        sinkComponent.LinkedSources.Remove(sourceUid);
        sourceComponent.LinkedPorts.Remove(sinkUid);
        var outputLists = sourceComponent.Outputs.Values;
        foreach (var outputList in outputLists)
        {
            outputList.Remove(sinkUid);
        }

    }

    /// <summary>
    /// Adds or removes a link depending on if it's already present
    /// </summary>
    /// <returns>True if the link was successfully added or removed</returns>
    public bool ToggleLink(
        EntityUid? userId,
        EntityUid sourceUid,
        EntityUid sinkUid,
        string source,
        string sink,
        DeviceLinkSourceComponent? sourceComponent = null,
        DeviceLinkSinkComponent? sinkComponent = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent) || !Resolve(sinkUid, ref sinkComponent))
            return false;

        if (sourceComponent.Ports == null || sinkComponent.Ports == null)
            return false;

        var outputs = sourceComponent.Outputs.GetOrNew(source);
        var linkedPorts = sourceComponent.LinkedPorts.GetOrNew(sinkUid);

        if (linkedPorts.Contains((source, sink)))
        {
            if (userId != null)
                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"{ToPrettyString(userId.Value):actor} unlinked {ToPrettyString(sourceUid):source} {source} and {ToPrettyString(sinkUid):sink} {sink}");
            else
                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"unlinked {ToPrettyString(sourceUid):source} {source} and {ToPrettyString(sinkUid):sink} {sink}");

            RaiseLocalEvent(sourceUid, new PortDisconnectedEvent(source, sinkUid));
            RaiseLocalEvent(sinkUid, new PortDisconnectedEvent(sink, sourceUid));

            outputs.Remove(sinkUid);
            linkedPorts.Remove((source, sink));

            if (linkedPorts.Count != 0)
                return true;

            sourceComponent.LinkedPorts.Remove(sinkUid);
            sinkComponent.LinkedSources.Remove(sourceUid);
            CreateLinkPopup(userId, sourceUid, source, sinkUid, sink, true);
        }
        else
        {
            if (!sourceComponent.Ports.Contains(source) || !sinkComponent.Ports.Contains(sink))
                return false;

            if (!CanLink(userId, sourceUid, sinkUid, source, sink, true, sourceComponent))
                return false;

            outputs.Add(sinkUid);
            linkedPorts.Add((source, sink));
            sinkComponent.LinkedSources.Add(sourceUid);

            SendNewLinkEvent(userId, sourceUid, source, sinkUid, sink);
            CreateLinkPopup(userId, sourceUid, source, sinkUid, sink, false);
        }

        return true;
    }

    /// <summary>
    /// Checks if a source and a sink can be linked by allowing other systems to veto the link
    /// and by optionally checking if they are in range of each other
    /// </summary>
    /// <returns></returns>
    private bool CanLink(
        EntityUid? userId,
        EntityUid sourceUid,
        EntityUid sinkUid,
        string source,
        string sink,
        bool checkRange = true,
        DeviceLinkSourceComponent? sourceComponent = null)
    {
        if (!Resolve(sourceUid, ref sourceComponent))
            return false;

        // if (checkRange && !InRange(sourceUid, sinkUid, sourceComponent.Range)) //hullrot edit: infinite link distance
        // {
        //     if (userId.HasValue)
        //         _popupSystem.PopupCursor(Loc.GetString("signal-linker-component-out-of-range"), userId.Value);

        //     return false;
        // }

        var linkAttemptEvent = new LinkAttemptEvent(userId, sourceUid, source, sinkUid, sink);

        RaiseLocalEvent(sourceUid, linkAttemptEvent, true);
        if (linkAttemptEvent.Cancelled && userId.HasValue)
        {
            _popupSystem.PopupCursor(Loc.GetString("signal-linker-component-connection-refused", ("machine", source)), userId.Value);
            return false;
        }

        RaiseLocalEvent(sinkUid, linkAttemptEvent, true);
        if (linkAttemptEvent.Cancelled && userId.HasValue)
        {
            _popupSystem.PopupCursor(Loc.GetString("signal-linker-component-connection-refused", ("machine", source)), userId.Value);
            return false;
        }

        return !linkAttemptEvent.Cancelled;
    }

    private bool InRange(EntityUid sourceUid, EntityUid sinkUid, float range)
    {
        // TODO: This should be using an existing method and also coordinates inrange instead.
        return _transform.GetMapCoordinates(sourceUid).InRange(_transform.GetMapCoordinates(sinkUid), range);
    }

    private void SendNewLinkEvent(EntityUid? user, EntityUid sourceUid, string source, EntityUid sinkUid, string sink)
    {
        if (user != null)
            _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"{ToPrettyString(user.Value):actor} linked {ToPrettyString(sourceUid):source} {source} and {ToPrettyString(sinkUid):sink} {sink}");
        else
            _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"linked {ToPrettyString(sourceUid):source} {source} and {ToPrettyString(sinkUid):sink} {sink}");

        var newLinkEvent = new NewLinkEvent(user, sourceUid, source, sinkUid, sink);
        RaiseLocalEvent(sourceUid, newLinkEvent);
        RaiseLocalEvent(sinkUid, newLinkEvent);
    }

    private void CreateLinkPopup(EntityUid? userId, EntityUid sourceUid, string source, EntityUid sinkUid, string sink, bool removed)
    {
        if (!userId.HasValue)
            return;

        var locString = removed ? "signal-linker-component-unlinked-port" : "signal-linker-component-linked-port";

        _popupSystem.PopupCursor(Loc.GetString(locString, ("machine1", sourceUid), ("port1", PortName<SourcePortPrototype>(source)),
                ("machine2", sinkUid), ("port2", PortName<SinkPortPrototype>(sink))), userId.Value, PopupType.Medium);
    }
    #endregion

    #region Sending & Receiving
    /// <summary>
    /// Sends a network payload directed at the sink entity.
    /// Just raises a <see cref="SignalReceivedEvent"/> without data if the source or the sink doesn't have a <see cref="DeviceNetworkComponent"/>
    /// </summary>
    /// <param name="uid">The source uid that invokes the port</param>
    /// <param name="port">The port to invoke</param>
    /// <param name="data">Optional data to send along</param>
    /// <param name="sourceComponent"></param>
    public virtual void InvokePort(EntityUid uid, string port, NetworkPayload? data = null,
        DeviceLinkSourceComponent? sourceComponent = null)
    {
        // NOOP on client for the moment.
    }
    #endregion
}
