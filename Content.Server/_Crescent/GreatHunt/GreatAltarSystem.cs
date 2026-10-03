using System.Linq;
using Content.Server.Chat.Systems;
using Content.Server.DoAfter;
using Content.Server.Popups;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.GreatHunt;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.GameTicking.Components;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Runs the ground game for <see cref="GreatAltarComponent"/>: walk up, click, hold still for the do-after, and the
/// altar is yours. Flipping it only changes who holds it and restarts the hold clock — whether that wins the round is
/// decided by <see cref="GreatHuntRuleSystem"/>, which listens for <see cref="GreatAltarCapturedEvent"/>.
/// </summary>
public sealed class GreatAltarSystem : EntitySystem
{
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPointLightSystem _light = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GreatAltarComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<GreatAltarComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<GreatAltarComponent, InteractHandEvent>(OnInteractHand);
        SubscribeLocalEvent<GreatAltarComponent, GreatAltarCaptureDoAfterEvent>(OnCaptureDoAfter);
        SubscribeLocalEvent<GreatAltarComponent, ExaminedEvent>(OnExamine);
    }

    private void OnMapInit(EntityUid uid, GreatAltarComponent altar, MapInitEvent args)
    {
        altar.CaptureTime = MathF.Max(1f, altar.CaptureTime);
        altar.CaptureRange = MathF.Max(0.25f, altar.CaptureRange);
        altar.UnlockTime = _timing.CurTime + TimeSpan.FromSeconds(MathF.Max(0f, altar.GracePeriod));
        UpdateLight(uid, altar);
    }

    private void OnInteractHand(EntityUid uid, GreatAltarComponent altar, InteractHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = TryStartCapture(uid, altar, args.User);
    }

    private void OnActivate(EntityUid uid, GreatAltarComponent altar, ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = TryStartCapture(uid, altar, args.User);
    }

    /// <summary>
    /// Vets the clicker and opens the claim do-after. Returns true once the click has been answered one way or
    /// another, so nothing else tries to handle it.
    /// </summary>
    private bool TryStartCapture(EntityUid uid, GreatAltarComponent altar, EntityUid user)
    {
        if (altar.Locked)
        {
            _popup.PopupEntity(Loc.GetString("great-altar-locked"), uid, user);
            return true;
        }

        if (_timing.CurTime < altar.UnlockTime)
        {
            var left = (int) MathF.Ceiling((float) (altar.UnlockTime - _timing.CurTime).TotalSeconds);
            _popup.PopupEntity(Loc.GetString("great-altar-grace", ("seconds", left)), uid, user);
            return true;
        }

        if (!TryComp<HullrotFactionComponent>(user, out var faction) ||
            string.IsNullOrWhiteSpace(faction.Faction) ||
            !altar.AllowedFactions.Contains(faction.Faction))
        {
            _popup.PopupEntity(Loc.GetString("great-altar-not-allowed"), uid, user);
            return true;
        }

        if (string.Equals(faction.Faction, altar.HolderFaction, StringComparison.Ordinal))
        {
            _popup.PopupEntity(Loc.GetString("great-altar-already-yours"), uid, user);
            return true;
        }

        var doAfter = new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(altar.CaptureTime),
            new GreatAltarCaptureDoAfterEvent(), uid, uid)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            DistanceThreshold = altar.CaptureRange,
            NeedHand = false,
            BlockDuplicate = true,
            CancelDuplicate = false,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
        {
            _popup.PopupEntity(Loc.GetString("great-altar-already-working"), uid, user);
            return true;
        }

        altar.ContestingFaction = faction.Faction;
        altar.ContestUntil = _timing.CurTime + TimeSpan.FromSeconds(altar.CaptureTime);

        // The rite is a prayer: the claimant starts reciting their faction's lines straight away (see Update).
        altar.Chanters[user] = new GreatAltarChant
        {
            Faction = faction.Faction,
            NextLineAt = _timing.CurTime,
            EndsAt = altar.ContestUntil,
        };

        _popup.PopupEntity(Loc.GetString("great-altar-capture-begin-self"), uid, user);
        _popup.PopupEntity(Loc.GetString("great-altar-capture-begin-others", ("faction", FactionDisplay.Abbreviation(faction.Faction))),
            uid, Filter.PvsExcept(user), true, PopupType.MediumCaution);

        return true;
    }

    private void OnCaptureDoAfter(EntityUid uid, GreatAltarComponent altar, GreatAltarCaptureDoAfterEvent args)
    {
        altar.Chanters.Remove(args.User);

        // Someone else may still be mid-rite; keep reporting their claim rather than the one that just ended.
        if (altar.Chanters.Values.MaxBy(chant => chant.EndsAt) is { } ongoing)
        {
            altar.ContestingFaction = ongoing.Faction;
            altar.ContestUntil = ongoing.EndsAt;
        }
        else
        {
            altar.ContestingFaction = null;
            altar.ContestUntil = TimeSpan.Zero;
        }

        if (args.Handled || args.Cancelled || altar.Locked)
            return;

        if (!TryComp<HullrotFactionComponent>(args.User, out var faction) ||
            string.IsNullOrWhiteSpace(faction.Faction) ||
            !altar.AllowedFactions.Contains(faction.Faction))
            return;

        // Someone from the same side may have finished first.
        if (string.Equals(faction.Faction, altar.HolderFaction, StringComparison.Ordinal))
            return;

        args.Handled = true;

        var previous = altar.HolderFaction;
        altar.HolderFaction = faction.Faction;
        altar.HeldSince = _timing.CurTime;
        UpdateLight(uid, altar);

        _popup.PopupEntity(Loc.GetString("great-altar-captured", ("faction", FactionDisplay.Abbreviation(faction.Faction))),
            uid, Filter.Pvs(uid), true, PopupType.LargeCaution);

        var ev = new GreatAltarCapturedEvent(uid, faction.Faction, previous);
        RaiseLocalEvent(ref ev);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GreatAltarComponent>();
        while (query.MoveNext(out _, out var altar))
        {
            if (altar.Chanters.Count == 0)
                continue;

            foreach (var (chanter, chant) in altar.Chanters.ToArray())
            {
                // Gibbed or deleted mid-rite never raises the do-after event, so drop them here.
                if (Deleted(chanter) || now > chant.EndsAt + TimeSpan.FromSeconds(1))
                {
                    altar.Chanters.Remove(chanter);
                    continue;
                }

                if (now < chant.NextLineAt ||
                    !altar.ChantLines.TryGetValue(chant.Faction, out var lines) ||
                    lines.Count == 0)
                    continue;

                var line = lines[Math.Min(chant.NextLine, lines.Count - 1)];
                _chat.TrySendInGameICMessage(chanter, Loc.GetString(line), InGameICChatType.Speak,
                    ChatTransmitRange.Normal, checkRadioPrefix: false);

                chant.NextLine++;
                chant.NextLineAt = now + TimeSpan.FromSeconds(MathF.Max(1f, altar.ChantInterval));
            }
        }
    }

    private void OnExamine(EntityUid uid, GreatAltarComponent altar, ExaminedEvent args)
    {
        if (altar.HolderFaction is { } holder)
        {
            args.PushMarkup(Loc.GetString("great-altar-examine-held", ("faction", FactionDisplay.Abbreviation(holder))));

            if (TryGetHoldTime(out var holdTime))
            {
                var left = holdTime - (_timing.CurTime - altar.HeldSince);
                if (left > TimeSpan.Zero)
                    args.PushMarkup(Loc.GetString("great-altar-examine-remaining", ("time", $"{(int) left.TotalMinutes:00}:{left.Seconds:00}")));
            }
        }
        else
        {
            args.PushMarkup(Loc.GetString("great-altar-examine-unclaimed"));
        }

        if (altar.Locked)
            return;

        if (_timing.CurTime < altar.UnlockTime)
        {
            var left = (int) MathF.Ceiling((float) (altar.UnlockTime - _timing.CurTime).TotalSeconds);
            args.PushMarkup(Loc.GetString("great-altar-examine-grace", ("seconds", left)));
        }
        else if (altar.ContestingFaction != null && _timing.CurTime < altar.ContestUntil)
        {
            args.PushMarkup(Loc.GetString("great-altar-examine-capturing", ("faction", FactionDisplay.Abbreviation(altar.ContestingFaction))));
        }
        else
        {
            args.PushMarkup(Loc.GetString("great-altar-examine-hint", ("seconds", (int) altar.CaptureTime)));
        }
    }

    /// <summary>The hold time of the running Great Hunt, if there is one.</summary>
    private bool TryGetHoldTime(out TimeSpan holdTime)
    {
        var query = EntityQueryEnumerator<ActiveGameRuleComponent, GreatHuntRuleComponent>();
        while (query.MoveNext(out _, out _, out var hunt))
        {
            holdTime = hunt.HoldTime;
            return true;
        }

        holdTime = TimeSpan.Zero;
        return false;
    }

    private void UpdateLight(EntityUid uid, GreatAltarComponent altar)
    {
        var color = altar.HolderFaction != null && altar.FactionColors.TryGetValue(altar.HolderFaction, out var factionColor)
            ? factionColor
            : altar.NeutralColor;

        _light.SetColor(uid, color);

        // The sprite swap per faction lives in the prototype's GenericVisualizer; unclaimed keeps the default layers.
        if (altar.HolderFaction != null)
            _appearance.SetData(uid, GreatAltarVisuals.Holder, altar.HolderFaction);
        else
            _appearance.RemoveData(uid, GreatAltarVisuals.Holder);
    }
}
