using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.DeviceLinking.Systems;
using Content.Server.DeviceNetwork.Components;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Database;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork.Systems;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using JetBrains.Annotations;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.DeviceNetwork.Systems;

[UsedImplicitly]
public sealed class NetworkConfiguratorSystem : SharedNetworkConfiguratorSystem
{
    [Dependency] private readonly DeviceListSystem _deviceListSystem = default!;
    [Dependency] private readonly DeviceLinkSystem _deviceLinkSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly AccessReaderSystem _accessSystem = default!;
    [Dependency] private readonly SharedInteractionSystem _interactionSystem = default!;
    [Dependency] private readonly AudioSystem _audioSystem = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NetworkConfiguratorComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NetworkConfiguratorComponent, ComponentShutdown>(OnShutdown);

        //Interaction
        SubscribeLocalEvent<NetworkConfiguratorComponent, AfterInteractEvent>(AfterInteract); //TODO: Replace with utility verb?
        SubscribeLocalEvent<NetworkConfiguratorComponent, ExaminedEvent>(DoExamine);

        //Verbs
        SubscribeLocalEvent<NetworkConfiguratorComponent, GetVerbsEvent<UtilityVerb>>(OnAddInteractVerb);
        SubscribeLocalEvent<DeviceNetworkComponent, GetVerbsEvent<AlternativeVerb>>(OnAddAlternativeSaveDeviceVerb);
        SubscribeLocalEvent<NetworkConfiguratorComponent, GetVerbsEvent<AlternativeVerb>>(OnAddSwitchModeVerb);
        SubscribeLocalEvent<NetworkConfiguratorComponent, GetVerbsEvent<Verb>>(OnAddLinkBufferVerbs);

        //UI
        SubscribeLocalEvent<NetworkConfiguratorComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorRemoveDeviceMessage>(OnRemoveDevice);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorClearDevicesMessage>(OnClearDevice);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorLinkPortsMessage>(OnLinkPorts);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorLinkDefaultsMessage>(OnLinkDefaults);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorClearLinksMessage>(OnClearLinks);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorRemoveLinkMessage>(OnRemoveLink);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorRemoveBufferedDeviceMessage>(OnRemoveBufferedDevice);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorClearLinkBufferMessage>(OnClearLinkBuffer);
        SubscribeLocalEvent<NetworkConfiguratorComponent, NetworkConfiguratorButtonPressedMessage>(OnConfigButtonPressed);

        SubscribeLocalEvent<DeviceListComponent, ComponentRemove>(OnComponentRemoved);

        SubscribeLocalEvent<BeforeSerializationEvent>(OnMapSave);
    }

    private void OnMapSave(BeforeSerializationEvent ev)
    {
        var enumerator = AllEntityQuery<NetworkConfiguratorComponent>();
        while (enumerator.MoveNext(out var uid, out var conf))
        {
            if (!TryComp(conf.ActiveDeviceList, out TransformComponent? listXform))
                continue;

            if (!ev.MapIds.Contains(listXform.MapID))
                continue;

            // The linked device list is (probably) being saved. Make sure that the configurator is also being saved
            // (i.e., not in the hands of a mapper/ghost). In the future, map saving should raise a separate event
            // containing a set of all entities that are about to be saved, which would make checking this much easier.
            // This is a shitty bandaid, and will force close the UI during auto-saves.
            // TODO Map serialization refactor
            // I'm refactoring it now and I still dont know what to do

            var xform = Transform(uid);
            if (ev.MapIds.Contains(xform.MapID) && IsSaveable(uid))
                continue;

            _uiSystem.CloseUi(uid, NetworkConfiguratorUiKey.Configure);
            DebugTools.AssertNull(conf.ActiveDeviceList);
        }

        bool IsSaveable(EntityUid uid)
        {
            while (uid.IsValid())
            {
                if (Prototype(uid)?.MapSavable == false)
                    return false;
                uid = Transform(uid).ParentUid;
            }
            return true;
        }
    }

    private void OnShutdown(EntityUid uid, NetworkConfiguratorComponent component, ComponentShutdown args)
    {
        ClearDevices(uid, component);

        if (TryComp(component.ActiveDeviceList, out DeviceListComponent? list))
            list.Configurators.Remove(uid);
        component.ActiveDeviceList = null;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NetworkConfiguratorComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (component.ActiveDeviceList != null
            && EntityManager.EntityExists(component.ActiveDeviceList.Value)
            && _interactionSystem.InRangeUnobstructed(uid, component.ActiveDeviceList.Value))
                continue;

            //The network configurator is a handheld device. There can only ever be an ui session open for the player holding the device.
            _uiSystem.CloseUi(uid, NetworkConfiguratorUiKey.Configure);
        }
    }

    private void OnMapInit(EntityUid uid, NetworkConfiguratorComponent component, MapInitEvent args)
    {
        UpdateListUiState(uid, component);
    }

    private void TryAddNetworkDevice(EntityUid? targetUid, EntityUid configuratorUid, EntityUid userUid,
        NetworkConfiguratorComponent? configurator = null)
    {
        if (!Resolve(configuratorUid, ref configurator))
            return;

        TryAddNetworkDevice(configuratorUid, targetUid, userUid, configurator);
    }

    private void TryAddNetworkDevice(EntityUid configuratorUid, EntityUid? targetUid, EntityUid userUid, NetworkConfiguratorComponent configurator, DeviceNetworkComponent? device = null)
    {
        if (!targetUid.HasValue || !Resolve(targetUid.Value, ref device, false))
            return;

        //This checks if the device is marked as having a savable address,
        //to avoid adding pdas and whatnot to air alarms. This flag is true
        //by default, so this will only prevent devices from being added to
        //network configurator lists if manually set to false in the prototype
        if (!device.SavableAddress)
            return;

        var address = device.Address;
        if (string.IsNullOrEmpty(address))
        {
            // This primarily checks if the entity in question is pre-map init or not.
            // This is because otherwise, anything that uses DeviceNetwork will not
            // have an address populated, as all devices that use DeviceNetwork
            // obtain their address on map init. If the entity is post-map init,
            // and it still doesn't have an address, it will fail. Otherwise,
            // it stores the entity's UID as a string for visual effect, that way
            // a mapper can reference the devices they've gathered by UID, instead of
            // by device network address. These entries, if the multitool is still in
            // the map after it being saved, are cleared upon mapinit.
            if (MetaData(targetUid.Value).EntityLifeStage == EntityLifeStage.MapInitialized)
            {
                _popupSystem.PopupCursor(Loc.GetString("network-configurator-device-failed", ("device", targetUid)),
                    userUid);
                return;
            }

            address = $"UID: {targetUid.Value}";
        }

        if (configurator.Devices.ContainsValue(targetUid.Value))
        {
            _popupSystem.PopupCursor(Loc.GetString("network-configurator-device-already-saved", ("device", targetUid)), userUid);
            return;
        }

        device.Configurators.Add(configuratorUid);
        configurator.Devices.Add(address, targetUid.Value);
        _popupSystem.PopupCursor(Loc.GetString("network-configurator-device-saved", ("address", device.Address), ("device", targetUid)),
            userUid, PopupType.Medium);

        _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"{ToPrettyString(userUid):actor} saved {ToPrettyString(targetUid.Value):subject} to {ToPrettyString(configuratorUid):tool}");

        UpdateListUiState(configuratorUid, configurator);
    }

    private bool IsLinkable(EntityUid? uid)
    {
        return HasComp<DeviceLinkSourceComponent>(uid) || HasComp<DeviceLinkSinkComponent>(uid);
    }

    /// <summary>
    /// Adds the target to the link buffer or removes it if it's already buffered
    /// </summary>
    private void ToggleBufferedLinkDevice(EntityUid uid, NetworkConfiguratorComponent configurator, EntityUid? target, EntityUid user)
    {
        if (target == null || !IsLinkable(target))
            return;

        PruneLinkBuffer(uid, configurator);

        if (configurator.LinkBuffer.Remove(target.Value))
        {
            if (configurator.LinkBuffer.Count == 0)
                configurator.LinkMenuAutoOpened = false;

            Dirty(uid, configurator);
            _popupSystem.PopupEntity(Loc.GetString("network-configurator-link-buffer-removed",
                ("device", target.Value), ("count", configurator.LinkBuffer.Count)), target.Value, user);
            UpdateLinkUiState(uid, configurator);
            return;
        }

        if (!AccessCheck(target.Value, user, configurator))
            return;

        if (configurator.LinkBuffer.Count >= configurator.MaxLinkBuffer)
        {
            _popupSystem.PopupEntity(Loc.GetString("network-configurator-link-buffer-full"), target.Value, user);
            return;
        }

        configurator.LinkBuffer.Add(target.Value);
        Dirty(uid, configurator);
        _popupSystem.PopupEntity(Loc.GetString("network-configurator-link-buffer-added",
            ("device", target.Value), ("count", configurator.LinkBuffer.Count)), target.Value, user);

        // Open the menu on its own the first time there's something to link, after that it only gets refreshed
        if (!configurator.LinkMenuAutoOpened
            && configurator.LinkBuffer.Any(HasComp<DeviceLinkSourceComponent>)
            && configurator.LinkBuffer.Any(HasComp<DeviceLinkSinkComponent>))
        {
            configurator.LinkMenuAutoOpened = true;
            OpenDeviceLinkUi(uid, configurator, user);
            return;
        }

        UpdateLinkUiState(uid, configurator);
    }

    /// <summary>
    /// Removes deleted or no longer linkable devices from the link buffer
    /// </summary>
    private void PruneLinkBuffer(EntityUid uid, NetworkConfiguratorComponent configurator)
    {
        if (configurator.LinkBuffer.RemoveAll(ent => TerminatingOrDeleted(ent) || !IsLinkable(ent)) > 0)
            Dirty(uid, configurator);
    }

    /// <summary>
    /// Links the defaults between the target and every buffered device
    /// </summary>
    private void TryLinkDefaults(EntityUid uid, NetworkConfiguratorComponent configurator, EntityUid? targetUid, EntityUid user)
    {
        if (!configurator.LinkModeActive || targetUid == null || !IsLinkable(targetUid))
            return;

        if (!AccessCheck(targetUid.Value, user, configurator))
            return;

        PruneLinkBuffer(uid, configurator);

        var sources = configurator.LinkBuffer.Where(ent => HasAccess(ent, user)).ToList();
        var sinks = new List<EntityUid>(sources);
        var linked = LinkDefaultPairs(user, sources, [targetUid.Value]) + LinkDefaultPairs(user, [targetUid.Value], sinks);

        _popupSystem.PopupCursor(Loc.GetString(linked > 0 ? "network-configurator-links-added" : "network-configurator-links-none",
            ("count", linked)), user, PopupType.Medium);
        UpdateLinkUiState(uid, configurator);
    }

    private bool AccessCheck(EntityUid target, EntityUid? user, NetworkConfiguratorComponent component)
    {
        if (!TryComp(target, out AccessReaderComponent? reader) || user == null)
            return true;

        if (_accessSystem.IsAllowed(user.Value, target, reader))
            return true;

        _audioSystem.PlayPvs(component.SoundNoAccess, user.Value, AudioParams.Default.WithVolume(-2f).WithPitchScale(1.2f));
        _popupSystem.PopupEntity(Loc.GetString("network-configurator-device-access-denied"), target, user.Value);

        return false;
    }

    /// <summary>
    /// Silent access check. Buffered devices were only checked against whoever buffered them,
    /// so anything acting on the buffer checks the current user again.
    /// </summary>
    private bool HasAccess(EntityUid target, EntityUid user)
    {
        return !TryComp(target, out AccessReaderComponent? reader) || _accessSystem.IsAllowed(user, target, reader);
    }

    private void OnComponentRemoved(EntityUid uid, DeviceListComponent component, ComponentRemove args)
    {
        _uiSystem.CloseUi(uid, NetworkConfiguratorUiKey.Configure);
    }

    /// <summary>
    /// Toggles between linking and listing mode
    /// </summary>
    private void SwitchMode(EntityUid? userUid, EntityUid configuratorUid, NetworkConfiguratorComponent configurator)
    {
        if (Delay(configurator))
            return;

        configurator.LinkModeActive = !configurator.LinkModeActive;

        if (!userUid.HasValue)
            return;

        UpdateModeAppearance(userUid.Value, configuratorUid, configurator);
    }

    /// <summary>
    /// Sets the mode to linking or list depending on the link mode parameter
    /// </summary>>
    private void SetMode(EntityUid configuratorUid, NetworkConfiguratorComponent configurator, EntityUid userUid, bool linkMode)
    {
        configurator.LinkModeActive = linkMode;

        UpdateModeAppearance(userUid, configuratorUid, configurator);
    }

    /// <summary>
    /// Updates the configurators appearance and plays a sound indicating that the mode switched
    /// </summary>
    private void UpdateModeAppearance(EntityUid userUid, EntityUid configuratorUid, NetworkConfiguratorComponent configurator)
    {
        Dirty(configuratorUid, configurator);
        _appearanceSystem.SetData(configuratorUid, NetworkConfiguratorVisuals.Mode, configurator.LinkModeActive);

        var pitch = configurator.LinkModeActive ? 1 : 0.8f;
        _audioSystem.PlayPvs(configurator.SoundSwitchMode, userUid, AudioParams.Default.WithVolume(1.5f).WithPitchScale(pitch));
    }

    /// <summary>
    /// Returns true if the last time this method was called is earlier than the configurators use delay.
    /// </summary>
    private bool Delay(NetworkConfiguratorComponent configurator)
    {
        var currentTime = _gameTiming.CurTime;
        if (currentTime < configurator.LastUseAttempt + configurator.UseDelay)
            return true;

        configurator.LastUseAttempt = currentTime;
        return false;
    }

    #region Interactions

    private void DoExamine(EntityUid uid, NetworkConfiguratorComponent component, ExaminedEvent args)
    {
        var mode = component.LinkModeActive ? "network-configurator-examine-mode-link" : "network-configurator-examine-mode-list";
        args.PushMarkup(Loc.GetString("network-configurator-examine-current-mode", ("mode", Loc.GetString(mode))));

        if (component.LinkBuffer.Count > 0)
            args.PushMarkup(Loc.GetString("network-configurator-examine-link-buffer", ("count", component.LinkBuffer.Count)));
    }

    private void AfterInteract(EntityUid uid, NetworkConfiguratorComponent component, AfterInteractEvent args)
    {
        OnUsed(uid, component, args.Target, args.User, args.CanReach);
    }

    /// <summary>
    /// Either adds a device to the device list or shows the config ui if the target is ant entity with a device list
    /// </summary>
    private void OnUsed(EntityUid uid, NetworkConfiguratorComponent configurator, EntityUid? target, EntityUid user, bool canReach = true)
    {
        if (!canReach || !target.HasValue)
            return;

        DetermineMode(uid, configurator, target, user);

        if (configurator.LinkModeActive)
        {
            ToggleBufferedLinkDevice(uid, configurator, target, user);
            return;
        }

        if (!HasComp<DeviceListComponent>(target))
        {
            TryAddNetworkDevice(uid, target, user, configurator);
            return;
        }

        OpenDeviceListUi(uid, target, user, configurator);
    }

    private void DetermineMode(EntityUid configuratorUid, NetworkConfiguratorComponent configurator, EntityUid? target, EntityUid userUid)
    {
        var hasLinking = HasComp<DeviceLinkSinkComponent>(target) || HasComp<DeviceLinkSourceComponent>(target);

        if (hasLinking && HasComp<DeviceListComponent>(target) || hasLinking == configurator.LinkModeActive)
            return;

        if (hasLinking)
        {
            SetMode(configuratorUid, configurator, userUid, true);
            return;
        }

        if (HasComp<DeviceNetworkComponent>(target))
            SetMode(configuratorUid, configurator, userUid, false);
    }

    #endregion

    #region Verbs

    /// <summary>
    /// Adds the interaction verb which is either configuring device lists or saving a device onto the configurator
    /// </summary>
    private void OnAddInteractVerb(EntityUid uid, NetworkConfiguratorComponent configurator, GetVerbsEvent<UtilityVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !args.Using.HasValue)
            return;

        var verb = new UtilityVerb
        {
            Act = () => OnUsed(uid, configurator, args.Target, args.User),
            Impact = LogImpact.Low
        };

        if (configurator.LinkModeActive && IsLinkable(args.Target))
        {
            var buffered = configurator.LinkBuffer.Contains(args.Target);
            verb.Text = Loc.GetString(buffered ? "network-configurator-link-buffer-remove" : "network-configurator-link-buffer-add");
            verb.Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/in.svg.192dpi.png"));
            args.Verbs.Add(verb);
        }
        else if (HasComp<DeviceNetworkComponent>(args.Target))
        {
            var isDeviceList = HasComp<DeviceListComponent>(args.Target);
            verb.Text = Loc.GetString(isDeviceList ? "network-configurator-configure" : "network-configurator-save-device");
            verb.Icon = isDeviceList
                ? new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png"))
                : new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/in.svg.192dpi.png"));
            args.Verbs.Add(verb);
        }
    }

    /// <summary>
    /// Powerful. Funny alt interact using.
    /// Adds an alternative verb for saving a device on the configurator for entities with the <see cref="DeviceListComponent"/>.
    /// Allows alt clicking entities with a network configurator that would otherwise trigger a different action like entities
    /// with a <see cref="DeviceListComponent"/>
    /// </summary>
    private void OnAddAlternativeSaveDeviceVerb(EntityUid uid, DeviceNetworkComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !args.Using.HasValue
            || !TryComp<NetworkConfiguratorComponent>(args.Using.Value, out var configurator))
            return;

        if (!configurator.LinkModeActive && HasComp<DeviceListComponent>(args.Target))
        {
            AlternativeVerb verb = new()
            {
                Text = Loc.GetString("network-configurator-save-device"),
                Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/in.svg.192dpi.png")),
                Act = () => TryAddNetworkDevice(args.Target, args.Using.Value, args.User),
                Impact = LogImpact.Low
            };
            args.Verbs.Add(verb);
            return;
        }

        if (configurator.LinkModeActive
            && configurator.LinkBuffer.Any(ent => ent != args.Target)
            && IsLinkable(args.Target))
        {
            var configuratorUid = args.Using.Value;
            AlternativeVerb verb = new()
            {
                Text = Loc.GetString("network-configurator-link-defaults-buffer"),
                Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/in.svg.192dpi.png")),
                Act = () => TryLinkDefaults(configuratorUid, configurator, args.Target, args.User),
                Impact = LogImpact.Low
            };
            args.Verbs.Add(verb);
        }
    }

    private void OnAddLinkBufferVerbs(EntityUid uid, NetworkConfiguratorComponent configurator, GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Using != uid)
            return;

        var user = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("network-configurator-open-link-menu"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () => OpenDeviceLinkUi(uid, configurator, user),
            Impact = LogImpact.Low
        });

        if (configurator.LinkBuffer.Count == 0)
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("network-configurator-link-buffer-clear"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/delete.svg.192dpi.png")),
            Act = () => ClearLinkBuffer(uid, configurator, user),
            Impact = LogImpact.Low
        });
    }

    private void OnAddSwitchModeVerb(EntityUid uid, NetworkConfiguratorComponent configurator, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !args.Using.HasValue || !HasComp<NetworkConfiguratorComponent>(args.Target))
            return;

        AlternativeVerb verb = new()
        {
            Text = Loc.GetString("network-configurator-switch-mode"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () => SwitchMode(args.User, args.Target, configurator),
            Impact = LogImpact.Low
        };
        args.Verbs.Add(verb);
    }

    #endregion

    #region UI

    protected override void OnLinkModeActivated(Entity<NetworkConfiguratorComponent> configurator, EntityUid user)
    {
        if (_uiSystem.IsUiOpen(configurator.Owner, NetworkConfiguratorUiKey.Link, user))
        {
            _uiSystem.CloseUi(configurator.Owner, NetworkConfiguratorUiKey.Link, user);
            return;
        }

        OpenDeviceLinkUi(configurator, configurator.Comp, user);
    }

    private void OpenDeviceLinkUi(EntityUid configuratorUid, NetworkConfiguratorComponent configurator, EntityUid userUid)
    {
        _uiSystem.OpenUi(configuratorUid, NetworkConfiguratorUiKey.Link, userUid);
        UpdateLinkUiState(configuratorUid, configurator);
    }

    /// <summary>
    /// Sends the link buffer and every link from or to the buffered devices to the link ui
    /// </summary>
    private void UpdateLinkUiState(EntityUid configuratorUid, NetworkConfiguratorComponent configurator)
    {
        if (!_uiSystem.IsUiOpen(configuratorUid, NetworkConfiguratorUiKey.Link))
            return;

        PruneLinkBuffer(configuratorUid, configurator);

        var devices = new List<DeviceLinkBufferEntry>();
        var links = new List<DeviceLinkEntry>();
        var seen = new HashSet<(EntityUid, EntityUid, ProtoId<SourcePortPrototype>, ProtoId<SinkPortPrototype>)>();

        void AddLinks(EntityUid sourceUid, EntityUid sinkUid,
            HashSet<(ProtoId<SourcePortPrototype> source, ProtoId<SinkPortPrototype> sink)> ports)
        {
            if (TerminatingOrDeleted(sourceUid) || TerminatingOrDeleted(sinkUid))
                return;

            foreach (var (sourcePort, sinkPort) in ports)
            {
                if (!seen.Add((sourceUid, sinkUid, sourcePort, sinkPort)))
                    continue;

                links.Add(new DeviceLinkEntry(GetNetEntity(sourceUid), Name(sourceUid), sourcePort,
                    GetNetEntity(sinkUid), Name(sinkUid), sinkPort));
            }
        }

        foreach (var device in configurator.LinkBuffer)
        {
            TryComp(device, out DeviceLinkSourceComponent? source);
            TryComp(device, out DeviceLinkSinkComponent? sink);
            var address = TryComp(device, out DeviceNetworkComponent? network) ? network.Address : string.Empty;

            devices.Add(new DeviceLinkBufferEntry(
                GetNetEntity(device),
                Name(device),
                address,
                source?.Ports?.ToList(),
                sink?.Ports?.ToList()));

            if (source != null)
            {
                foreach (var (sinkUid, ports) in source.LinkedPorts)
                {
                    AddLinks(device, sinkUid, ports);
                }
            }

            if (sink == null)
                continue;

            foreach (var sourceUid in sink.LinkedSources)
            {
                if (TryComp(sourceUid, out DeviceLinkSourceComponent? linkedSource))
                    AddLinks(sourceUid, device, _deviceLinkSystem.GetLinks(sourceUid, device, linkedSource));
            }
        }

        _uiSystem.SetUiState(configuratorUid, NetworkConfiguratorUiKey.Link, new DeviceLinkUserInterfaceState(devices, links));
    }

    /// <summary>
    /// Opens the config ui. It can be used to modify the devices in the targets device list.
    /// </summary>
    private void OpenDeviceListUi(EntityUid configuratorUid, EntityUid? targetUid, EntityUid userUid, NetworkConfiguratorComponent configurator)
    {
        if (Delay(configurator))
            return;

        if (!targetUid.HasValue || !AccessCheck(targetUid.Value, userUid, configurator))
            return;

        if (!TryComp(targetUid, out DeviceListComponent? list))
            return;

        if (TryComp(configurator.ActiveDeviceList, out DeviceListComponent? oldList))
            oldList.Configurators.Remove(configuratorUid);

        list.Configurators.Add(configuratorUid);
        configurator.ActiveDeviceList = targetUid;
        Dirty(configuratorUid, configurator);

        if (_uiSystem.TryOpenUi(configuratorUid, NetworkConfiguratorUiKey.Configure, userUid))
        {
            _uiSystem.SetUiState(configuratorUid, NetworkConfiguratorUiKey.Configure, new DeviceListUserInterfaceState(
                _deviceListSystem.GetDeviceList(configurator.ActiveDeviceList.Value)
                    .Select(v => (v.Key, MetaData(v.Value).EntityName)).ToHashSet()
            ));
        }
    }

    /// <summary>
    /// Sends the list of saved devices to the ui
    /// </summary>
    private void UpdateListUiState(EntityUid uid, NetworkConfiguratorComponent component)
    {
        HashSet<(string address, string name)> devices = new();
        HashSet<string> invalidDevices = new();

        foreach (var pair in component.Devices)
        {
            if (!Exists(pair.Value))
            {
                invalidDevices.Add(pair.Key);
                continue;
            }

            devices.Add((pair.Key, Name(pair.Value)));
        }

        //Remove saved entities that don't exist anymore
        foreach (var invalidDevice in invalidDevices)
        {
            component.Devices.Remove(invalidDevice);
        }

        _uiSystem.SetUiState(uid, NetworkConfiguratorUiKey.List, new NetworkConfiguratorUserInterfaceState(devices));
    }

    /// <summary>
    /// Clears the active device list when the ui is closed
    /// </summary>
    private void OnUiClosed(EntityUid uid, NetworkConfiguratorComponent component, BoundUIClosedEvent args)
    {
        // The link buffer is kept when the link ui gets closed
        if (!args.UiKey.Equals(NetworkConfiguratorUiKey.Configure)
            && !args.UiKey.Equals(NetworkConfiguratorUiKey.List))
        {
            return;
        }

        if (TryComp(component.ActiveDeviceList, out DeviceListComponent? list))
        {
            list.Configurators.Remove(uid);
        }

        component.ActiveDeviceList = null;
    }

    public void OnDeviceListShutdown(Entity<NetworkConfiguratorComponent?> conf, Entity<DeviceListComponent> list)
    {
        list.Comp.Configurators.Remove(conf.Owner);
        if (Resolve(conf.Owner, ref conf.Comp))
            conf.Comp.ActiveDeviceList = null;
    }

    /// <summary>
    /// Removes a device from the saved devices list
    /// </summary>
    private void OnRemoveDevice(EntityUid uid, NetworkConfiguratorComponent component, NetworkConfiguratorRemoveDeviceMessage args)
    {
        if (component.Devices.TryGetValue(args.Address, out var removedDevice))
        {
            _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
                $"{ToPrettyString(args.Actor):actor} removed buffered device {ToPrettyString(removedDevice):subject} from {ToPrettyString(uid):tool}");
        }

        component.Devices.Remove(args.Address);
        if (TryComp(removedDevice, out DeviceNetworkComponent? device))
            device.Configurators.Remove(uid);

        UpdateListUiState(uid, component);
    }

    /// <summary>
    /// Clears the saved devices
    /// </summary>
    private void OnClearDevice(EntityUid uid, NetworkConfiguratorComponent component, NetworkConfiguratorClearDevicesMessage args)
    {
        _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
            $"{ToPrettyString(args.Actor):actor} cleared buffered devices from {ToPrettyString(uid):tool}");

        ClearDevices(uid, component);
        UpdateListUiState(uid, component);
    }

    private void ClearDevices(EntityUid uid, NetworkConfiguratorComponent component)
    {
        var query = GetEntityQuery<DeviceNetworkComponent>();
        foreach (var device in component.Devices.Values)
        {
            if (query.TryGetComponent(device, out var comp))
                comp.Configurators.Remove(uid);
        }

        component.Devices.Clear();
    }

    #region Link buffer

    private bool IsLinked(DeviceLinkSourceComponent source, EntityUid sinkUid,
        ProtoId<SourcePortPrototype> sourcePort, ProtoId<SinkPortPrototype> sinkPort)
    {
        return _deviceLinkSystem.IsLinked(source, sinkUid, sourcePort, sinkPort);
    }

    /// <summary>
    /// Resolves the given entities, ignoring everything that isn't in the link buffer, doesn't have the component
    /// or that the user has no access to
    /// </summary>
    private List<Entity<T>> GetBufferedDevices<T>(NetworkConfiguratorComponent configurator, List<NetEntity> netEntities, EntityUid user)
        where T : IComponent
    {
        var result = new List<Entity<T>>();
        foreach (var netEntity in netEntities)
        {
            if (!TryGetEntity(netEntity, out var uid)
                || !configurator.LinkBuffer.Contains(uid.Value)
                || result.Any(ent => ent.Owner == uid.Value)
                || !TryComp(uid, out T? comp)
                || !HasAccess(uid.Value, user))
                continue;

            result.Add((uid.Value, comp));
        }

        return result;
    }

    /// <summary>
    /// Toggles one port link for every selected source and sink pair
    /// </summary>
    private void OnLinkPorts(EntityUid uid, NetworkConfiguratorComponent configurator, NetworkConfiguratorLinkPortsMessage args)
    {
        PruneLinkBuffer(uid, configurator);

        var sinks = GetBufferedDevices<DeviceLinkSinkComponent>(configurator, args.Sinks, args.Actor)
            .Where(sink => _deviceLinkSystem.HasPort(sink.Comp, args.SinkPort))
            .ToList();

        var pairs = new List<(Entity<DeviceLinkSourceComponent> Source, Entity<DeviceLinkSinkComponent> Sink)>();
        foreach (var source in GetBufferedDevices<DeviceLinkSourceComponent>(configurator, args.Sources, args.Actor))
        {
            if (!_deviceLinkSystem.HasPort(source.Comp, args.SourcePort))
                continue;

            foreach (var sink in sinks)
            {
                if (source.Owner != sink.Owner)
                    pairs.Add((source, sink));
            }
        }

        if (pairs.Count == 0)
            return;

        // If every pair already has this link it gets removed everywhere, otherwise the missing ones get added
        var unlink = pairs.All(pair => IsLinked(pair.Source.Comp, pair.Sink, args.SourcePort, args.SinkPort));
        var changed = 0;
        foreach (var (source, sink) in pairs)
        {
            if (!unlink && IsLinked(source.Comp, sink, args.SourcePort, args.SinkPort))
                continue;

            if (_deviceLinkSystem.ToggleLink(null, source, sink, args.SourcePort, args.SinkPort, source.Comp, sink.Comp))
                changed++;
        }

        _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
            $"{ToPrettyString(args.Actor):actor} {(unlink ? "unlinked" : "linked")} {args.SourcePort} to {args.SinkPort} on {changed} device pairs with {ToPrettyString(uid):tool}");

        var popup = changed == 0
            ? "network-configurator-links-none"
            : unlink ? "network-configurator-links-removed" : "network-configurator-links-added";
        _popupSystem.PopupCursor(Loc.GetString(popup, ("count", changed)), args.Actor, PopupType.Medium);

        UpdateLinkUiState(uid, configurator);
    }

    private void OnLinkDefaults(EntityUid uid, NetworkConfiguratorComponent configurator, NetworkConfiguratorLinkDefaultsMessage args)
    {
        PruneLinkBuffer(uid, configurator);

        var sources = GetBufferedDevices<DeviceLinkSourceComponent>(configurator, args.Sources, args.Actor).Select(ent => ent.Owner).ToList();
        var sinks = GetBufferedDevices<DeviceLinkSinkComponent>(configurator, args.Sinks, args.Actor).Select(ent => ent.Owner).ToList();
        var linked = LinkDefaultPairs(args.Actor, sources, sinks);

        _popupSystem.PopupCursor(Loc.GetString(linked > 0 ? "network-configurator-links-added" : "network-configurator-links-none",
            ("count", linked)), args.Actor, PopupType.Medium);
        UpdateLinkUiState(uid, configurator);
    }

    /// <summary>
    /// Adds the default links of every source port to every sink. Existing links are kept.
    /// </summary>
    /// <returns>The amount of links that got added</returns>
    private int LinkDefaultPairs(EntityUid user, List<EntityUid> sources, List<EntityUid> sinks)
    {
        var linked = 0;
        foreach (var sourceUid in sources)
        {
            if (!TryComp(sourceUid, out DeviceLinkSourceComponent? source))
                continue;

            var defaults = new List<(ProtoId<SourcePortPrototype> source, ProtoId<SinkPortPrototype> sink)>();
            foreach (var port in _deviceLinkSystem.GetSourcePorts(sourceUid, source))
            {
                if (port.DefaultLinks == null)
                    continue;

                foreach (var sinkPort in port.DefaultLinks)
                {
                    defaults.Add((port.ID, sinkPort));
                }
            }

            if (defaults.Count == 0)
                continue;

            foreach (var sinkUid in sinks)
            {
                if (sinkUid == sourceUid || !TryComp(sinkUid, out DeviceLinkSinkComponent? sink))
                    continue;

                foreach (var (sourcePort, sinkPort) in defaults)
                {
                    if (!_deviceLinkSystem.HasPort(sink, sinkPort) || IsLinked(source, sinkUid, sourcePort, sinkPort))
                        continue;

                    if (_deviceLinkSystem.ToggleLink(null, sourceUid, sinkUid, sourcePort, sinkPort, source, sink))
                        linked++;
                }
            }
        }

        if (linked > 0)
            _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low, $"{ToPrettyString(user):actor} linked {linked} default links");

        return linked;
    }

    /// <summary>
    /// Removes every link between the selected source and sink pairs
    /// </summary>
    private void OnClearLinks(EntityUid uid, NetworkConfiguratorComponent configurator, NetworkConfiguratorClearLinksMessage args)
    {
        PruneLinkBuffer(uid, configurator);

        var sinks = GetBufferedDevices<DeviceLinkSinkComponent>(configurator, args.Sinks, args.Actor);
        var cleared = 0;
        foreach (var source in GetBufferedDevices<DeviceLinkSourceComponent>(configurator, args.Sources, args.Actor))
        {
            foreach (var sink in sinks)
            {
                if (source.Owner == sink.Owner)
                    continue;

                var ports = _deviceLinkSystem.GetLinks(source, sink, source.Comp);
                if (ports.Count == 0)
                    continue;

                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
                    $"{ToPrettyString(args.Actor):actor} cleared links between {ToPrettyString(source):subject} and {ToPrettyString(sink):subject2} with {ToPrettyString(uid):tool}");

                cleared += ports.Count;
                _deviceLinkSystem.RemoveSinkFromSource(source, sink, source.Comp, sink.Comp);
            }
        }

        _popupSystem.PopupCursor(Loc.GetString(cleared > 0 ? "network-configurator-links-removed" : "network-configurator-links-none",
            ("count", cleared)), args.Actor, PopupType.Medium);
        UpdateLinkUiState(uid, configurator);
    }

    /// <summary>
    /// Removes a single link from or to a buffered device
    /// </summary>
    private void OnRemoveLink(EntityUid uid, NetworkConfiguratorComponent configurator, NetworkConfiguratorRemoveLinkMessage args)
    {
        if (!TryGetEntity(args.Source, out var sourceUid) || !TryGetEntity(args.Sink, out var sinkUid))
            return;

        if (!configurator.LinkBuffer.Contains(sourceUid.Value) && !configurator.LinkBuffer.Contains(sinkUid.Value))
            return;

        // The other end may be outside the buffer and was never access checked, so check both ends here.
        // Otherwise buffering an unrestricted button lets anyone cut it from a restricted door.
        if (!AccessCheck(sourceUid.Value, args.Actor, configurator) || !AccessCheck(sinkUid.Value, args.Actor, configurator))
            return;

        if (!TryComp(sourceUid, out DeviceLinkSourceComponent? source)
            || !TryComp(sinkUid, out DeviceLinkSinkComponent? sink)
            || !IsLinked(source, sinkUid.Value, args.SourcePort, args.SinkPort))
            return;

        _deviceLinkSystem.ToggleLink(args.Actor, sourceUid.Value, sinkUid.Value, args.SourcePort, args.SinkPort, source, sink);
        UpdateLinkUiState(uid, configurator);
    }

    private void OnRemoveBufferedDevice(EntityUid uid, NetworkConfiguratorComponent configurator, NetworkConfiguratorRemoveBufferedDeviceMessage args)
    {
        if (!TryGetEntity(args.Device, out var device) || !configurator.LinkBuffer.Remove(device.Value))
            return;

        if (configurator.LinkBuffer.Count == 0)
            configurator.LinkMenuAutoOpened = false;

        Dirty(uid, configurator);
        UpdateLinkUiState(uid, configurator);
    }

    private void OnClearLinkBuffer(EntityUid uid, NetworkConfiguratorComponent configurator, NetworkConfiguratorClearLinkBufferMessage args)
    {
        ClearLinkBuffer(uid, configurator, args.Actor);
    }

    private void ClearLinkBuffer(EntityUid uid, NetworkConfiguratorComponent configurator, EntityUid user)
    {
        configurator.LinkBuffer.Clear();
        configurator.LinkMenuAutoOpened = false;
        Dirty(uid, configurator);

        _popupSystem.PopupCursor(Loc.GetString("network-configurator-link-buffer-cleared"), user);
        UpdateLinkUiState(uid, configurator);
    }

    #endregion

    /// <summary>
    /// Handles all the button presses from the config ui.
    /// Modifies, copies or visualizes the targets device list
    /// </summary>
    private void OnConfigButtonPressed(EntityUid uid, NetworkConfiguratorComponent component, NetworkConfiguratorButtonPressedMessage args)
    {
        if (!component.ActiveDeviceList.HasValue)
            return;

        var result = DeviceListUpdateResult.NoComponent;
        switch (args.ButtonKey)
        {
            case NetworkConfiguratorButtonKey.Set:
                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
                    $"{ToPrettyString(args.Actor):actor} set device links to {ToPrettyString(component.ActiveDeviceList.Value):subject} with {ToPrettyString(uid):tool}");

                result = _deviceListSystem.UpdateDeviceList(component.ActiveDeviceList.Value, new HashSet<EntityUid>(component.Devices.Values));
                break;
            case NetworkConfiguratorButtonKey.Add:
                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
                    $"{ToPrettyString(args.Actor):actor} added device links to {ToPrettyString(component.ActiveDeviceList.Value):subject} with {ToPrettyString(uid):tool}");

                result = _deviceListSystem.UpdateDeviceList(component.ActiveDeviceList.Value, new HashSet<EntityUid>(component.Devices.Values), true);
                break;
            case NetworkConfiguratorButtonKey.Clear:
                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
                    $"{ToPrettyString(args.Actor):actor} cleared device links from {ToPrettyString(component.ActiveDeviceList.Value):subject} with {ToPrettyString(uid):tool}");
                result = _deviceListSystem.UpdateDeviceList(component.ActiveDeviceList.Value, new HashSet<EntityUid>());
                break;
            case NetworkConfiguratorButtonKey.Copy:
                _adminLogger.Add(LogType.DeviceLinking, LogImpact.Low,
                    $"{ToPrettyString(args.Actor):actor} copied devices from {ToPrettyString(component.ActiveDeviceList.Value):subject} to {ToPrettyString(uid):tool}");

                ClearDevices(uid, component);

                var query = GetEntityQuery<DeviceNetworkComponent>();
                foreach (var (addr, device) in _deviceListSystem.GetDeviceList(component.ActiveDeviceList.Value))
                {
                    if (query.TryGetComponent(device, out var comp))
                    {
                        component.Devices.Add(addr, device);
                        comp.Configurators.Add(uid);
                    }
                }
                UpdateListUiState(uid, component);
                return;
            case NetworkConfiguratorButtonKey.Show:
                break;
        }

        var resultText = result switch
        {
            DeviceListUpdateResult.TooManyDevices => Loc.GetString("network-configurator-too-many-devices"),
            DeviceListUpdateResult.UpdateOk => Loc.GetString("network-configurator-update-ok"),
            _ => "error"
        };

        _popupSystem.PopupCursor(Loc.GetString(resultText), args.Actor, PopupType.Medium);
        _uiSystem.SetUiState(
            uid,
            NetworkConfiguratorUiKey.Configure,
            new DeviceListUserInterfaceState(
                _deviceListSystem.GetDeviceList(component.ActiveDeviceList.Value)
                    .Select(v => (v.Key, MetaData(v.Value).EntityName)).ToHashSet()));
    }

    public void OnDeviceShutdown(Entity<NetworkConfiguratorComponent?> conf, Entity<DeviceNetworkComponent> device)
    {
        device.Comp.Configurators.Remove(conf.Owner);
        if (!Resolve(conf.Owner, ref conf.Comp, false)) // rat-change
            return;

        foreach (var (addr, dev) in conf.Comp.Devices)
        {
            if (device.Owner == dev)
                conf.Comp.Devices.Remove(addr);
        }

        UpdateListUiState(conf, conf.Comp);
    }
    #endregion
}
