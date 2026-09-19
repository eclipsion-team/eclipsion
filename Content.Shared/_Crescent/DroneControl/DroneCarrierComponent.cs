using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.DroneControl;

/// <summary>
///     Marks an entity (a drone control console) as a drone carrier. Any <see cref="AutoDroneComponent"/>
///     drone docked to this console's grid is automatically deployed into formation and directed to engage
///     diplomatic enemies (War relation) that come within <see cref="EngagementRange"/>.
/// </summary>
[RegisterComponent]
public sealed partial class DroneCarrierComponent : Component
{
    /// <summary>
    ///     Maximum number of drones deployed at once. Also defines the formation slot layout size.
    /// </summary>
    [DataField]
    public int MaxDrones = 4;

    /// <summary>
    ///     Combat stance the drones adopt. Selected by the player from the console verbs.
    ///     Attack engages any enemy within <see cref="EngagementRange"/>; Defend only engages threats that
    ///     come within <see cref="DefendRange"/> of the carrier; Follow holds formation and never fires.
    /// </summary>
    [DataField]
    public DroneStance Stance = DroneStance.Attack;

    /// <summary>
    ///     Which ships the drones treat as targets. Enemies = only ships at War; All = every ship in range
    ///     except friendly (allied/same-faction) ones.
    /// </summary>
    [DataField]
    public DroneTargeting Targeting = DroneTargeting.Enemies;

    /// <summary>
    ///     Formation pattern the drones hold behind the carrier. Cycled at runtime via the console verb.
    /// </summary>
    [DataField]
    public DroneFormation Formation = DroneFormation.Arrow;

    /// <summary>
    ///     Lateral spacing between formation slots, in meters.
    /// </summary>
    [DataField]
    public float FormationSpacing = 26f;

    /// <summary>
    ///     Longitudinal spacing behind the carrier between formation rows, in meters (measured from the
    ///     carrier's rear edge). Kept large so slots sit well clear of the hull.
    /// </summary>
    [DataField]
    public float FormationDepth = 45f;

    /// <summary>
    ///     Extra clearance (meters) added to the carrier's hull radius. A freshly deployed drone first flies
    ///     straight out past this before forming up, so it never grinds the hull on the way out.
    ///     The hull radius itself is measured from the carrier's AABB, so this is pure margin.
    /// </summary>
    [DataField]
    public float LaunchClearance = 25f;

    /// <summary>
    ///     How long a produced drone neither deals nor receives shuttle impact damage after spawning.
    /// </summary>
    [DataField]
    public TimeSpan SpawnCollisionProtectionDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How close (meters) a drone must get to its slot to be considered holding formation.
    /// </summary>
    [DataField]
    public float FormationRange = 8f;

    /// <summary>
    ///     Enemies within this range (meters) of the carrier get engaged in the Attack stance. Drones return
    ///     to formation once no enemy remains in range (carrier-leashed engagement).
    /// </summary>
    [DataField]
    public float EngagementRange = 2000f;

    /// <summary>
    ///     Engagement range (meters) used in the Defend stance: only threats that get this close to the
    ///     carrier are engaged, keeping the drones tight to the carrier.
    /// </summary>
    [DataField]
    public float DefendRange = 600f;

    /// <summary>
    ///     Orbit distance (meters) drones keep from an engaged enemy while firing.
    /// </summary>
    [DataField]
    public float OrbitRange = 250f;

    /// <summary>
    ///     Extra standoff (meters) added per formation slot while attacking, so the squadron spreads into a
    ///     staggered ring instead of every drone flying the exact same circle.
    /// </summary>
    [DataField]
    public float OrbitSpread = 25f;

    /// <summary>
    ///     How far (meters) a drone may drift off its orbit radius before it corrects. Wide enough that the
    ///     drone keeps its momentum through the turn instead of constantly braking.
    /// </summary>
    [DataField]
    public float OrbitTolerance = 40f;

    /// <summary>
    ///     How far around the circle a drone aims while orbiting. This is what drives the orbit at all - the
    ///     steering aims at a point this many degrees ahead of the drone's own projection onto the orbit
    ///     radius, so at zero the drone would just hold its radius and never come around. Larger values make
    ///     it cut the circle more aggressively.
    /// </summary>
    [DataField]
    public Angle OrbitLeadAngle = Angle.FromDegrees(30f);

    /// <summary>
    ///     Whether drones scuttle themselves when this console is destroyed. Without it a dead carrier leaves
    ///     a squadron of unrecoverable derelicts drifting on the map forever.
    /// </summary>
    [DataField]
    public bool SelfDestructOnOrphan = true;

    /// <summary>
    ///     Whether producing a drone bills the owning faction's treasury for the vessel's price. Off by default:
    ///     the drones are part of the carrier's own material cost, and lost ones are paid for at a repair
    ///     station restock instead.
    /// </summary>
    [DataField]
    public bool ChargeTreasury;

    /// <summary>
    ///     Multiplier applied to the vessel's shipyard price when billing the treasury for a produced drone.
    /// </summary>
    [DataField]
    public float PriceMultiplier = 1f;

    /// <summary>
    ///     Currently deployed drones (their control-server uids) keyed by formation slot index.
    /// </summary>
    [ViewVariables]
    public Dictionary<int, EntityUid> Slots = new();

    /// <summary>
    ///     Drones taken out of the hangar so far. A destroyed or written-off drone does NOT free up capacity -
    ///     once <see cref="MaxDrones"/> have been produced the hangar is empty. Only a repair station restock
    ///     brings it back down to the number of drones still alive.
    /// </summary>
    [ViewVariables]
    public int ProducedCount;

    /// <summary>
    ///     How long a drone may sit unpowered before the carrier writes it off. A written-off drone is dropped
    ///     from the console and can never be reclaimed.
    /// </summary>
    [DataField]
    public TimeSpan DisabledTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Hull integrity (0..1) below which a drone is written off as unusable.
    /// </summary>
    [DataField]
    public float DisabledHullIntegrity = 0.25f;

    /// <summary>
    ///     What a repair station bills to restock the hangar after losing one drone. Scales linearly up to
    ///     <see cref="RestockCostMax"/> for losing the whole squadron.
    /// </summary>
    [DataField]
    public int RestockCostMin = 15000;

    /// <inheritdoc cref="RestockCostMin"/>
    [DataField]
    public int RestockCostMax = 25000;

    /// <summary>
    ///     Vessel prototype IDs this console can produce, shown for selection in the UI. Set per faction
    ///     (e.g. DSM console: ValorDrone / RocinanteDrone).
    /// </summary>
    [DataField]
    public List<string> SpawnableDrones = new();

    /// <summary>
    ///     Radar/IFF colour applied to produced drones so they show in the faction's colour instead of the
    ///     default gold a bare grid gets.
    /// </summary>
    [DataField]
    public Color IffColor = Color.Gold;

    /// <summary>
    ///     Drones produced but not yet claimed into a slot, used to gate production against the limit while a
    ///     fresh drone is still FTL-docking. The grid is recorded so the eventual claim can be matched back to
    ///     the hull we actually built, rather than crediting whichever drone happens to dock next. Entries
    ///     expire on their own. Runtime state.
    /// </summary>
    [ViewVariables]
    public List<(TimeSpan Time, EntityUid Grid)> PendingSpawns = new();

    /// <summary>
    ///     Shared focus target all this carrier's drones concentrate fire on. Re-selected when the current
    ///     one dies, powers down, or leaves range; drones only switch targets once it is gone (focus fire).
    /// </summary>
    [ViewVariables]
    public EntityUid? FocusTarget;
}

[Serializable, NetSerializable]
public enum DroneTargeting : byte
{
    /// <summary>Only ships at War (diplomatic enemies).</summary>
    Enemies,

    /// <summary>Every ship in range except friendly (allied/same-faction) ones.</summary>
    All,
}

[Serializable, NetSerializable]
public enum DroneStance : byte
{
    /// <summary>Engage any War enemy within EngagementRange.</summary>
    Attack,

    /// <summary>Hold formation, only engage threats that come within DefendRange of the carrier.</summary>
    Defend,

    /// <summary>Hold formation and never fire; purely follow the carrier.</summary>
    Follow,
}

[Serializable, NetSerializable]
public enum DroneFormation : byte
{
    /// <summary>Trailing V behind the carrier ("ok"): pairs off the rear-left and rear-right.</summary>
    Arrow,

    /// <summary>Spread out abreast directly behind the carrier, side by side.</summary>
    LineAbreast,

    /// <summary>Single file directly astern.</summary>
    Column,

    /// <summary>Diagonal staircase off one side.</summary>
    Echelon,

    /// <summary>Box arranged around a point behind the carrier.</summary>
    Diamond,
}
