namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: works the mob's internals for it - on once its loadout has been equipped, then kept on while the
/// air around it is unbreathable and switched off while it isn't.
/// </summary>
/// <remarks>
/// Boarding NPCs are spawned in hardsuits with a tank and a breath mask, but nothing in the HTN ever
/// presses the internals button, so they would suffocate the moment they stepped into vacuum - which is
/// most of the places anyone wants to use them. Leaving the tank open all the time is no better: a garrison
/// mapped onto a station stands there for the whole round, and breathes its tank dry in a pressurised room
/// long before anyone comes to fight it. So the tank is only drawn on when it has to be, and once one runs
/// low the NPC moves over to the fullest other tank it carries. Once those are all spent it fetches a fresh
/// one - a loose tank nearby or one out of a vending machine - and drops the empty.
/// </remarks>
[RegisterComponent, Access(typeof(NpcStartInternalsSystem))]
public sealed partial class NpcStartInternalsComponent : Component
{
    /// <summary>
    /// How often the air around it and the tank it breathes from are checked.
    /// </summary>
    [DataField]
    public TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the air has to stay breathable before the tank is closed again, so walking along the edge
    /// of a breach doesn't click the valve on and off every check.
    /// </summary>
    [DataField]
    public TimeSpan SwitchOffDelay = TimeSpan.FromSeconds(6);

    [ViewVariables]
    public TimeSpan NextCheck;

    /// <summary>
    /// When the air around it last became breathable, while it still is.
    /// </summary>
    [ViewVariables]
    public TimeSpan? BreathableSince;

    /// <summary>
    /// Every carried tank but the jetpack below this (kPa) and it goes for a fresh one. Full is about 1000.
    /// </summary>
    [DataField]
    public float RefillPressure = 100f;

    /// <summary>
    /// The least a loose tank has to hold (kPa) to be worth walking over to.
    /// </summary>
    [DataField]
    public float SourceMinPressure = 500f;

    /// <summary>
    /// How far it looks for a loose tank, or a vending machine stocking one. Same grid only.
    /// </summary>
    [DataField]
    public float SourceRange = 20f;

    [DataField]
    public TimeSpan SourceSearchInterval = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a source it couldn't reach or use is left alone.
    /// </summary>
    [DataField]
    public TimeSpan SourceIgnoreTime = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Trips to one source before giving up on it.
    /// </summary>
    [DataField]
    public int SourceMaxAttempts = 4;

    [ViewVariables]
    public NpcAirNeed AirNeed;

    /// <summary>
    /// The loose tank or vending machine it means to get air from.
    /// </summary>
    [ViewVariables]
    public EntityUid? AirSource;

    /// <summary>
    /// What it takes when <see cref="AirSource"/> is a vending machine.
    /// </summary>
    [ViewVariables]
    public string? AirSourceItem;

    /// <summary>
    /// The source last confirmed reachable, so it isn't pathfound again on every replan.
    /// </summary>
    [ViewVariables]
    public EntityUid? CheckedSource;

    [ViewVariables]
    public int SourceAttempts;

    [ViewVariables]
    public TimeSpan NextSourceSearch;

    [ViewVariables]
    public Dictionary<EntityUid, TimeSpan> IgnoredSources = new();
}

public enum NpcAirNeed : byte
{
    None,

    /// <summary>
    /// Down to the jetpack: refill once there's nothing to fight.
    /// </summary>
    Routine,

    /// <summary>
    /// Nothing left to breathe, in air it can't breathe.
    /// </summary>
    Urgent,
}
