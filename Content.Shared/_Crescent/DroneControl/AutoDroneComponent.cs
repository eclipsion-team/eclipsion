using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.DroneControl;

/// <summary>
///     Put on a drone's control server (the entity that steers the drone grid). Makes the drone
///     automatically undock from its carrier, hold an assigned formation slot behind it, and
///     engage diplomatic enemies within the carrier's engagement range.
///     Manual orders from a drone control console temporarily override this autopilot.
/// </summary>
[RegisterComponent]
public sealed partial class AutoDroneComponent : Component
{
    /// <summary>
    ///     The carrier console this drone has been deployed from, or null if not yet deployed.
    ///     The carrier grid is derived from this console's grid.
    /// </summary>
    [ViewVariables]
    public EntityUid? CarrierConsole;

    /// <summary>
    ///     Assigned formation slot index on the carrier, or -1 if unassigned.
    /// </summary>
    [ViewVariables]
    public int Slot = -1;

    /// <summary>
    ///     Formation slot target, expressed relative to the carrier grid so it rotates and moves
    ///     with the carrier. Resolved live by the steering system each tick.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates SlotCoordinates;

    /// <summary>
    ///     Current behavior mode, for debugging/visibility. Mirrored to the console UI.
    /// </summary>
    [ViewVariables]
    public AutoDroneMode Mode = AutoDroneMode.Idle;

    /// <summary>
    ///     True once the drone has cleared its carrier's hull after deployment. Until then it flies straight
    ///     out instead of trying to take up its formation slot. Latched, so a drone that has formed up never
    ///     drops back into the launch behaviour just because the carrier drifted onto it.
    /// </summary>
    [ViewVariables]
    public bool Launched;

    /// <summary>
    ///     True once we have cast the drone off its carrier. Undocking is a grid-wide lookup, so it is done
    ///     once at deployment (and retried while the drone is still attached) rather than every tick.
    /// </summary>
    [ViewVariables]
    public bool Undocked;

    /// <summary>
    ///     True if this drone was produced by its carrier console rather than claimed from a dock. Only
    ///     produced drones may have their ownership deed stripped and count against pending production.
    /// </summary>
    [ViewVariables]
    public bool Produced;

    /// <summary>
    ///     Hangar space this drone takes up on its carrier, see <see cref="DroneCarrierComponent.HangarCost"/>.
    ///     A drone claimed from a dock rather than produced always counts as 1.
    /// </summary>
    [ViewVariables]
    public int HangarCost = 1;

    /// <summary>
    ///     Set when the carrier wrote this drone off as disabled (unpowered too long, or hull shot away). A
    ///     written-off drone is never claimed by any carrier again.
    /// </summary>
    [ViewVariables]
    public bool WrittenOff;

    /// <summary>
    ///     When the drone last lost power while deployed, or null while it is powered.
    /// </summary>
    [ViewVariables]
    public TimeSpan? UnpoweredSince;

    // ---- condition readout ----

    /// <summary>
    ///     Fraction (0..1) of the drone grid's original tiles that are still intact. Recomputed on a slow
    ///     timer by <c>AutoDroneSystem</c> and shown on the console status panel.
    /// </summary>
    [ViewVariables]
    public float HullIntegrity = 1f;

    /// <summary>
    ///     Tile count of the drone grid when it was deployed, used as the denominator for
    ///     <see cref="HullIntegrity"/>. Zero until the drone is deployed.
    /// </summary>
    [ViewVariables]
    public int InitialTileCount;

    // ---- self destruct ----

    /// <summary>
    ///     When set, the drone scuttles itself at this time. Set by a console order, or automatically when
    ///     the drone is orphaned (its carrier console is destroyed) so dead squadrons don't litter the map.
    /// </summary>
    [ViewVariables]
    public TimeSpan? SelfDestructAt;

    /// <summary>
    ///     Countdown applied when a self destruct is ordered from the console.
    /// </summary>
    [DataField]
    public TimeSpan SelfDestructDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Countdown applied automatically when the drone loses its carrier console. Longer than the ordered
    ///     one so a carrier that is merely rebuilt/replaced has a chance to reclaim the squadron.
    /// </summary>
    [DataField]
    public TimeSpan OrphanSelfDestructDelay = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     Explosion prototype used when the drone scuttles itself.
    /// </summary>
    [DataField]
    public string SelfDestructExplosion = "Default";

    /// <summary>
    ///     Total intensity of the scuttling explosion. Sized to gut the drone without threatening a ship
    ///     holding station next to it.
    /// </summary>
    [DataField]
    public float SelfDestructIntensity = 60f;

    /// <summary>
    ///     Intensity falloff per tile of the scuttling explosion.
    /// </summary>
    [DataField]
    public float SelfDestructSlope = 8f;

    /// <summary>
    ///     Per-tile intensity cap of the scuttling explosion.
    /// </summary>
    [DataField]
    public float SelfDestructMaxTileIntensity = 12f;

    /// <summary>
    ///     How long after the scuttling charge goes off the wreck is removed. Long enough for the blast to
    ///     visibly tear the hull apart first, rather than the grid blinking out from under the explosion.
    /// </summary>
    [DataField]
    public TimeSpan ScuttleCleanupDelay = TimeSpan.FromSeconds(4);

    // ---- manual override ----

    /// <summary>
    ///     Until this time, the drone obeys the manual console order instead of the autopilot.
    /// </summary>
    [ViewVariables]
    public TimeSpan ManualOverrideUntil = TimeSpan.Zero;

    /// <summary>
    ///     The last manual command sent from a drone control console (drone_cmd_move / drone_cmd_target).
    /// </summary>
    [ViewVariables]
    public string? ManualCommand;

    /// <summary>
    ///     The target of the last manual command.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates ManualTarget;

    /// <summary>
    ///     How long a manual console order suspends the autopilot before it resumes.
    /// </summary>
    [DataField]
    public TimeSpan ManualOverrideTimeout = TimeSpan.FromSeconds(12);
}

[Serializable, NetSerializable]
public enum AutoDroneMode : byte
{
    Idle,
    Launching,
    Follow,
    Attack,
    Manual,
    SelfDestructing,
}
