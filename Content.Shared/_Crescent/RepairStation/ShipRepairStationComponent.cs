using System.Numerics;
using Content.Shared.Decals;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._Crescent.RepairStation;

/// <summary>
/// Console that reconstructs a docked ship from the structural snapshot the ship carries
/// (<c>ShipRepairDataComponent</c>), billing whoever authorises the job slightly above the market
/// value of everything it puts back. Work is done part by part rather than instantly.
/// </summary>
[RegisterComponent]
public sealed partial class ShipRepairStationComponent : Component
{
    // ---------------------------------------------------------------------------------------------
    // Override slip. Every one of these is off on a slip the crew can walk up to, and the set of them
    // is what turns the machine into an administrative tool rather than a service the round pays for.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Restricts the whole console - opening it, picking a hull, authorising work, filing a blueprint -
    /// to a player holding the admin flag. Anyone else is refused at the door.
    /// </summary>
    /// <remarks>
    /// Checked again on every message rather than only on the window opening, since a bound interface
    /// message is a packet a client sends and not a button the server watched being pressed.
    /// </remarks>
    [DataField]
    public bool AdminOnly;

    /// <summary>
    /// Does the work for nothing. Whoever authorises the job is not billed, nothing is refunded when
    /// one is cut short, and the console does not care whether he has an account at all - which an
    /// admin looking out of a ghost does not.
    /// </summary>
    [DataField]
    public bool Free;

    /// <summary>
    /// Lets the console file a fresh structural blueprint of the hull in front of it, which is the only
    /// way a hull that was never bought from a shipyard - a station, a mapped-in fleet ship - ever gets
    /// one. What is on the deck at that moment becomes what the slip restores it to.
    /// </summary>
    /// <remarks>
    /// Deliberately not something a crew slip can do: a customer who could re-file the blueprint of a
    /// hull he had just stripped would be writing off his own repair bill.
    /// </remarks>
    [DataField]
    public bool AllowSnapshot;

    /// <summary>
    /// Lists the grid the console is standing on alongside whatever is in the clamps, so an override
    /// slip dropped straight onto a station repairs that station without anything having to dock.
    /// </summary>
    [DataField]
    public bool ServiceOwnGrid;

    /// <summary>
    /// Works on a hull with no blueprint on file at all. Nothing missing can be put back - there is no
    /// record of what was there - but the damage is still beaten out of what is standing, the spills
    /// are still mopped, the wreckage is still binned and the magazines are still filled.
    /// </summary>
    [DataField]
    public bool RepairUnregistered;

    /// <summary>
    /// What the yard charges over the raw material value of the parts it reinstates.
    /// 1.15 = the customer pays 15% above what the missing hull is worth.
    /// </summary>
    [DataField]
    public float PriceMarkup = 1.15f;

    /// <summary>
    /// Credits charged for a plating tile whose drop prototype cannot be priced.
    /// </summary>
    [DataField]
    public int FallbackTilePrice = 25;

    /// <summary>
    /// Seconds of work per reinstated part. The whole job is squeezed into
    /// <see cref="MaxRepairSeconds"/> by welding several parts per tick when a wreck is big enough
    /// that one-at-a-time would take all round.
    /// </summary>
    [DataField]
    public float SecondsPerPart = 0.4f;

    /// <summary>
    /// Floor on how long a repair takes, so a one-tile scratch still spends time in the slip.
    /// </summary>
    [DataField]
    public float MinRepairSeconds = 8f;

    /// <summary>
    /// Ceiling on how long a repair takes, however wrecked the hull is.
    /// </summary>
    [DataField]
    public float MaxRepairSeconds = 420f;

    /// <summary>
    /// Credits charged per point of damage beaten out of a structure that is still standing.
    /// </summary>
    [DataField]
    public float DamageRepairRate = 0.6f;

    /// <summary>
    /// Credits charged for mopping up one spill.
    /// </summary>
    [DataField]
    public int SpillCleaningFee = 12;

    /// <summary>
    /// Credits charged per round of ammunition whose prototype cannot be priced, and per unit of fuel
    /// whose reagent cannot be. Almost no fuel carries a market price, and a slip that read that as
    /// nothing would hand out full tanks and full magazines for free.
    /// </summary>
    [DataField]
    public int FallbackRoundPrice = 5;

    /// <inheritdoc cref="FallbackRoundPrice"/>
    [DataField]
    public float FallbackReagentPrice = 0.25f;

    /// <summary>
    /// Credits charged for repainting one deck marking the hull lost.
    /// </summary>
    [DataField]
    public int DecalPaintFee = 6;

    /// <summary>
    /// Credits charged for binning one piece of loose wreckage - a sheet off a broken wall, a shard
    /// off a window, the coil a cut cable left behind.
    /// </summary>
    [DataField]
    public int DebrisClearingFee = 5;

    /// <summary>
    /// How long after the last weld the yard waits before making the air good. Freshly laid plating only
    /// joins the grid's atmosphere on the next revalidation pass, so filling it any sooner misses exactly
    /// the compartments that were rebuilt.
    /// </summary>
    [DataField]
    public float AtmosSettleSeconds = 2f;

    /// <summary>
    /// RCD-style effect spawned on each tile as it is worked on.
    /// </summary>
    [DataField]
    public EntProtoId ConstructEffect = "EffectRCDConstruct1";

    [DataField]
    public SoundSpecifier? RepairSound = new SoundPathSpecifier("/Audio/Items/deconstruct.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");

    [DataField]
    public SoundSpecifier? ConfirmSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    /// <summary>
    /// Grid currently in the slip, if a job is running.
    /// </summary>
    [ViewVariables]
    public EntityUid? Target;

    /// <summary>
    /// Outstanding work, popped from the back.
    /// </summary>
    [ViewVariables]
    public List<ShipRepairJob> Jobs = new();

    [ViewVariables]
    public int JobsTotal;

    [ViewVariables]
    public int JobsDone;

    /// <summary>
    /// Parts welded per tick, raised above 1 only when <see cref="MaxRepairSeconds"/> demands it.
    /// </summary>
    [ViewVariables]
    public int PartsPerTick = 1;

    [ViewVariables]
    public TimeSpan NextTickTime;

    [ViewVariables]
    public TimeSpan StartTime;

    [ViewVariables]
    public TimeSpan EndTime;

    /// <summary>
    /// Interval between job ticks, derived from <see cref="SecondsPerPart"/> at job start.
    /// </summary>
    [ViewVariables]
    public TimeSpan TickInterval = TimeSpan.FromSeconds(0.4);

    /// <summary>
    /// Credits taken up front, refunded pro rata if the job is cut short.
    /// </summary>
    [ViewVariables]
    public int AmountPaid;

    /// <summary>
    /// Who to refund if the job is cut short.
    /// </summary>
    [ViewVariables]
    public EntityUid? Payer;

    [ViewVariables]
    public TimeSpan NextUiUpdate;

    /// <summary>
    /// Where filling starts once the job in hand is done: inside the holes the yard welded shut, and to
    /// either side of the walls, windows and doors it put back. Filling stops at the first thing that
    /// holds air, and passes over anything still holding a breathable pressure, so the rest of the ship
    /// - a fire someone is fighting, a hold vented on purpose, a plasma leak - is left alone.
    /// </summary>
    [ViewVariables]
    public HashSet<Vector2i> RefillSeeds = new();

    /// <summary>
    /// Hull waiting to have its air made good, once <see cref="AtmosRefreshTime"/> comes round, and the
    /// tiles to start filling from.
    /// </summary>
    [ViewVariables]
    public EntityUid? AtmosTarget;

    [ViewVariables]
    public HashSet<Vector2i> AtmosSeeds = new();

    [ViewVariables]
    public TimeSpan AtmosRefreshTime;
}

/// <summary>
/// What one unit of work does.
/// </summary>
public enum ShipRepairJobKind : byte
{
    /// <summary>Lay a plating tile back down.</summary>
    Tile,

    /// <summary>Rebuild a structure off the hand device's snapshot, keeping that snapshot in step.</summary>
    ToolPart,

    /// <summary>Rebuild a structure off the slip's own file.</summary>
    DrydockPart,

    /// <summary>Beat the damage out of a structure that is still standing.</summary>
    Heal,

    /// <summary>Repaint a deck marking that went with the plating it was laid on.</summary>
    Decal,

    /// <summary>Mop up a spill.</summary>
    Clean,

    /// <summary>Bin a piece of loose wreckage the hull shed.</summary>
    Sweep,

    /// <summary>Put ammunition or fuel back into something that is running short.</summary>
    Restock,
}

/// <summary>
/// One unit of work on the tile at <see cref="Indices"/>.
/// </summary>
public struct ShipRepairJob
{
    public ShipRepairJobKind Kind;

    public Vector2i Indices;

    /// <summary>
    /// Entity id within the chunk of the hand-held device's snapshot, for <see cref="ShipRepairJobKind.ToolPart"/>.
    /// </summary>
    public int? SpecId;

    /// <summary>
    /// A structure only the slip tracks, for <see cref="ShipRepairJobKind.DrydockPart"/>.
    /// </summary>
    public DrydockPart? Part;

    /// <summary>
    /// The marking to lay back down, for <see cref="ShipRepairJobKind.Decal"/>.
    /// </summary>
    public Decal? Decal;

    /// <summary>
    /// The thing being worked on, for jobs that act on something already aboard.
    /// </summary>
    public EntityUid Target;

    /// <summary>
    /// For <see cref="ShipRepairJobKind.Tile"/>, whether the tile was open to space rather than merely
    /// beaten down to the layer underneath. Only a hull the yard actually sealed gets its air back.
    /// </summary>
    public bool Breach;

    /// <summary>
    /// What this one piece of work was quoted at, before the yard's markup. Kept so a job cut short can
    /// be refunded on what is left undone rather than on how many entries are left in the list - a gun
    /// and a mopped puddle are one entry each and are not worth the same money.
    /// </summary>
    public int Cost;
}
