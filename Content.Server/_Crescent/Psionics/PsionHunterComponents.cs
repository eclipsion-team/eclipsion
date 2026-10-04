using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Psionics;

/// <summary>
/// A Saint's Militia hunter: trained to find psions, never one. Hands out Hunter's Sense and Scrutiny, two actions
/// that find psions without touching the noosphere, and keeps the bearer from ever being psionic. Psionics that
/// arrive from anywhere (a trait, an implant, a glimmer event, cloning) are stripped again on the next tick.
/// </summary>
[RegisterComponent, Access(typeof(PsionHunterSystem))]
public sealed partial class PsionHunterComponent : Component
{
    [DataField]
    public EntProtoId SenseAction = "ActionPsionHunterSense";

    [DataField]
    public EntityUid? SenseActionEntity;

    [DataField]
    public EntProtoId ScrutinyAction = "ActionPsionHunterScrutiny";

    [DataField]
    public EntityUid? ScrutinyActionEntity;

    /// <summary>How far Hunter's Sense reaches, in tiles.</summary>
    [DataField]
    public float SenseRange = 15f;

    /// <summary>Within this distance Hunter's Sense calls the psion close.</summary>
    [DataField]
    public float SenseCloseRange = 5f;

    /// <summary>How long the hunter has to study someone before Scrutiny gives its answer.</summary>
    [DataField]
    public TimeSpan ScrutinyDelay = TimeSpan.FromSeconds(4);
}

/// <summary>
/// A hunt leader's Null Ward: for <see cref="Duration"/> the bearer projects a null field, the same one the
/// χ Waveform Misalignment trait and nullifier helmets carry, and every psion inside loses the noosphere.
/// </summary>
[RegisterComponent, Access(typeof(PsionHunterSystem))]
public sealed partial class NullWardComponent : Component
{
    [DataField]
    public EntProtoId Action = "ActionNullWard";

    [DataField]
    public EntityUid? ActionEntity;

    /// <summary>Radius of the field, in tiles.</summary>
    [DataField]
    public float Range = 5f;

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When the field the ward put up comes down. Null while no ward is up, and also when the bearer's field came
    /// from somewhere else, which the ward never touches.
    /// </summary>
    [ViewVariables]
    public TimeSpan? ActiveUntil;
}
