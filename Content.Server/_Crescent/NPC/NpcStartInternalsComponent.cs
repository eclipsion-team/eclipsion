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
/// low the NPC moves over to the fullest other tank it carries.
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
}
