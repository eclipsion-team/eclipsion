using Content.Shared.Audio;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// The Great Hunt: King of the Hill over a <see cref="GreatAltarComponent"/>. Whichever faction holds an altar
/// unbroken for <see cref="HoldTime"/> wins; the round then ends and restarts after <see cref="RestartDelay"/>.
/// Losing the altar to the other side resets the clock — the new holder starts from zero.
/// The hunt opens with a preparation phase of <see cref="GracePeriod"/>: every base in <see cref="BarrierStations"/>
/// is walled in and the altar cannot be claimed until it ends.
/// </summary>
[RegisterComponent]
public sealed partial class GreatHuntRuleComponent : Component
{
    /// <summary>Length of the preparation phase, counted from the moment the rule starts. 0 skips it.</summary>
    [DataField]
    public TimeSpan GracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>Delay after the rule starts before the preparation phase is announced, so players have spawned in.</summary>
    [DataField]
    public TimeSpan GraceStartAnnouncementDelay = TimeSpan.FromSeconds(15);

    /// <summary>Time left in the preparation phase at which a reminder goes out. Announced once each.</summary>
    [DataField]
    public List<TimeSpan> GraceWarnings = new() { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1) };

    /// <summary>Seconds of the final countdown, one announcement per second, right before the walls fall.</summary>
    [DataField]
    public int GraceCountdown = 10;

    [DataField]
    public string GraceStartAnnouncement = "great-hunt-grace-start";

    [DataField]
    public string GraceWarningAnnouncement = "great-hunt-grace-warning";

    [DataField]
    public string GraceCountdownAnnouncement = "great-hunt-grace-countdown";

    [DataField]
    public string GraceOverAnnouncement = "great-hunt-grace-over";

    /// <summary>Announcer sound IDs for the preparation phase; the same war sirens Unionfall's grace period uses.</summary>
    [DataField]
    public string GraceStartSound = "unionfallBegin";

    [DataField]
    public string GraceWarningSound = "unionfallPeriodic";

    /// <summary>Sound for the last <see cref="GraceWarnings"/> entry, the final minute before the walls fall.</summary>
    [DataField]
    public string GraceLastWarningSound = "unionfallAlmost";

    [DataField]
    public string GraceCountdownSound = "unionfallCountdown";

    [DataField]
    public string GraceOverSound = "unionfallGraceOver";

    /// <summary>
    /// Ambient music played to both sides for the whole preparation phase, over biome, ship and combat music; it is
    /// handed back to them when the walls fall. Null for none.
    /// </summary>
    [DataField]
    public ProtoId<AmbientMusicPrototype>? GraceMusic;

    /// <summary>Players who have been sent <see cref="GraceMusic"/>, so each gets it once and exactly they get it stopped.</summary>
    [ViewVariables]
    public HashSet<NetUserId> GraceMusicListeners = new();

    /// <summary>When <see cref="GraceMusicListeners"/> is next checked for players who joined or respawned.</summary>
    [ViewVariables]
    public TimeSpan NextGraceMusicCheck;

    /// <summary>
    /// <c>BecomesStation</c> IDs of the home bases walled in during the preparation phase. Each gets a square ring
    /// of <see cref="BarrierWall"/> <see cref="BarrierMargin"/> tiles out from its outer edge.
    /// </summary>
    [DataField]
    public List<string> BarrierStations = new();

    /// <summary>
    /// Open space between a home base's outer edge and its wall, in tiles. It has to hold every ship bought during
    /// preparation: a ship that finds no free dock is placed beside the base and its docked ships, and the wall must
    /// stay outside that reach so the ship lands inside it.
    /// </summary>
    [DataField]
    public int BarrierMargin = 160;

    /// <summary>The wall the ring is built from; the same indestructible wall as Unionfall's grace barrier.</summary>
    [DataField]
    public EntProtoId BarrierWall = "N14WallRoughScrapSlantedIndestructible";

    /// <summary>Tile laid under the wall.</summary>
    [DataField]
    public string BarrierTile = "FloorHullReinforced";

    [DataField]
    public string BarrierName = "great-hunt-barrier-name";

    [DataField]
    public Color BarrierColor = Color.Gray;

    /// <summary>Set once the rings have been put up, so they are only ever built once per hunt.</summary>
    [ViewVariables]
    public bool BarrierBuilt;

    /// <summary>Absolute time the preparation phase ends. VV to adjust; <c>greathunt_skipgrace</c> ends it now.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan GraceEndsAt;

    /// <summary>Set once the preparation phase is over and the walls are down.</summary>
    [ViewVariables]
    public bool GraceOver;

    [ViewVariables]
    public bool GraceStartAnnounced;

    /// <summary>How many of <see cref="GraceWarnings"/> have gone out, largest first.</summary>
    [ViewVariables]
    public int GraceWarningsGiven;

    /// <summary>The last countdown second announced, so each second is called exactly once.</summary>
    [ViewVariables]
    public int LastCountdownAnnounced = int.MaxValue;

    /// <summary>How long a faction has to hold the altar without losing it to win.</summary>
    [DataField]
    public TimeSpan HoldTime = TimeSpan.FromMinutes(10);

    /// <summary>Time between the victory being called and the round restarting.</summary>
    [DataField]
    public TimeSpan RestartDelay = TimeSpan.FromMinutes(1);

    /// <summary>Sector-wide warnings, as time left on the current holder's clock. Announced once per hold.</summary>
    [DataField]
    public List<TimeSpan> Warnings = new() { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1) };

    /// <summary>Victory announcement locale ID per faction.</summary>
    [DataField]
    public Dictionary<string, string> VictoryAnnouncements = new();

    /// <summary>Victory announcement for a faction without its own entry in <see cref="VictoryAnnouncements"/>.</summary>
    [DataField]
    public string FallbackVictoryAnnouncement = "great-hunt-victory-generic";

    [DataField]
    public string CaptureAnnouncement = "great-hunt-altar-captured";

    [DataField]
    public string WarningAnnouncement = "great-hunt-altar-warning";

    /// <summary>
    ///     Colour for anything that goes to everyone, such as the round end. Faction announcements use
    ///     <see cref="FactionColors"/>.
    /// </summary>
    [DataField]
    public Color AnnouncementColor = Color.Gold;

    /// <summary>
    ///     The sides of the hunt. Every announcement goes to each of them separately, written from that side's point of
    ///     view through the <c>$viewer</c> locale argument, and nobody else hears it.
    /// </summary>
    [DataField]
    public List<string> AnnouncedFactions = new() { "TAP", "SRM" };

    /// <summary>
    ///     Announcement colour per faction in <see cref="AnnouncedFactions"/>. The colour of the altar's fire for
    ///     each side.
    /// </summary>
    [DataField]
    public Dictionary<string, Color> FactionColors = new()
    {
        ["TAP"] = Color.FromHex("#9FED58"),
        ["SRM"] = Color.FromHex("#6FA8FF"),
    };

    /// <summary>Sender shown on the preparation phase announcements.</summary>
    [DataField]
    public string AnnouncementSender = "great-hunt-announcement-sender";

    /// <summary>Warnings already given for each altar's current hold. Reset whenever the altar changes hands.</summary>
    [ViewVariables]
    public Dictionary<EntityUid, int> WarningsGiven = new();

    [ViewVariables]
    public bool Decided;

    [ViewVariables]
    public string? Winner;
}
