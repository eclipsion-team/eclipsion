using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.ShipShields;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.RoundEnd;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._Crescent.SpaceArtillery;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Timing;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server._Crescent.RoundEnd;

/// <summary>
/// The conquest win condition. A faction is counted as holding the sector for as long as a station of its own is
/// standing — a station falls when its grid is gone, or when every <see cref="ConquestFlagComponent"/> planted on
/// it has been in enemy hands for <see cref="FactionConquestRuleComponent.HoldToFall"/>. Losing the last banner is
/// announced when the clock starts, and each station broadcasts its own obituary once the clock runs out. A station
/// with no banners can only fall by being destroyed — the banners are placed by hand per map.
///
/// This is pure scorekeeping: nobody is ever taken out of the war, and diplomacy is never touched. DSM and NCWL
/// are at war by definition and stay that way whatever happens to their hulls — losing Aurora or Balreska only
/// means that side no longer holds a seat of power, not that it has bowed out of anything.
///
/// Who wins:
///  * exactly one great power left standing — that power takes it, whoever else is still holding a station;
///  * both great powers dead — the minors do NOT have to finish each other off, every survivor shares the sector;
///  * both great powers still alive — nothing is settled, the war continues.
///
/// A settled war does not end the round straight away: <see cref="FactionConquestRuleComponent.VictoryDelay"/>
/// gives whoever is left one last window to move. The ten-minute capture warning is the defenders' chance to
/// reclaim their banners; once the Turning infestation begins, that station's fall is irreversible.
/// If the round instead runs out of time, the surviving great power is credited; failing that, Taypan is swallowed.
/// </summary>
public sealed class FactionConquestRuleSystem : GameRuleSystem<FactionConquestRuleComponent>
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ShipShieldsSystem _shipShields = default!;

    protected override void Started(EntityUid uid, FactionConquestRuleComponent component, GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        component.NextCheck = _timing.CurTime + component.CheckInterval;
    }

    /// <summary>
    /// The round ended without the conquest resolving — almost always the 4h cap. A great power still standing
    /// takes the sector by default; if none is, nobody won and the Abyss takes it.
    /// </summary>
    protected override void AppendRoundEndText(EntityUid uid, FactionConquestRuleComponent conquest,
        GameRuleComponent gameRule, ref RoundEndTextAppendEvent args)
    {
        base.AppendRoundEndText(uid, conquest, gameRule, ref args);

        if (!GameTicker.IsGameRuleActive(uid, gameRule))
            return;

        // The war was already called mid-round; just restate it on the summary screen.
        if (conquest.Decided)
        {
            AppendSummary(conquest, ref args);
            return;
        }

        conquest.Decided = true;

        // A round that hit the cap before the map ever came up has no war to credit anyone for.
        if (!TryResolveMajors(conquest))
        {
            _chat.DispatchServerAnnouncement(Loc.GetString(conquest.TimeoutAnnouncement));
            AppendSummary(conquest, ref args);
            return;
        }

        var alive = GetSurvivingFactions(conquest);
        var majors = alive.Where(conquest.ActiveMajors!.Contains).ToList();

        if (majors.Count == 1 && conquest.VictoryAnnouncements.TryGetValue(majors[0], out var victory))
        {
            conquest.Winners = majors;
            _chat.DispatchServerAnnouncement(Loc.GetString(victory));
        }
        else
        {
            _chat.DispatchServerAnnouncement(Loc.GetString(conquest.TimeoutAnnouncement));
        }

        AppendSummary(conquest, ref args);
    }

    /// <summary>Who took Taypan and which seats of power were left standing, for the round end screen.</summary>
    private void AppendSummary(FactionConquestRuleComponent conquest, ref RoundEndTextAppendEvent args)
    {
        args.AddLine(conquest.Winners.Count > 0
            ? Loc.GetString("faction-conquest-summary-winner",
                ("factions", string.Join(", ", conquest.Winners.Select(FactionDisplay.Abbreviation))))
            : Loc.GetString("faction-conquest-summary-nobody"));

        var query = EntityQueryEnumerator<FactionStationComponent>();
        while (query.MoveNext(out var stationUid, out var station))
        {
            args.AddLine(Loc.GetString(
                conquest.AnnouncedFallen.Contains(stationUid)
                    ? "faction-conquest-summary-station-fallen"
                    : "faction-conquest-summary-station-standing",
                ("station", station.StationName),
                ("faction", FactionDisplay.Abbreviation(station.Faction))));
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<FactionConquestRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var conquest, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule) || conquest.Decided)
                continue;

            if (_timing.CurTime < conquest.NextCheck)
                continue;

            conquest.NextCheck = _timing.CurTime + conquest.CheckInterval;

            // The map may not be up yet, and nothing can be judged against an empty sector.
            if (!TryResolveMajors(conquest))
                continue;

            Evaluate(conquest);
        }
    }

    private void Evaluate(FactionConquestRuleComponent conquest)
    {
        var alive = GetSurvivingFactions(conquest);

        // Nobody holds anything — there is no victor to crown.
        if (alive.Count == 0)
        {
            CancelPending(conquest);
            return;
        }

        if (!TryResolveWinners(conquest, alive, out var winners))
        {
            // The surviving-station or alliance state changed before declaration — call off the pending victory.
            CancelPending(conquest);
            return;
        }

        // First time this looks settled: start the response window rather than ending immediately.
        if (conquest.PendingWinners == null || !conquest.PendingWinners.SequenceEqual(winners))
        {
            conquest.PendingWinners = winners;
            conquest.PendingSince = _timing.CurTime;

            // Tell the sector, or the response window is a silent countdown nobody can respond to.
            _chat.DispatchServerAnnouncement(Loc.GetString(conquest.PendingAnnouncement,
                ("factions", string.Join(", ", winners.Select(FactionDisplay.Abbreviation))),
                ("minutes", (int) conquest.VictoryDelay.TotalMinutes)));
            return;
        }

        if (_timing.CurTime - conquest.PendingSince < conquest.VictoryDelay)
            return;

        Declare(conquest, winners);
    }

    /// <summary>Drops a pending victory, telling the sector about it if one was actually running.</summary>
    private void CancelPending(FactionConquestRuleComponent conquest)
    {
        if (conquest.PendingWinners == null)
            return;

        conquest.PendingWinners = null;
        _chat.DispatchServerAnnouncement(Loc.GetString(conquest.PendingCancelledAnnouncement));
    }

    /// <summary>
    /// Returns the faction identifiers accepted by the emergency admin victory command for the active conquest
    /// rule. Multiple active rules are tolerated here so the command can still recover a misconfigured round.
    /// </summary>
    public List<string> GetForceableFactions()
    {
        var factions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var query = EntityQueryEnumerator<FactionConquestRuleComponent, GameRuleComponent>();

        while (query.MoveNext(out var uid, out var conquest, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            factions.UnionWith(conquest.VictoryAnnouncements.Keys);
        }

        return factions.OrderBy(faction => faction, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Immediately awards the active conquest round to one faction. This intentionally goes through the same
    /// declaration path as an organic victory so round-end text, victory announcements and war-result listeners
    /// all receive the same result.
    /// </summary>
    public bool TryForceVictory(string requestedFaction, out string winner, out string error)
    {
        winner = string.Empty;
        error = string.Empty;

        if (GameTicker.RunLevel != GameRunLevel.InRound)
        {
            error = "The round is not currently running.";
            return false;
        }

        if (GameTicker.IsRoundEndBypassed())
        {
            error = "A RoundEndBypass rule is active. Remove it before forcing a faction victory.";
            return false;
        }

        var activeRules = new List<FactionConquestRuleComponent>();
        FactionConquestRuleComponent? announcingRule = null;
        var query = EntityQueryEnumerator<FactionConquestRuleComponent, GameRuleComponent>();

        while (query.MoveNext(out var uid, out var conquest, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            activeRules.Add(conquest);

            // Admins type the abbreviation they see in game, which for the TFCF is not its frozen prototype id.
            var requestedId = FactionDisplay.ResolveId(requestedFaction.Trim());
            var match = conquest.VictoryAnnouncements.Keys.FirstOrDefault(faction =>
                faction.Equals(requestedId, StringComparison.OrdinalIgnoreCase));

            if (match == null || announcingRule != null)
                continue;

            winner = match;
            announcingRule = conquest;
        }

        if (activeRules.Count == 0)
        {
            error = "No active faction conquest rule was found. Use 'endround' for a generic round end.";
            return false;
        }

        if (announcingRule == null)
        {
            var valid = activeRules
                .SelectMany(rule => rule.VictoryAnnouncements.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(faction => faction, StringComparer.OrdinalIgnoreCase)
                .Select(FactionDisplay.Abbreviation);
            error = $"Unknown faction '{requestedFaction}'. Valid factions: {string.Join(", ", valid)}.";
            return false;
        }

        // Keep every active conquest component consistent if a preset bug started the rule more than once. Only
        // the selected announcing rule goes through Declare, preventing duplicate events and restart timers.
        foreach (var conquest in activeRules)
        {
            conquest.PendingWinners = null;
            conquest.Decided = true;
            conquest.Winners = new List<string> { winner };
        }

        Declare(announcingRule, new List<string> { winner });
        return true;
    }

    private void Declare(FactionConquestRuleComponent conquest, List<string> winners)
    {
        conquest.Decided = true;
        conquest.Winners = winners;

        var decided = new FactionWarDecidedEvent(winners);
        RaiseLocalEvent(ref decided);

        // A lone great power gets its own ending; surviving minors share a generic one that names nobody.
        if (winners.Count == 1 && conquest.VictoryAnnouncements.TryGetValue(winners[0], out var victory))
            _chat.DispatchServerAnnouncement(Loc.GetString(victory));
        else
            _chat.DispatchServerAnnouncement(Loc.GetString(conquest.MinorVictoryAnnouncement));

        GameTicker.EndRound($"{string.Join(", ", winners.Select(FactionDisplay.Abbreviation))} won the war for Taypan.");
        // An admin may restart by hand during the countdown; the timer must not then restart the next round too.
        var roundId = GameTicker.RoundId;
        Timer.Spawn(conquest.RestartDelay, () =>
        {
            if (GameTicker.RoundId == roundId && GameTicker.RunLevel == GameRunLevel.PostRound)
                GameTicker.RestartRound();
        });
    }

    /// <summary>
    /// Factions that still hold a standing station, announcing stations as their last banner falls. A faction
    /// dropping out of this set has only lost its seat of power — it is not out of the war, and its diplomacy is
    /// untouched. A station is under enemy control only when it carries at least one banner and every one of them
    /// is held by someone other than the station's own faction; reclaiming any banner puts it back in the war.
    /// </summary>
    private HashSet<string> GetSurvivingFactions(FactionConquestRuleComponent conquest)
    {
        var alive = new HashSet<string>();
        var seen = new HashSet<EntityUid>();
        var flagsByGrid = GatherFlagsByGrid();

        var query = EntityQueryEnumerator<FactionStationComponent>();
        while (query.MoveNext(out var stationUid, out var station))
        {
            seen.Add(stationUid);
            conquest.KnownStations[stationUid] = station.FallAnnouncement ?? string.Empty;

            // Once the Turning has entered the hull the station cannot be reclaimed by changing its banners.
            // The grid deliberately remains so the infestation can keep consuming it.
            if (conquest.AnnouncedFallen.Contains(stationUid))
            {
                DisableStationShield(conquest, stationUid);
                continue;
            }

            var underControl = IsUnderEnemyControl(flagsByGrid, stationUid, station.Faction, out var captor);

            if (!underControl)
            {
                // Friendly hands again — or the station carries no banners at all. Either way it is in the war,
                // and any running capture clock is wiped so a fresh push has to hold the full window over again.
                conquest.ControlledSince.Remove(stationUid);
                conquest.AnnouncedControl.Remove(stationUid);
                RestoreStationShield(conquest, stationUid, station);
                alive.Add(station.Faction);
                continue;
            }

            // Repeat this cheap scan while the warning is active so an emitter built after the initial capture
            // cannot silently bring the station's shields back online.
            DisableStationShield(conquest, stationUid);

            if (!conquest.ControlledSince.TryGetValue(stationUid, out var since))
            {
                // The capture clock starts now. Say so — the defenders get a chance to answer it, and the
                // attackers learn their push actually landed.
                conquest.ControlledSince[stationUid] = _timing.CurTime;
                AnnounceControl(conquest, stationUid, station, captor);
                alive.Add(station.Faction);
                continue;
            }

            // Enemy-held, but not long enough to count as lost yet.
            if (_timing.CurTime - since < conquest.HoldToFall)
            {
                alive.Add(station.Faction);
                continue;
            }

            AnnounceFall(conquest, stationUid, station);
        }

        // A station that is outright destroyed never gets to be seen captured — it simply vanishes from the query.
        // Catch it here so a blown-apart hull still gets its obituary instead of dying silently.
        foreach (var (tracked, obituary) in conquest.KnownStations.ToList())
        {
            if (seen.Contains(tracked))
                continue;

            if (!string.IsNullOrEmpty(obituary) && conquest.AnnouncedFallen.Add(tracked))
                _chat.DispatchServerAnnouncement(Loc.GetString(obituary));

            conquest.KnownStations.Remove(tracked);
            conquest.ControlledSince.Remove(tracked);
        }

        return alive;
    }

    /// <summary>
    /// Whether a station is currently in enemy hands: it must carry at least one banner and every banner on its grid
    /// must be held by a faction other than <paramref name="owner"/>. <paramref name="captor"/> is the faction holding
    /// the first such banner, for the warning message.
    /// </summary>
    private bool IsUnderEnemyControl(IReadOnlyDictionary<EntityUid, List<ConquestFlagComponent>> flagsByGrid,
        EntityUid stationUid, string owner, out string? captor)
    {
        captor = null;

        if (!flagsByGrid.TryGetValue(stationUid, out var flags) || flags.Count == 0)
            return false;

        foreach (var flag in flags)
        {
            // A banner nobody has taken, or one back in the owner's hands, keeps the whole station out of enemy control.
            if (string.IsNullOrWhiteSpace(flag.OwnerFaction) ||
                string.Equals(flag.OwnerFaction, owner, StringComparison.Ordinal))
                return false;

            captor ??= flag.OwnerFaction;
        }

        return true;
    }

    /// <summary>Every banner grouped by the grid it sits on, gathered in one sweep.</summary>
    private Dictionary<EntityUid, List<ConquestFlagComponent>> GatherFlagsByGrid()
    {
        var byGrid = new Dictionary<EntityUid, List<ConquestFlagComponent>>();

        var query = EntityQueryEnumerator<ConquestFlagComponent, TransformComponent>();
        while (query.MoveNext(out _, out var flag, out var xform))
        {
            if (xform.GridUid is not { } grid)
                continue;

            if (!byGrid.TryGetValue(grid, out var list))
                byGrid[grid] = list = new List<ConquestFlagComponent>();

            list.Add(flag);
        }

        return byGrid;
    }

    /// <summary>
    /// Warns the sector that a station's last banner has fallen and it is now on the clock. Announced once per
    /// capture — reclaiming a banner clears the flag, so a station that is taken twice is reported twice.
    /// </summary>
    private void AnnounceControl(FactionConquestRuleComponent conquest, EntityUid stationUid,
        FactionStationComponent station, string? captor)
    {
        if (!conquest.AnnouncedControl.Add(stationUid))
            return;

        _chat.DispatchServerAnnouncement(Loc.GetString(conquest.ControlAnnouncement,
            ("station", station.StationName),
            ("faction", FactionDisplay.Abbreviation(station.Faction)),
            ("captor", FactionDisplay.Abbreviation(captor ?? station.Faction)),
            ("minutes", (int) conquest.HoldToFall.TotalMinutes)));
    }

    private void AnnounceFall(FactionConquestRuleComponent conquest, EntityUid stationUid, FactionStationComponent station)
    {
        if (!conquest.AnnouncedFallen.Add(stationUid))
            return;

        if (station.FallAnnouncement is { } message)
            _chat.DispatchServerAnnouncement(Loc.GetString(message));

        var ev = new FactionStationFellEvent(stationUid, station.Faction, station.StationName);
        RaiseLocalEvent(ref ev);
    }

    private void DisableStationShield(FactionConquestRuleComponent conquest, EntityUid stationUid)
    {
        if (HasComp<BlockShipWeaponProjectileGridComponent>(stationUid))
        {
            RemComp<BlockShipWeaponProjectileGridComponent>(stationUid);
            conquest.DisabledShields.Add(stationUid);
        }

        if (!conquest.DisabledShieldEmitters.TryGetValue(stationUid, out var disabledEmitters))
            disabledEmitters = new HashSet<EntityUid>();

        var query = EntityQueryEnumerator<ShipShieldEmitterComponent, TransformComponent>();
        while (query.MoveNext(out var emitterUid, out var emitter, out var xform))
        {
            if (xform.GridUid == stationUid && _shipShields.SetForcedDisabled(emitterUid, true, emitter))
                disabledEmitters.Add(emitterUid);
        }

        if (disabledEmitters.Count > 0)
            conquest.DisabledShieldEmitters[stationUid] = disabledEmitters;
    }

    private void RestoreStationShield(FactionConquestRuleComponent conquest, EntityUid stationUid,
        FactionStationComponent station)
    {
        if (TerminatingOrDeleted(stationUid))
            return;

        var restored = false;
        if (conquest.DisabledShields.Remove(stationUid))
        {
            EnsureComp<BlockShipWeaponProjectileGridComponent>(stationUid);
            restored = true;
        }

        if (conquest.DisabledShieldEmitters.Remove(stationUid, out var emitters))
        {
            foreach (var emitterUid in emitters)
            {
                if (!TerminatingOrDeleted(emitterUid) && _shipShields.SetForcedDisabled(emitterUid, false))
                    restored = true;
            }
        }

        if (!restored)
            return;

        _chat.DispatchServerAnnouncement(Loc.GetString(conquest.ControlCancelledAnnouncement,
            ("station", station.StationName),
            ("faction", FactionDisplay.Abbreviation(station.Faction))));
    }

    /// <summary>
    /// Works out, once, which factions this round's war is actually between. Normally that is the configured great
    /// powers — but a mode that maps neither of them is not a round where both already fell, it is a round they
    /// were never in (Freeplay | TFSC &amp; SHI fields no DSM or NCWL station). Treating that as "both great powers
    /// dead" crowns every survivor on the first check and ends the round at the start of it, so the factions that
    /// DID turn up take the great powers' place and the round is decided by the last of them standing.
    ///
    /// Returns false while the sector is still empty, which keeps the whole win condition asleep until the map is up.
    /// </summary>
    private bool TryResolveMajors(FactionConquestRuleComponent conquest)
    {
        if (conquest.ActiveMajors != null)
            return true;

        var present = new HashSet<string>();
        var query = EntityQueryEnumerator<FactionStationComponent>();
        while (query.MoveNext(out _, out var station))
            present.Add(station.Faction);

        if (present.Count == 0)
            return false;

        var majors = conquest.MajorFactions.Where(present.Contains).ToList();

        // One great power alone has no war to win either, so it takes the same fallback.
        conquest.ActiveMajors = majors.Count >= 2 ? majors : present.OrderBy(f => f).ToList();
        return true;
    }

    /// <summary>Decides whether the war is settled, and who is credited for it.</summary>
    private bool TryResolveWinners(FactionConquestRuleComponent conquest, HashSet<string> alive, out List<string> winners)
    {
        winners = new List<string>();

        var greatPowers = conquest.ActiveMajors ?? conquest.MajorFactions;
        var majors = alive.Where(greatPowers.Contains).ToList();

        switch (majors.Count)
        {
            // Both great powers have lost their seats. The minors are not made to finish each other off — whoever
            // is still holding a station inherits Taypan together, allied or not.
            case 0:
                winners = alive.OrderBy(f => f).ToList();
                return true;

            // One great power still holds a seat of power, so the war between the great powers has an answer —
            // and that is the war being fought. Surviving minors do not block it: they never had a throne to lose,
            // and holding Tatsumoto or Jackal to the end would otherwise stall every round out to the time cap.
            case 1:
                winners.Add(majors[0]);
                return true;

            // Two great powers still standing is not a settled war, allied or not.
            default:
                return false;
        }
    }
}
