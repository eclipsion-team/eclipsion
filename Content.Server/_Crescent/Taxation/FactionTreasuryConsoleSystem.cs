using Content.Server.Crescent.Dispenser;
using Content.Server._Crescent.Economy;
using Content.Server._Crescent.Overwatch;
using Content.Server._Crescent.Shipyard; // Eclipsion - high-value purchase approval
using Content.Server.Power.EntitySystems;
using Content.Server.Stack;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Taxation;
using Content.Shared.Access.Systems;
using Content.Shared.Ghost;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Taxation;

/// <summary>
/// Backs the faction treasury console: authorized faction members view/withdraw accumulated tax
/// revenue. Anyone without access cannot open the UI; instead their click starts (or reports on) a
/// robbery that siphons a fixed share of the vault over several minutes and then stops, so defenders
/// can respond and the vault is never drained instantly.
///
/// Depositing physical cash is deliberately ungated — anyone holding a bill stack can pay into any
/// faction's vault by clicking it with the cash. Only taking money out is a privilege; putting money
/// in costs the faction nothing, and gating it just stopped rank-and-file members from banking their
/// own earnings.
/// </summary>
public sealed class FactionTreasuryConsoleSystem : EntitySystem
{
    [Dependency] private readonly StationTradeMarketSystem _market = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly OverwatchSystem _overwatch = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly FactionMachineSystem _factionMachine = default!;
    [Dependency] private readonly ShipPurchaseApprovalSystem _approvals = default!; // Eclipsion - high-value purchase approval
    [Dependency] private readonly OfflineFactionProtectionSystem _protection = default!;

    /// <summary>How often an open vault console re-reads the balance and the viewer's remaining share.</summary>
    private const float RefreshInterval = 3f;

    private float _sinceRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FactionTreasuryConsoleComponent, MapInitEvent>(OnConsoleMapInit);
        SubscribeLocalEvent<FactionTreasuryConsoleComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
        SubscribeLocalEvent<FactionTreasuryConsoleComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<FactionTreasuryConsoleComponent, TreasuryWithdrawMessage>(OnWithdraw);
        SubscribeLocalEvent<FactionTreasuryConsoleComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FactionTreasuryConsoleComponent, TreasuryPurchaseDecisionMessage>(OnPurchaseDecision); // Eclipsion
    }

    /// <summary>
    /// On round start, tie this station's treasury to the console's faction and load its persisted,
    /// cross-round balance so the vault no longer resets to zero each round.
    /// </summary>
    private void OnConsoleMapInit(EntityUid uid, FactionTreasuryConsoleComponent comp, MapInitEvent args)
    {
        if (string.IsNullOrEmpty(comp.Faction))
            return;

        var station = _market.TryGetOwningStation(uid);
        if (station is null)
            return;

        _market.BindFactionTreasury(station.Value, comp.Faction);
        UpdateUi(uid);
    }

    /// <summary>
    /// Gate the UI: without faction funds access the console won't open at all. An outsider trying
    /// anyway pops a warning and sounds the intrusion alarm (rate-limited so it can't be spammed);
    /// the faction's own rank and file are merely turned away.
    /// </summary>
    private void OnOpenAttempt(EntityUid uid, FactionTreasuryConsoleComponent comp, ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (_access.IsAllowed(args.User, uid))
            return;

        args.Cancel();

        // An admin ghost looking inside is not a break-in. Ordinary ghosts cannot interact at all, so
        // anything reaching this point is an admin — same stance as FactionMachineSystem.
        if (HasComp<GhostComponent>(args.User))
            return;

        // Do not reveal whether the faction is offline or understaffed. To an outsider a protected
        // vault behaves like an ordinary credential rejection and never starts the robbery machinery.
        if (_protection.IsProtected(GetConsoleFaction(uid, comp)))
        {
            _popup.PopupEntity(Loc.GetString("treasury-console-not-command"), uid, args.User, PopupType.Medium);
            return;
        }

        // Checked before the intrusion popup so a dead vault gives one honest message rather than
        // "alarm engaged" immediately followed by "there is no power".
        if (!_power.IsPowered(uid))
        {
            _popup.PopupEntity(Loc.GetString("treasury-console-unpowered"), uid, args.User, PopupType.Medium);
            return;
        }

        // A shipbreaker rattling the vault door of the station they work on is not a heist. Only
        // someone from outside the faction can rob it; members just get told no.
        if (IsOwnFactionMember(uid, comp, args.User))
        {
            _popup.PopupEntity(Loc.GetString("treasury-console-not-command"), uid, args.User, PopupType.Medium);
            return;
        }

        _popup.PopupEntity(Loc.GetString("treasury-console-intrusion"), uid, args.User, PopupType.MediumCaution);
        TriggerRobbery(uid, comp, args.User);
    }

    /// <summary>
    /// Whether <paramref name="user"/> belongs to the faction that owns this vault. Membership lives on
    /// the body rather than the ID card, so a stolen card cannot make a thief count as staff. Falls back
    /// to the console's grid when the console itself names no faction.
    /// </summary>
    private bool IsOwnFactionMember(EntityUid uid, FactionTreasuryConsoleComponent comp, EntityUid user)
    {
        var userFaction = CompOrNull<HullrotFactionComponent>(user)?.Faction ?? string.Empty;
        if (string.IsNullOrEmpty(userFaction))
            return false;

        // Compared raw, not folded through FactionMachineSystem's parent-faction table: that table maps
        // TAP onto TFSC for machine ownership, which here would quietly make every Pact nomad count as
        // TFCF staff and lock them out of robbing the Freeport. Every job writes the same faction id
        // its vault is configured with, so an exact match is what we want.
        var owner = string.IsNullOrEmpty(comp.Faction)
            ? _factionMachine.GetFaction(uid)
            : comp.Faction;

        return userFaction == owner;
    }

    /// <summary>
    /// A thief's unauthorized click. If the vault is idle, starts a fresh heist that siphons
    /// <see cref="FactionTreasuryConsoleComponent.RobberyFraction"/> of the current balance over
    /// <see cref="FactionTreasuryConsoleComponent.RobberyDuration"/>, then stops on its own. If a
    /// robbery is already running, just reports progress to the robber (their "screen"/log feedback).
    /// Clicking again after one finishes starts another share.
    /// </summary>
    private void TriggerRobbery(EntityUid uid, FactionTreasuryConsoleComponent comp, EntityUid user)
    {
        // A dead vault cannot be cracked. Cutting the station's power is a valid defence against a
        // heist - it also locks the owners out, so it costs the faction something to do it.
        if (!_power.IsPowered(uid))
        {
            _popup.PopupEntity(Loc.GetString("treasury-console-unpowered"), uid, user, PopupType.Medium);
            return;
        }

        var now = _timing.CurTime;

        SoundAlarm(uid, comp);
        AnnounceIntrusion(uid, comp);

        // Already robbing? Report status instead of stacking another heist on top.
        if (comp.RobberyStart is not null && comp.RobberyEnd is { } activeEnd && now < activeEnd)
        {
            var remainingCr = Math.Max(0, comp.RobberyTotal - comp.RobberyDispensed);
            var minsLeft = Math.Max(1, (int) Math.Ceiling((activeEnd - now).TotalMinutes));
            _popup.PopupEntity(
                Loc.GetString("treasury-console-robbery-progress",
                    ("stolen", comp.RobberyDispensed), ("remaining", remainingCr), ("minutes", minsLeft)),
                uid, user, PopupType.MediumCaution);
            return;
        }

        var station = _market.TryGetOwningStation(uid);
        var balance = station is null ? 0 : _market.GetTreasury(station.Value);

        var target = (int) (balance * comp.RobberyFraction);
        if (target <= 0)
        {
            _popup.PopupEntity(Loc.GetString("treasury-console-robbery-empty"), uid, user, PopupType.MediumCaution);
            return;
        }

        comp.RobberyTotal = target;
        comp.RobberyDispensed = 0;
        comp.RobberyStart = now;
        comp.RobberyEnd = now + comp.RobberyDuration;
        comp.LastLoot = now;

        var mins = Math.Max(1, (int) Math.Round(comp.RobberyDuration.TotalMinutes));
        var pct = (int) MathF.Round(comp.RobberyFraction * 100f);
        _popup.PopupEntity(
            Loc.GetString("treasury-console-robbery-started",
                ("amount", target), ("percent", pct), ("minutes", mins)),
            uid, user, PopupType.LargeCaution);

        UpdateUi(uid);
    }

    /// <summary>Plays the local intrusion alarm, throttled by <see cref="FactionTreasuryConsoleComponent.AlarmInterval"/> so it never spams.</summary>
    private void SoundAlarm(EntityUid uid, FactionTreasuryConsoleComponent comp)
    {
        var now = _timing.CurTime;
        if (comp.LastAlarm is { } last && now - last < comp.AlarmInterval)
            return;

        comp.LastAlarm = now;
        _audio.PlayPvs(comp.AlarmSound, uid);
    }

    /// <summary>
    /// Fires a faction-only overwatch alert naming the station (throttled by
    /// <see cref="FactionTreasuryConsoleComponent.AnnounceCooldown"/>). Only the vault's faction sees it.
    /// </summary>
    private void AnnounceIntrusion(EntityUid uid, FactionTreasuryConsoleComponent comp)
    {
        if (string.IsNullOrEmpty(comp.Faction))
            return;

        var now = _timing.CurTime;
        if (comp.LastAnnounce is { } lastAnnounce && now - lastAnnounce < comp.AnnounceCooldown)
            return;

        comp.LastAnnounce = now;

        // Name the station in the alert for roleplay flavour (e.g. "the Aurora treasury vault").
        var station = _market.TryGetOwningStation(uid);
        var stationName = station is null ? Loc.GetString("treasury-console-alarm-unknown-station") : MetaData(station.Value).EntityName;

        _overwatch.SendFactionAnnouncement(
            comp.Faction,
            Loc.GetString("treasury-console-alarm-announcement", ("station", stationName)));
    }

    /// <summary>
    /// Depositing physical cash (SpaceCash) straight into the faction treasury. Open to everyone: no
    /// access check and no alarm, so a deckhand, a visiting trader or a guilty pirate can all pay in.
    /// </summary>
    private void OnInteractUsing(EntityUid uid, FactionTreasuryConsoleComponent comp, InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        // Only cash stacks may be deposited. Match on the Credit stack type, not the prototype ID:
        // real cash is spawned as denominations (SpaceCash1000, SpaceCash10000, ...), all of which
        // share stackType "Credit". Checking ID == "SpaceCash" rejected every actual bill stack.
        if (!TryComp<StackComponent>(args.Used, out var stack)
            || stack.StackTypeId != "Credit")
        {
            return;
        }

        args.Handled = true;

        if (!_power.IsPowered(uid))
        {
            _popup.PopupEntity(Loc.GetString("treasury-console-unpowered"), uid, args.User, PopupType.Medium);
            return;
        }

        var station = _market.TryGetOwningStation(uid);
        if (station is null || stack.Count <= 0)
            return;

        // Deposit at most DepositPerClick, taken out of the stack rather than swallowing it whole.
        // Clicking the vault with a large stack used to bank every last credit and delete the item with
        // no prompt, so paying in a thousand from a five-million stack cost you the five million.
        var amount = Math.Min(stack.Count, comp.DepositPerClick);
        if (!_stack.Use(args.Used, amount, stack))
            return;

        _market.AddTreasury(station.Value, amount);

        // Paying in is open to everyone, but re-securing stays a privilege: otherwise a single credit
        // from any passer-by — including one of the thieves — would call off a robbery in progress.
        if (_access.IsAllowed(args.User, uid))
            Resecure(comp);

        _popup.PopupEntity(Loc.GetString("treasury-console-deposited", ("amount", amount)), uid, args.User, PopupType.Medium);
        UpdateUi(uid);
    }

    private void OnOpened(EntityUid uid, FactionTreasuryConsoleComponent comp, BoundUIOpenedEvent args)
    {
        // OnOpenAttempt already denied anyone without access, so reaching here means authorized:
        // an authorized member at the console re-secures any breach in progress.
        Resecure(comp);
        UpdateUi(uid);
    }

    private void OnWithdraw(EntityUid uid, FactionTreasuryConsoleComponent comp, TreasuryWithdrawMessage args)
    {
        if (!_access.IsAllowed(args.Actor, uid))
            return;

        // An authorized member operating the console re-secures a breach in progress.
        Resecure(comp);

        var station = _market.TryGetOwningStation(uid);
        if (station is null)
            return;

        var amount = Math.Max(0, args.Amount);
        if (amount == 0)
            return;

        // Identify the withdrawing player so we can enforce the per-person, per-round cap.
        if (!TryComp<ActorComponent>(args.Actor, out var actor))
            return;

        var withdrawn = _market.TryWithdrawTreasuryCapped(
            station.Value, actor.PlayerSession.UserId, amount, comp.MaxWithdrawFraction);

        if (withdrawn > 0)
        {
            _stack.SpawnMultiple("SpaceCash", withdrawn, Transform(uid).Coordinates);
            _popup.PopupEntity(
                Loc.GetString("treasury-console-withdrew", ("amount", withdrawn)),
                uid, args.Actor, PopupType.Medium);
        }
        else
        {
            // Either the vault is empty or this member has already hit their round withdrawal cap.
            _popup.PopupEntity(
                Loc.GetString("treasury-console-limit-reached",
                    ("percent", (int) MathF.Round(comp.MaxWithdrawFraction * 100f))),
                uid, args.Actor, PopupType.MediumCaution);
        }

        UpdateUi(uid);
    }

    // Eclipsion Start - high-value purchase approval
    /// <summary>
    /// Signs off (or refuses) a ship purchase somebody asked for at a faction shipyard. Gated by the same
    /// access reader as withdrawing: committing a chunk of the vault to a hull is spending the faction's
    /// money just as much as taking cash out of it is.
    /// </summary>
    private void OnPurchaseDecision(EntityUid uid, FactionTreasuryConsoleComponent comp, TreasuryPurchaseDecisionMessage args)
    {
        if (!_access.IsAllowed(args.Actor, uid))
            return;

        var approver = Name(args.Actor);
        var faction = GetConsoleFaction(uid, comp);
        if (string.IsNullOrEmpty(faction))
            return;

        if (args.Approve)
            _approvals.Approve(args.Id, faction, approver);
        else
            _approvals.Deny(args.Id, faction, approver);

        UpdateUi(uid);
    }

    /// <summary>
    /// The faction this console answers for. Falls back to the console's grid when the vault names no
    /// faction of its own, matching how the intrusion alarm resolves it.
    /// </summary>
    private string GetConsoleFaction(EntityUid uid, FactionTreasuryConsoleComponent comp)
    {
        return string.IsNullOrEmpty(comp.Faction) ? _factionMachine.GetFaction(uid) : comp.Faction;
    }
    // Eclipsion End

    /// <summary>
    /// Pays out active robberies: each running heist spills its captured share as physical cash next
    /// to the console, spread evenly over <see cref="FactionTreasuryConsoleComponent.RobberyDuration"/>,
    /// then stops on its own. An authorized member operating the console (or an empty vault) ends it early.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // The balance moves from trade tax, payroll, drone and shipyard purchases and other consoles,
        // none of which the vault hears about, so an open UI has to re-read it on a timer or it shows
        // whatever was true when the operator last pressed a button.
        _sinceRefresh += frameTime;
        var refresh = _sinceRefresh >= RefreshInterval;
        if (refresh)
            _sinceRefresh = 0f;

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<FactionTreasuryConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (refresh && _ui.IsUiOpen(uid, TreasuryConsoleUiKey.Key))
                UpdateUi(uid);

            if (comp.RobberyStart is not { } start || comp.RobberyEnd is not { } end)
                continue;

            // Power cut mid-heist: the siphon dies with the console and the thieves keep only what
            // has already been dispensed. They have to start over once the lights come back.
            if (!_power.IsPowered(uid))
            {
                _popup.PopupEntity(Loc.GetString("treasury-console-robbery-cut"), uid, PopupType.MediumCaution);
                StopRobbery(comp);
                UpdateUi(uid);
                continue;
            }

            var finished = now >= end;

            // Pace the payout; skip until the next tick unless we're wrapping up.
            if (!finished && comp.LastLoot is { } last && now - last < comp.RobberyTickInterval)
                continue;

            comp.LastLoot = now;

            var station = _market.TryGetOwningStation(uid);
            if (station is not null)
            {
                // How much of the captured total should have been paid out by now (linear over the duration).
                var progress = finished
                    ? 1.0
                    : Math.Clamp((now - start).TotalSeconds / comp.RobberyDuration.TotalSeconds, 0.0, 1.0);
                var due = (int) (comp.RobberyTotal * progress) - comp.RobberyDispensed;

                if (due > 0)
                {
                    var stolen = _market.TryWithdrawTreasury(station.Value, due);
                    if (stolen > 0)
                    {
                        comp.RobberyDispensed += stolen;
                        _stack.SpawnMultiple("SpaceCash", stolen, Transform(uid).Coordinates);
                        _popup.PopupEntity(Loc.GetString("treasury-console-looted", ("amount", stolen)), uid, PopupType.LargeCaution);
                        UpdateUi(uid);
                    }
                }

                SoundAlarm(uid, comp);
            }

            if (finished)
                StopRobbery(comp);
        }
    }

    /// <summary>Clears any active robbery: it has finished paying out, or an authorized member re-secured the console.</summary>
    private void StopRobbery(FactionTreasuryConsoleComponent comp)
    {
        comp.RobberyStart = null;
        comp.RobberyEnd = null;
        comp.RobberyTotal = 0;
        comp.RobberyDispensed = 0;
        comp.LastLoot = null;
    }

    /// <summary>An authorized member has taken control of the console, halting any robbery in progress.</summary>
    private void Resecure(FactionTreasuryConsoleComponent comp)
    {
        StopRobbery(comp);
    }

    private void UpdateUi(EntityUid uid)
    {
        var station = _market.TryGetOwningStation(uid);
        var balance = station is null ? 0 : _market.GetTreasury(station.Value);
        var robbed = TryComp<FactionTreasuryConsoleComponent>(uid, out var comp) && comp.RobberyStart != null;
        var capFraction = comp?.MaxWithdrawFraction ?? 0f;

        // The remaining allowance is per player, so it is read for whoever currently holds the console.
        // The vault is singleUser by way of its access gate — only one member is ever looking.
        var remaining = balance;
        if (station is { } s
            && _ui.GetActors(uid, TreasuryConsoleUiKey.Key).FirstOrDefault() is { Valid: true } viewer
            && TryComp<ActorComponent>(viewer, out var viewerActor))
        {
            remaining = _market.GetRemainingWithdrawal(s, viewerActor.PlayerSession.UserId, capFraction);
        }

        // Eclipsion Start - purchase approval. Read per faction rather than per console: a faction has
        // several vaults and any of them should be able to answer for a purchase asked of the treasury.
        var requests = new List<TreasuryPurchaseRequestState>();
        if (comp != null)
        {
            var factionId = GetConsoleFaction(uid, comp);
            if (!string.IsNullOrEmpty(factionId))
            {
                foreach (var request in _approvals.GetPending(factionId))
                {
                    requests.Add(new TreasuryPurchaseRequestState(
                        request.Id,
                        request.BuyerName,
                        request.VesselName,
                        request.Price,
                        request.Approved,
                        request.Approver));
                }
            }
        }
        // Eclipsion End

        // Only authorized members can have the UI open, so authorized is always true here.
        _ui.SetUiState(uid, TreasuryConsoleUiKey.Key, new TreasuryConsoleState(
            balance,
            true,
            robbed,
            remaining,
            (int) MathF.Round(capFraction * 100f),
            requests)); // Eclipsion - purchase approval
    }

    /// <summary>
    /// Refreshes the treasury UI for every console owned by <paramref name="station"/>. Called when the
    /// balance is changed out-of-band (e.g. from the admin economy panel) so an open console reflects the
    /// new balance immediately instead of waiting for the next withdraw/deposit/robbery tick.
    /// </summary>
    public void RefreshStationConsoles(EntityUid station)
    {
        var query = EntityQueryEnumerator<FactionTreasuryConsoleComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_market.TryGetOwningStation(uid) == station)
                UpdateUi(uid);
        }
    }
}
