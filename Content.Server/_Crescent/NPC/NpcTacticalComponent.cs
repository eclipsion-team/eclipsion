using Content.Shared.Dataset;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: makes a humanoid NPC fight like a soldier rather than a zombie - it looks for cover instead of
/// walking straight at its target, holds fire while a friendly is in the way, patches itself up when hurt,
/// and shouts about all of it.
/// </summary>
/// <remarks>
/// The behaviour itself lives in the FactionSoldierCompound HTN; this is the tuning and bookkeeping the
/// operators in there share. See <see cref="NpcTacticalSystem"/>.
/// </remarks>
[RegisterComponent, Access(typeof(NpcTacticalSystem))]
public sealed partial class NpcTacticalComponent : Component
{
    #region Cover

    /// <summary>
    /// How far from itself, in tiles, the NPC will look for a spot to fight from.
    /// </summary>
    [DataField]
    public int CoverSearchRadius = 7;

    /// <summary>
    /// How long a cover search result is trusted before the tiles are looked at again. The HTN replans every
    /// half second, and scanning the neighbourhood that often for every soldier would be a waste.
    /// </summary>
    [DataField]
    public TimeSpan CoverCacheTime = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// How long the NPC fights from one position before it reconsiders where it should be standing.
    /// </summary>
    [DataField]
    public TimeSpan MinRepositionTime = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan MaxRepositionTime = TimeSpan.FromSeconds(9);

    /// <summary>
    /// After deciding to reposition, how long the tiles around the old position count against a new pick.
    /// Without it the search would score the tile it is standing on best again and the NPC would not move.
    /// </summary>
    [DataField]
    public TimeSpan RepositionAvoidTime = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How strongly a spot is marked down for looking at the target from the same side as a squadmate.
    /// Spreads a group out around its target instead of stacking them up behind one corner.
    /// </summary>
    [DataField]
    public float FlankWeight = 1.5f;

    /// <summary>
    /// The position the NPC gave up on, while <see cref="RepositionAvoidUntil"/> lasts.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? RepositionFrom;

    [ViewVariables]
    public TimeSpan RepositionAvoidUntil;

    /// <summary>
    /// How long the target may stay out of sight, once the NPC has stopped moving, before it gives up on the
    /// position and finds another.
    /// </summary>
    [DataField]
    public TimeSpan LostSightTime = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// How long a friendly may block the line of fire before the NPC moves to get a clear shot.
    /// </summary>
    [DataField]
    public TimeSpan BlockedFireTime = TimeSpan.FromSeconds(1.5);

    [ViewVariables]
    public EntityUid? CachedCoverTarget;

    [ViewVariables]
    public EntityCoordinates? CachedCover;

    [ViewVariables]
    public TimeSpan CachedCoverTime;

    [ViewVariables]
    public EntityCoordinates? CachedHide;

    [ViewVariables]
    public TimeSpan CachedHideTime;

    /// <summary>
    /// The tile this NPC means to fight from, so squadmates don't all pile onto the same one.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? ClaimedCover;

    /// <summary>
    /// Since when a friendly has been standing in the line of fire, or null if the line is clear.
    /// </summary>
    [ViewVariables]
    public TimeSpan? LineOfFireBlockedSince;

    /// <summary>
    /// When the NPC should stop fighting from its current position and reconsider.
    /// </summary>
    [ViewVariables]
    public TimeSpan EngageUntil;

    /// <summary>
    /// Since when the target has been out of sight while the NPC stood still, or null if it hasn't.
    /// </summary>
    [ViewVariables]
    public TimeSpan? LostSightSince;

    #endregion

    #region Assault

    /// <summary>
    /// Under an attack order, how long the NPC stops at each spot on its way in before pushing on. Short:
    /// long enough to put some rounds out, not long enough to get pinned.
    /// </summary>
    [DataField]
    public TimeSpan AssaultMinHoldTime = TimeSpan.FromSeconds(1.5);

    [DataField]
    public TimeSpan AssaultMaxHoldTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How much closer to the target, in tiles, each bound of an assault has to get it.
    /// </summary>
    [DataField]
    public float AssaultStep = 2.5f;

    /// <summary>
    /// How far from itself, in tiles, the NPC looks for the next spot to push up to.
    /// </summary>
    [DataField]
    public int AssaultSearchRadius = 6;

    /// <summary>
    /// The closest an assault goes before it stops pushing and fights from where it is. The NPC's own
    /// <c>CombatRangeMin</c> is used when that is further, so a marksman still stops well short.
    /// </summary>
    [DataField]
    public float AssaultStopRange = 2f;

    [ViewVariables]
    public EntityUid? CachedFiringTarget;

    [ViewVariables]
    public EntityCoordinates? CachedFiring;

    [ViewVariables]
    public TimeSpan CachedFiringTime;

    [ViewVariables]
    public EntityUid? CachedAdvanceTarget;

    [ViewVariables]
    public EntityCoordinates? CachedAdvance;

    [ViewVariables]
    public TimeSpan CachedAdvanceTime;

    #endregion

    #region Peeking

    /// <summary>
    /// How often, while fighting from cover, the NPC steps out to a neighbouring tile to shoot from a
    /// slightly different angle before ducking back.
    /// </summary>
    [DataField]
    public TimeSpan MinPeekInterval = TimeSpan.FromSeconds(2.5);

    [DataField]
    public TimeSpan MaxPeekInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long it stays out on the neighbouring tile.
    /// </summary>
    [DataField]
    public TimeSpan PeekHoldTime = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// The tile the NPC is stepping out to, or null when it is staying on its cover.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? PeekTile;

    [ViewVariables]
    public TimeSpan PeekUntil;

    /// <summary>
    /// Whether the NPC leaned out because it couldn't see its target from cover, rather than on its timer.
    /// </summary>
    [ViewVariables]
    public bool PeekFromBlind;

    [ViewVariables]
    public TimeSpan NextPeek;

    /// <summary>
    /// Whether the fight in progress lets the NPC step out of cover at all.
    /// </summary>
    [ViewVariables]
    public bool PeekAllowed;

    #endregion

    #region Searching

    /// <summary>
    /// How long the NPC keeps a lead on where an enemy was - last seen, or shot from - before forgetting it.
    /// </summary>
    [DataField]
    public TimeSpan LeadMemory = TimeSpan.FromSeconds(25);

    /// <summary>
    /// For how long after losing sight of its target the NPC still keeps up with where it went: long enough
    /// to follow it round a corner, not long enough to track it through walls.
    /// </summary>
    [DataField]
    public TimeSpan LeadGraceTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How far away friendly soldiers hear this one call out an enemy and come to help.
    /// </summary>
    [DataField]
    public float AlertRadius = 12f;

    /// <summary>
    /// How long the NPC looks around once it reaches the spot it is searching.
    /// </summary>
    [DataField]
    public TimeSpan MinSearchTime = TimeSpan.FromSeconds(3);

    [DataField]
    public TimeSpan MaxSearchTime = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Where the NPC thinks an enemy is, or null when it has no idea.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? Lead;

    /// <summary>
    /// Who the lead is on, if the NPC knows. Once they're down there is nothing left to go looking for.
    /// </summary>
    [ViewVariables]
    public EntityUid? LeadTarget;

    /// <summary>
    /// When <see cref="Lead"/> was last updated.
    /// </summary>
    [ViewVariables]
    public TimeSpan LeadTime;

    /// <summary>
    /// When the NPC last actually saw its target.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastSawTarget;

    /// <summary>
    /// How many times the NPC has set off to search the current lead. A lead it can't reach is dropped after
    /// a few tries instead of being retried until it expires.
    /// </summary>
    [ViewVariables]
    public int SearchAttempts;

    #endregion

    #region Looking around

    /// <summary>
    /// When whatever the NPC is looking around for - idling, searching - is over.
    /// </summary>
    [ViewVariables]
    public TimeSpan LookAroundUntil;

    #endregion

    #region Healing

    /// <summary>
    /// Fraction of the critical threshold at which the NPC stops fighting to treat itself.
    /// </summary>
    [DataField]
    public float HealThreshold = 0.45f;

    /// <summary>
    /// Once it has started treating itself, it keeps at it until damage is back under this fraction.
    /// </summary>
    [DataField]
    public float HealStopThreshold = 0.2f;

    /// <summary>
    /// Pause after a failed or interrupted treatment before it tries again, so a soldier being shot while
    /// it bandages doesn't spend the whole fight starting and cancelling the same do-after.
    /// </summary>
    [DataField]
    public TimeSpan HealRetryDelay = TimeSpan.FromSeconds(6);

    [ViewVariables]
    public TimeSpan NextHealAttempt;

    /// <summary>
    /// Whether the NPC is currently in the middle of treating itself, which switches it over to
    /// <see cref="HealStopThreshold"/>.
    /// </summary>
    [ViewVariables]
    public bool Healing;

    #endregion

    #region Supplies

    /// <summary>
    /// Extra kit put into the NPC's worn storage on spawn, after its loadout: a medkit, building material,
    /// anything that doesn't depend on which gun it carries.
    /// </summary>
    [DataField]
    public List<EntProtoId> Supplies = new();

    /// <summary>
    /// Loaded into the injector slots of the NPC's hardsuit on spawn, in slot order. The hardsuit injects
    /// the first of them by itself when the wearer goes critical.
    /// </summary>
    [DataField]
    public List<EntProtoId> InjectorSupplies = new();

    /// <summary>
    /// Fraction of the critical threshold past which the NPC also reaches for a medipen, on itself or on
    /// its squad leader.
    /// </summary>
    [DataField]
    public float MedipenThreshold = 0.65f;

    /// <summary>
    /// Minimum gap between two medipens from the same NPC, so it doesn't overdose itself or its leader.
    /// </summary>
    [DataField]
    public TimeSpan MedipenCooldown = TimeSpan.FromSeconds(30);

    [ViewVariables]
    public TimeSpan NextMedipen;

    /// <summary>
    /// How hurt the squad leader has to be, as a fraction of their critical threshold, before a squadmate
    /// comes over to patch them up.
    /// </summary>
    [DataField]
    public float LeaderHealThreshold = 0.25f;

    #endregion

    #region Guarding

    /// <summary>
    /// How far from the leader the rest of the squad stands while one of them patches the leader up.
    /// </summary>
    [DataField]
    public float GuardRadius = 2f;

    /// <summary>
    /// How far either side of straight out from the leader a guard will look.
    /// </summary>
    [DataField]
    public Angle GuardLookSpread = Angle.FromDegrees(80);

    /// <summary>
    /// How long a guard watches one direction before looking somewhere else.
    /// </summary>
    [DataField]
    public TimeSpan MinGuardLookTime = TimeSpan.FromSeconds(1.5);

    [DataField]
    public TimeSpan MaxGuardLookTime = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// How fast a guard turns its head, in radians per second. Slow enough to read as scanning the area.
    /// </summary>
    [DataField]
    public float GuardTurnSpeed = 4f;

    [ViewVariables]
    public Angle GuardLookAngle;

    [ViewVariables]
    public TimeSpan NextGuardLook;

    #endregion

    #region Barricades

    /// <summary>
    /// What the NPC builds when it digs in.
    /// </summary>
    [DataField]
    public EntProtoId BarricadePrototype = "CrescentBarricadeMetal";

    /// <summary>
    /// The material it builds it from, and how much of it one takes. Matches the player recipe.
    /// </summary>
    [DataField]
    public string BarricadeStackType = "Steel";

    [DataField]
    public int BarricadeCost = 5;

    [DataField]
    public TimeSpan BarricadeBuildTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum gap between two barricades from the same NPC.
    /// </summary>
    [DataField]
    public TimeSpan BarricadeCooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How hurt the squad leader has to be before the squad fortifies around them.
    /// </summary>
    [DataField]
    public float LeaderFortifyThreshold = 0.4f;

    [ViewVariables]
    public TimeSpan NextBarricade;

    /// <summary>
    /// Where the barricade the NPC is working on goes, and which way it faces. Also serves as a claim so
    /// two squadmates don't build on the same tile.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? PendingBarricade;

    [ViewVariables]
    public Angle PendingBarricadeRotation;

    /// <summary>
    /// Set once the pending barricade has actually gone up.
    /// </summary>
    [ViewVariables]
    public bool BarricadeFinished;

    #endregion

    #region Callouts

    /// <summary>
    /// What the NPC shouts, per occasion. Each entry is a localized dataset of lines to pick from. Anything
    /// not listed falls back to the datasets for the NPC's faction, see <see cref="NpcTacticalSystem"/>.
    /// </summary>
    [DataField]
    public Dictionary<NpcCalloutType, ProtoId<LocalizedDatasetPrototype>> Callouts = new();

    /// <summary>
    /// Minimum gap between two callouts from the same NPC.
    /// </summary>
    [DataField]
    public TimeSpan CalloutCooldown = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Chance per occasion that the NPC says something at all, so a squad doesn't bark in unison.
    /// </summary>
    [DataField]
    public float CalloutChance = 0.6f;

    /// <summary>
    /// Average gap between battle cries while the NPC is fighting.
    /// </summary>
    [DataField]
    public TimeSpan BattleCryInterval = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Average gap between idle remarks while it is not.
    /// </summary>
    [DataField]
    public TimeSpan IdleChatterInterval = TimeSpan.FromSeconds(150);

    [ViewVariables]
    public TimeSpan NextCallout;

    [ViewVariables]
    public TimeSpan NextBattleCry;

    [ViewVariables]
    public TimeSpan NextIdleChatter;

    [ViewVariables]
    public bool WasInCombat;

    #endregion
}

public enum NpcCalloutType : byte
{
    /// <summary>
    /// Spotting an enemy and opening fire.
    /// </summary>
    Engage,

    /// <summary>
    /// Every so often while a fight goes on.
    /// </summary>
    BattleCry,

    Reload,
    Heal,

    /// <summary>
    /// Acknowledging an order from the squad leader.
    /// </summary>
    Acknowledge,

    /// <summary>
    /// Joining a player's squad.
    /// </summary>
    Recruited,

    /// <summary>
    /// Being released from one.
    /// </summary>
    Dismissed,

    /// <summary>
    /// Nothing going on.
    /// </summary>
    Idle,

    /// <summary>
    /// Digging in behind a barricade.
    /// </summary>
    Fortify,

    /// <summary>
    /// Running over to patch up the squad leader.
    /// </summary>
    Medic,

    /// <summary>
    /// Standing watch while a squadmate patches the leader up.
    /// </summary>
    Guard,

    /// <summary>
    /// Going to check out where an enemy was last seen or shot from.
    /// </summary>
    Search,

    /// <summary>
    /// Pushing forward under an attack order.
    /// </summary>
    Advance,
}
