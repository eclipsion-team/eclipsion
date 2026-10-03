using Content.Shared._Crescent.Religion;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Religion.Components;

/// <summary>
///     The TAP Prophet. Draws Veiled Eyes, forces the Binding on whoever stands on one, and keeps watch over the
///     Host of the Mother: everyone the Prophet has brought into the Covenant.
/// </summary>
[RegisterComponent, Access(typeof(VeiledProphetSystem))]
public sealed partial class VeiledProphetComponent : Component
{
    [DataField]
    public ProtoId<ReligionPrototype> Religion = "VeiledMother";

    [DataField]
    public EntProtoId DrawEyeAction = "ActionDrawVeiledEye";

    [DataField]
    public EntityUid? DrawEyeActionEntity;

    [DataField]
    public EntProtoId HostAction = "ActionVeiledHost";

    [DataField]
    public EntityUid? HostActionEntity;

    [DataField]
    public EntProtoId EyePrototype = "VeiledEyeRune";

    [DataField]
    public TimeSpan DrawTime = TimeSpan.FromSeconds(8);

    /// <summary>
    ///     Drawing past this closes the oldest eye.
    /// </summary>
    [DataField]
    public int MaxEyes = 2;

    [ViewVariables]
    public List<EntityUid> Eyes = new();

    /// <summary>
    ///     Whispered while drawing an eye, one picked at random.
    /// </summary>
    [DataField]
    public List<LocId> DrawLines = new();

    /// <summary>
    ///     Spoken by the Prophet one line per step of a forced Binding.
    /// </summary>
    [DataField]
    public List<LocId> BindingLines = new();

    [DataField]
    public TimeSpan BindingStepDelay = TimeSpan.FromSeconds(3);

    [DataField]
    public SoundSpecifier BindingSound = new SoundPathSpecifier("/Audio/Ambience/Antag/headrev_start.ogg");

    [DataField]
    public Color BindingColor = Color.FromHex("#B57BFF");

    /// <summary>
    ///     Spoken by the Prophet when chastising a straying member of the Host, one picked at random.
    /// </summary>
    [DataField]
    public List<LocId> ChastiseLines = new();

    [DataField]
    public TimeSpan ChastiseStun = TimeSpan.FromSeconds(4);

    /// <summary>
    ///     Per member, so the Prophet cannot chain one convert into a permanent stun.
    /// </summary>
    [DataField]
    public TimeSpan ChastiseCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Everyone the Prophet brought into the Covenant, bound or accepted.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> Host = new();
}

/// <summary>
///     A member of a Prophet's Host.
/// </summary>
[RegisterComponent, Access(typeof(VeiledProphetSystem))]
public sealed partial class VeiledHostMemberComponent : Component
{
    [ViewVariables]
    public EntityUid Prophet;

    /// <summary>
    ///     Bound on a Veiled Eye rather than accepted at the altar. Bound members are told to follow the Prophet, can
    ///     be chastised for straying, and cannot leave the Covenant while the Prophet lives.
    /// </summary>
    [ViewVariables]
    public bool Bound;

    [ViewVariables]
    public TimeSpan NextChastise;
}

/// <summary>
///     A Veiled Eye drawn on the floor by a Prophet. Whoever stands within its area can have the Binding forced on them.
/// </summary>
[RegisterComponent, Access(typeof(VeiledProphetSystem))]
public sealed partial class VeiledEyeComponent : Component
{
    [ViewVariables]
    public EntityUid? Prophet;

    /// <summary>
    ///     Half the side of the square the eye watches over, in tiles. 2 makes a 4x4 area around the eye.
    /// </summary>
    [DataField]
    public float HalfExtent = 2f;

    [DataField]
    public TimeSpan ScrubTime = TimeSpan.FromSeconds(6);
}
