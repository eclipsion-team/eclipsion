using System.Collections.Generic;
using Content.Shared._Crescent.Diplomacy;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.IntruderTeleporter;

/// <summary>
/// A boarding console. Crew sign themselves onto a squad roster, somebody carrying the right access picks a
/// hostile vessel off the console's radar, and after a telegraphed delay the roster is thrown onto random
/// tiles of that vessel. The trip is one-way; the console has no way to pull anybody back.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class IntruderTeleporterComponent : Component
{
    /// <summary>
    /// How far out a vessel may be and still be boardable, in metres. Also the radius the console's own
    /// scanner is drawn at.
    /// </summary>
    [DataField]
    public float Range = 200f;

    /// <summary>
    /// How many people may be on the roster at once.
    /// </summary>
    [DataField]
    public int MaxSquadSize = 4;

    /// <summary>
    /// Seconds between the launch order and the squad actually leaving. The target is warned at the start of
    /// this window, which is the entire point of it existing.
    /// </summary>
    [DataField]
    public float WarningDelay = 30f;

    /// <summary>
    /// Seconds before arrival that the target gets its second, louder warning. Set above
    /// <see cref="WarningDelay"/> to skip it.
    /// </summary>
    [DataField]
    public float FinalWarningDelay = 10f;

    /// <summary>
    /// Seconds the console is unusable for after a launch order. Timed from the order, not the arrival, so an
    /// aborted run still costs the cooldown.
    /// </summary>
    [DataField]
    public float Cooldown = 600f;

    /// <summary>
    /// Whether the warning sent to the target names the vessel the boarders are coming from. Off makes the
    /// boarding party much harder to answer, so it is on by default.
    /// </summary>
    [DataField]
    public bool RevealSourceVessel = true;

    /// <summary>
    /// Which diplomatic relations count as a valid boarding target, judged from the console's own grid.
    /// Anything outside this set cannot be selected.
    /// </summary>
    [DataField]
    public HashSet<Relations> HostileRelations = new() { Relations.War, Relations.ColdWar };

    /// <summary>
    /// Tiles sampled when looking for a drop point on the target. Each boarder gets its own search.
    /// </summary>
    [DataField]
    public int DropSearchAttempts = 96;

    /// <summary>
    /// Whether a drop tile has to hold breathable air. Turning this off lets the squad land in a hull breach
    /// or an unpressurised hangar.
    /// </summary>
    [DataField]
    public bool RequireBreathableDrop = true;

    /// <summary>
    /// Played on the target grid when the boarding order goes out.
    /// </summary>
    [DataField]
    public SoundSpecifier? WarningSound = new SoundPathSpecifier("/Audio/Machines/vessel_warning.ogg");

    /// <summary>
    /// Played on the target grid when the squad lands.
    /// </summary>
    [DataField]
    public SoundSpecifier? ArrivalSound = new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg");

    /// <summary>
    /// Played at the console as the squad leaves.
    /// </summary>
    [DataField]
    public SoundSpecifier? DepartureSound = new SoundPathSpecifier("/Audio/Effects/teleport_departure.ogg");

    /// <summary>
    /// Who is signed up, in the order they signed up. Runtime state: a roster is meaningless across a round.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> Squad = new();

    /// <summary>
    /// The grid the console is currently aimed at. Only a pick - nothing happens until somebody with access
    /// gives the launch order.
    /// </summary>
    [ViewVariables]
    public EntityUid? SelectedTarget;

    /// <summary>
    /// The grid an in-flight launch is aimed at, snapshotted at the launch order so that re-aiming the
    /// scanner mid-countdown cannot redirect a squad the target has already been warned about.
    /// </summary>
    [ViewVariables]
    public EntityUid? LaunchTarget;

    /// <summary>
    /// The roster as it was when the launch order was given. Joining after the order does not get you a seat.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> LaunchSquad = new();

    /// <summary>
    /// When the in-flight launch lands, or null when no launch is in flight.
    /// </summary>
    [ViewVariables]
    public TimeSpan? LaunchAt;

    /// <summary>
    /// Set once the in-flight launch has sent its second warning.
    /// </summary>
    [ViewVariables]
    public bool FinalWarningSent;

    /// <summary>
    /// When the console becomes usable again, or null when it already is.
    /// </summary>
    [ViewVariables]
    public TimeSpan? CooldownEnd;
}
