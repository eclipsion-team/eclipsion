using Content.Shared.DeviceLinking;
using Content.Shared.DeviceNetwork.Systems;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.DeviceNetwork.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedNetworkConfiguratorSystem))]
public sealed partial class NetworkConfiguratorComponent : Component
{
    // AAAAA ALL OF THESE FAA
    /// <summary>
    /// Determines whether the configurator is in linking mode or list mode
    /// </summary>
    [DataField, AutoNetworkedField]
    [ViewVariables(VVAccess.ReadWrite)]
    public bool LinkModeActive = true;

    /// <summary>
    /// The entity containing a <see cref="DeviceListComponent"/> this configurator is currently interacting with
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid? ActiveDeviceList { get; set; }

    /// <summary>
    /// Devices with a <see cref="DeviceLinkSourceComponent"/> or <see cref="DeviceLinkSinkComponent"/> buffered for linking.
    /// Every selected source can be linked to every selected sink at once through the link menu.
    /// </summary>
    [AutoNetworkedField]
    public List<EntityUid> LinkBuffer = new();

    /// <summary>
    /// Maximum amount of devices the link buffer can hold
    /// </summary>
    [DataField]
    public int MaxLinkBuffer = 32;

    /// <summary>
    /// Whether the link menu was already opened automatically for the current buffer.
    /// Reset when the buffer is emptied.
    /// </summary>
    public bool LinkMenuAutoOpened;

    /// <summary>
    /// The list of devices stored in the configurator
    /// </summary>
    [DataField]
    public Dictionary<string, EntityUid> Devices = new();

    [DataField]
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan UseDelay = TimeSpan.FromSeconds(0.5);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan LastUseAttempt;

    [DataField]
    public SoundSpecifier SoundNoAccess = new SoundPathSpecifier("/Audio/Machines/custom_deny.ogg");

    [DataField]
    public SoundSpecifier SoundSwitchMode = new SoundPathSpecifier("/Audio/Machines/quickbeep.ogg");

    [DataField]
    public bool ShowLabel = true; // Shitmed - Starlight Abductors Change
}
