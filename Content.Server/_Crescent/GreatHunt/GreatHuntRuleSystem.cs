using System.Linq;
using Content.Server.Announcements.Systems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.Chat;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mind;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// King of the Hill win condition for the Great Hunt. Every capture of a <see cref="GreatAltarComponent"/> is
/// announced sector-wide and restarts that altar's hold clock; the first faction to keep an altar for
/// <see cref="GreatHuntRuleComponent.HoldTime"/> wins, the round ends and restarts after
/// <see cref="GreatHuntRuleComponent.RestartDelay"/>.
/// </summary>
/// <remarks>
/// The hunt opens with a preparation phase, the Great Hunt's take on Unionfall's grace period: both home bases are
/// walled in (see GreatHuntRuleSystem.Barrier.cs) and the altar sleeps until it ends. The phase is announced at the
/// start, at each <see cref="GreatHuntRuleComponent.GraceWarnings"/> mark and over a final countdown; then the walls
/// are deleted and the altar wakes. <c>greathunt_skipgrace</c> ends it on the spot.
/// </remarks>
public sealed partial class GreatHuntRuleSystem : GameRuleSystem<GreatHuntRuleComponent>
{
    [Dependency] private readonly AnnouncerSystem _announcer = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GreatAltarCapturedEvent>(OnAltarCaptured);
        InitializeMusic();
    }

    protected override void Started(EntityUid uid, GreatHuntRuleComponent hunt, GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, hunt, gameRule, args);

        if (hunt.GracePeriod < TimeSpan.Zero)
            hunt.GracePeriod = TimeSpan.Zero;

        hunt.GraceEndsAt = Timing.CurTime + hunt.GracePeriod;
    }

    /// <summary>
    /// Ends the preparation phase of every running Great Hunt right now: walls down, altar awake.
    /// Returns how many hunts were still preparing.
    /// </summary>
    public int SkipGracePeriod()
    {
        var skipped = 0;
        var query = QueryActiveRules();
        while (query.MoveNext(out _, out _, out var hunt, out _))
        {
            if (hunt.GraceOver)
                continue;

            EndGrace(hunt, announce: true);
            skipped++;
        }

        return skipped;
    }

    private void OnAltarCaptured(ref GreatAltarCapturedEvent ev)
    {
        var query = QueryActiveRules();
        while (query.MoveNext(out _, out _, out var hunt, out _))
        {
            if (hunt.Decided)
                continue;

            hunt.WarningsGiven[ev.Altar] = 0;
            AnnounceToFactions(hunt, null, hunt.CaptureAnnouncement, false,
                ("faction", FactionDisplay.Abbreviation(ev.Faction)),
                ("previous", ev.PreviousFaction is { } previous ? FactionDisplay.Abbreviation(previous) : "none"),
                ("minutes", (int) hunt.HoldTime.TotalMinutes));
        }
    }

    protected override void ActiveTick(EntityUid uid, GreatHuntRuleComponent hunt, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, hunt, gameRule, frameTime);

        if (hunt.Decided)
            return;

        if (!hunt.GraceOver)
        {
            TickGrace(hunt);
            return;
        }

        var now = Timing.CurTime;
        var altars = EntityQueryEnumerator<GreatAltarComponent>();
        while (altars.MoveNext(out var altarUid, out var altar))
        {
            if (altar.HolderFaction is not { } holder || altar.Locked)
                continue;

            var left = hunt.HoldTime - (now - altar.HeldSince);
            if (left <= TimeSpan.Zero)
            {
                Declare(hunt, holder);
                return;
            }

            AnnounceWarnings(hunt, altarUid, holder, left);
        }
    }

    /// <summary>
    /// Runs the preparation phase: keeps the altar asleep until it ends, gives the announcements as their marks come
    /// up, and brings the walls down once the time is out.
    /// </summary>
    private void TickGrace(GreatHuntRuleComponent hunt)
    {
        var now = Timing.CurTime;
        var left = hunt.GraceEndsAt - now;
        if (left <= TimeSpan.Zero)
        {
            // A hunt configured without preparation never had walls worth announcing.
            EndGrace(hunt, announce: hunt.GracePeriod > TimeSpan.Zero);
            return;
        }

        // Built on the first tick rather than in Started: the bases are loaded by another rule of the same preset,
        // and by now every rule has started and the map is initialised.
        if (!hunt.BarrierBuilt)
            BuildBarriers(hunt);

        // The altar spawns from its marker a tick after map init, so keep re-pinning its unlock time rather than
        // setting it once; this also follows an admin moving GraceEndsAt by hand.
        var altars = EntityQueryEnumerator<GreatAltarComponent>();
        while (altars.MoveNext(out _, out var altar))
        {
            if (altar.UnlockTime < hunt.GraceEndsAt)
                altar.UnlockTime = hunt.GraceEndsAt;
        }

        UpdateGraceMusic(hunt, now);

        // Whole seconds left, rounded up, so the announcements read 00:05:00 rather than 00:04:59.
        var secondsLeft = (int) Math.Ceiling(left.TotalSeconds);
        var warnings = hunt.GraceWarnings
            .Where(w => w.TotalSeconds > hunt.GraceCountdown)
            .OrderByDescending(w => w)
            .ToList();

        if (!hunt.GraceStartAnnounced)
        {
            if (now < hunt.GraceEndsAt - hunt.GracePeriod + hunt.GraceStartAnnouncementDelay &&
                secondsLeft > hunt.GraceCountdown)
                return;

            hunt.GraceStartAnnounced = true;
            Announce(hunt, hunt.GraceStartSound, hunt.GraceStartAnnouncement, ("time", FormatTime(secondsLeft)));

            // Reminders the start announcement already covers would only repeat it.
            while (hunt.GraceWarningsGiven < warnings.Count && left <= warnings[hunt.GraceWarningsGiven])
            {
                hunt.GraceWarningsGiven++;
            }

            return;
        }

        // Only the latest reminder crossed goes out; one an admin skipped past by shortening the phase stays silent.
        var due = -1;
        while (hunt.GraceWarningsGiven < warnings.Count && left <= warnings[hunt.GraceWarningsGiven])
        {
            due = hunt.GraceWarningsGiven;
            hunt.GraceWarningsGiven++;
        }

        if (due >= 0 && secondsLeft > hunt.GraceCountdown)
        {
            var sound = due == warnings.Count - 1 ? hunt.GraceLastWarningSound : hunt.GraceWarningSound;
            Announce(hunt, sound, hunt.GraceWarningAnnouncement, ("time", FormatTime(secondsLeft)));
        }

        if (secondsLeft <= hunt.GraceCountdown && secondsLeft < hunt.LastCountdownAnnounced)
        {
            hunt.LastCountdownAnnounced = secondsLeft;
            Announce(hunt, hunt.GraceCountdownSound, hunt.GraceCountdownAnnouncement, ("time", FormatTime(secondsLeft)));
        }
    }

    /// <summary>Brings the walls down and wakes every altar.</summary>
    private void EndGrace(GreatHuntRuleComponent hunt, bool announce)
    {
        hunt.GraceOver = true;
        StopGraceMusic(hunt);

        var now = Timing.CurTime;
        hunt.GraceEndsAt = now;

        var barriers = EntityQueryEnumerator<GreatHuntGraceBarrierComponent>();
        while (barriers.MoveNext(out var barrier, out _))
        {
            QueueDel(barrier);
        }

        var altars = EntityQueryEnumerator<GreatAltarComponent>();
        while (altars.MoveNext(out _, out var altar))
        {
            if (altar.UnlockTime > now)
                altar.UnlockTime = now;
        }

        if (announce)
            Announce(hunt, hunt.GraceOverSound, hunt.GraceOverAnnouncement);
    }

    private void Announce(GreatHuntRuleComponent hunt, string sound, string message, params (string, object)[] args)
    {
        AnnounceToFactions(hunt, sound, message, true, args);
    }

    /// <summary>
    ///     Sends an announcement to each side of the hunt on its own, in that side's colour and worded for it through
    ///     the <c>$viewer</c> locale argument. Anyone outside <see cref="GreatHuntRuleComponent.AnnouncedFactions"/>
    ///     hears nothing.
    /// </summary>
    /// <param name="sound">Announcer sound id, or null for a silent chat announcement.</param>
    /// <param name="withSender">Whether to frame it as coming from <see cref="GreatHuntRuleComponent.AnnouncementSender"/>.</param>
    private void AnnounceToFactions(GreatHuntRuleComponent hunt,
        string? sound,
        string locale,
        bool withSender,
        params (string, object)[] args)
    {
        var sender = Loc.GetString(hunt.AnnouncementSender);

        foreach (var faction in hunt.AnnouncedFactions)
        {
            var filter = Filter.Empty().AddWhere(session => GetSessionFaction(session) == faction);
            if (!filter.Recipients.Any())
                continue;

            var message = Loc.GetString(locale, args.Append(("viewer", (object) faction)).ToArray());
            if (string.IsNullOrEmpty(message))
                continue;

            if (sound != null)
                _announcer.SendAnnouncementAudio(sound, filter);

            var escaped = FormattedMessage.EscapeText(message);
            var wrapped = withSender
                ? Loc.GetString("chat-manager-sender-announcement-wrap-message", ("sender", sender), ("message", escaped))
                : Loc.GetString("chat-manager-server-wrap-message", ("message", escaped));

            ChatManager.ChatMessageToManyFiltered(filter,
                withSender ? ChatChannel.Radio : ChatChannel.Server,
                message,
                wrapped,
                EntityUid.Invalid,
                false,
                true,
                hunt.FactionColors.GetValueOrDefault(faction, hunt.AnnouncementColor));
        }
    }

    /// <summary>
    ///     The faction a player plays for. Read off the body they control, or for a ghost off the body they left,
    ///     so the dead still follow their side's hunt.
    /// </summary>
    private string? GetSessionFaction(ICommonSession session)
    {
        if (TryComp<HullrotFactionComponent>(session.AttachedEntity, out var attached))
            return attached.Faction;

        if (!_mind.TryGetMind(session, out _, out var mind))
            return null;

        if (TryComp<HullrotFactionComponent>(mind.OwnedEntity, out var owned))
            return owned.Faction;

        if (TryGetEntity(mind.LastOwnedBody, out var body) && TryComp<HullrotFactionComponent>(body, out var last))
            return last.Faction;

        if (TryGetEntity(mind.OriginalOwnedEntity, out var original) && TryComp<HullrotFactionComponent>(original, out var first))
            return first.Faction;

        return null;
    }

    /// <summary>hh:mm:ss, the same clock Unionfall's grace announcements read out.</summary>
    private static string FormatTime(int seconds)
    {
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    /// <summary>Fires every warning whose threshold the current hold has crossed, oldest first, once each.</summary>
    private void AnnounceWarnings(GreatHuntRuleComponent hunt, EntityUid altar, string holder, TimeSpan left)
    {
        var given = hunt.WarningsGiven.GetValueOrDefault(altar);
        var warnings = hunt.Warnings.OrderByDescending(w => w).ToList();

        while (given < warnings.Count && left <= warnings[given])
        {
            AnnounceToFactions(hunt, null, hunt.WarningAnnouncement, false,
                ("faction", FactionDisplay.Abbreviation(holder)),
                ("minutes", (int) Math.Ceiling(warnings[given].TotalMinutes)));
            given++;
        }

        hunt.WarningsGiven[altar] = given;
    }

    private void Declare(GreatHuntRuleComponent hunt, string winner)
    {
        hunt.Decided = true;
        hunt.Winner = winner;

        // Freeze every altar so nobody can flip it during the restart countdown.
        var altars = EntityQueryEnumerator<GreatAltarComponent>();
        while (altars.MoveNext(out _, out var altar))
        {
            altar.Locked = true;
        }

        var announcement = hunt.VictoryAnnouncements.GetValueOrDefault(winner, hunt.FallbackVictoryAnnouncement);
        AnnounceToFactions(hunt, null, announcement, false, ("faction", FactionDisplay.Abbreviation(winner)));

        GameTicker.EndRound(Loc.GetString("great-hunt-round-end", ("faction", FactionDisplay.Abbreviation(winner))));

        // An admin may restart by hand during the countdown; the timer must not then restart the next round too.
        var roundId = GameTicker.RoundId;
        Timer.Spawn(hunt.RestartDelay, () =>
        {
            if (GameTicker.RoundId == roundId && GameTicker.RunLevel == GameRunLevel.PostRound)
                GameTicker.RestartRound();
        });
    }

    protected override void AppendRoundEndText(EntityUid uid, GreatHuntRuleComponent hunt, GameRuleComponent gameRule,
        ref RoundEndTextAppendEvent args)
    {
        base.AppendRoundEndText(uid, hunt, gameRule, ref args);

        if (!GameTicker.IsGameRuleActive(uid, gameRule))
            return;

        args.AddLine(hunt.Winner is { } winner
            ? Loc.GetString("great-hunt-summary-winner", ("faction", FactionDisplay.Abbreviation(winner)))
            : Loc.GetString("great-hunt-summary-none"));
    }
}
