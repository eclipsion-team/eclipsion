using Content.Shared._Crescent.Religion;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Religion.Components;

/// <summary>
///     The faith a character currently holds. Every humanoid body carries one through its species prototype; players
///     get theirs set from their profile when they spawn, or from their job's <c>requiredReligion</c>.
/// </summary>
/// <remarks>
///     Server only on purpose: a secret convert should not be readable off the network.
/// </remarks>
[RegisterComponent, Access(typeof(ReligionSystem))]
public sealed partial class BelieverComponent : Component
{
    [DataField]
    public ProtoId<ReligionPrototype> Religion = ReligionPrototype.Default;

    /// <summary>
    ///     The profile faction the character spawned with. A DSM character working a Gliessian job is CMM in round,
    ///     but should keep the faiths their own faction allows.
    /// </summary>
    [DataField]
    public string HomeFaction = string.Empty;

    /// <summary>
    ///     Prayer lines of <see cref="PrayerBagReligion"/> not yet whispered this cycle, drawn from the end.
    /// </summary>
    [ViewVariables]
    public List<LocId> PrayerBag = new();

    [ViewVariables]
    public ProtoId<ReligionPrototype>? PrayerBagReligion;

    /// <summary>
    ///     Kept so a refilled bag does not open with the line that closed the last one.
    /// </summary>
    [ViewVariables]
    public LocId? LastPrayer;
}

/// <summary>
///     An altar where characters join <see cref="Religion"/>.
/// </summary>
[RegisterComponent]
public sealed partial class ReligionAltarComponent : Component
{
    [DataField(required: true)]
    public ProtoId<ReligionPrototype> Religion;

    /// <summary>
    ///     How far from the altar a leader can stand and still hear a request, accept it and officiate the rite.
    /// </summary>
    [DataField]
    public float LeaderRange = 4f;

    /// <summary>
    ///     How far the convert can drift from the altar before the rite breaks.
    /// </summary>
    [DataField]
    public float RiteRange = 2f;

    /// <summary>
    ///     How long a request to join waits for a leader's answer.
    /// </summary>
    [DataField]
    public TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
}

/// <summary>
///     Someone who can accept converts into <see cref="Religion"/> at its altars, for faiths with
///     <see cref="ReligionPrototype.RequiresLeader"/>.
/// </summary>
[RegisterComponent]
public sealed partial class ReligiousLeaderComponent : Component
{
    [DataField(required: true)]
    public ProtoId<ReligionPrototype> Religion;
}

/// <summary>
///     Sits on someone who asked to join a leader-gated faith and is waiting on an answer.
/// </summary>
[RegisterComponent, Access(typeof(ReligionSystem))]
public sealed partial class ReligionJoinRequestComponent : Component
{
    [DataField]
    public EntityUid Altar;

    [DataField]
    public ProtoId<ReligionPrototype> Religion;

    [DataField]
    public TimeSpan ExpiresAt;
}

/// <summary>
///     Raised on someone right before their faith is changed. Cancel it to keep them where they are.
/// </summary>
[ByRefEvent]
public record struct ReligionChangeAttemptEvent(ProtoId<ReligionPrototype> NewReligion)
{
    public bool Cancelled;

    /// <summary>
    ///     Shown to the believer when the attempt is cancelled.
    /// </summary>
    public string? Reason;
}

/// <summary>
///     Raised on a believer after their faith changed.
/// </summary>
/// <param name="Officiant">The leader who accepted them, if any.</param>
/// <param name="Forced">Whether this was done to them rather than chosen.</param>
[ByRefEvent]
public readonly record struct ReligionChangedEvent(
    ProtoId<ReligionPrototype> OldReligion,
    ProtoId<ReligionPrototype> NewReligion,
    EntityUid? Officiant,
    bool Forced);
