using System;
using System.Globalization;
using Content.Server._Crescent.Bank;
using Content.Server.Administration.Logs;
using Content.Server.Bank;
using Content.Server.CartridgeLoader;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Roles.Jobs;
using Content.Shared._Crescent.CartridgeLoader.Cartridges;
using Content.Shared.Bank.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network; // Eclipsion - blocking
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.CartridgeLoader.Cartridges;

public sealed class MoneyTransferCartridgeSystem : EntitySystem
{
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly JobSystem _jobs = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    private const int MaxCommentLength = 120;
    private const int MaxTransferAmount = 10_000_000;
    private const double TransferCommissionRate = 0.06;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MoneyTransferCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<MoneyTransferCartridgeComponent, CartridgeMessageEvent>(OnUiMessage);
    }

    private void OnUiReady(EntityUid uid, MoneyTransferCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        var sender = GetLoaderMob(args.Loader);
        UpdateUiState(uid, args.Loader, component, sender, error: null, success: null);
    }

    private void OnUiMessage(EntityUid uid, MoneyTransferCartridgeComponent component, CartridgeMessageEvent args)
    {
        // Eclipsion Start - blocking
        if (args is MoneyTransferBlockUiMessageEvent blockMsg)
        {
            HandleBlock(uid, component, blockMsg);
            return;
        }
        // Eclipsion End

        if (args is not MoneyTransferUiMessageEvent msg)
            return;

        var loaderUid = GetEntity(args.LoaderUid);
        string? error = null;
        string? successToast = null;

        var sender = args.Actor;

        if (!_mind.TryGetMind(sender, out _, out _))
        {
            error = Loc.GetString("money-transfer-error-not-player");
        }
        else if (!TryComp<BankAccountComponent>(sender, out var senderBank))
        {
            error = Loc.GetString("money-transfer-error-no-account");
        }
        else
        {
            var recipient = GetEntity(msg.Recipient);
            var amount = msg.Amount;
            var comment = SanitizeComment(msg.Comment);

            if (recipient == sender)
                error = Loc.GetString("money-transfer-error-self");
            else if (!_mind.TryGetMind(recipient, out _, out _))
                error = Loc.GetString("money-transfer-error-recipient");
            else if (!TryComp<BankAccountComponent>(recipient, out _))
                error = Loc.GetString("money-transfer-error-recipient-account");
            else if (_mobState.IsDead(recipient))
                error = Loc.GetString("money-transfer-error-dead");
            // Eclipsion - refused up front, before any money moves, so a block cannot be used to make
            // someone's credits vanish and the sender knows exactly why it did not go through.
            else if (IsBlockedBy(recipient, sender))
                error = Loc.GetString("money-transfer-error-blocked");
            else if (amount <= 0)
                error = Loc.GetString("money-transfer-error-amount");
            else if (amount > MaxTransferAmount)
                error = Loc.GetString("money-transfer-error-max", ("max", MaxTransferAmount));
            else
            {
                var commission = CalculateCommission(amount);
                var totalDebit = amount + commission;

                if (senderBank.Balance < totalDebit)
                {
                    error = Loc.GetString("money-transfer-error-funds-with-commission",
                        ("total", totalDebit),
                        ("commission", commission));
                }
                else if (!_bank.TryBankWithdraw(sender, totalDebit))
                {
                    error = Loc.GetString("money-transfer-error-failed");
                }
                else if (!_bank.TryBankDeposit(recipient, amount))
                {
                    // Compensate the sender if crediting recipient failed after a successful withdraw.
                    if (!_bank.TryBankDeposit(sender, totalDebit))
                    {
                        _adminLog.Add(LogType.ATMUsage, LogImpact.Extreme,
                            $"Failed to rollback transfer debit for {ToPrettyString(sender):player}. Amount: {amount}, Fee: {commission}, Debited: {totalDebit}, Recipient: {ToPrettyString(recipient):player}");
                    }

                    error = Loc.GetString("money-transfer-error-failed");
                }
                else
                {
                    var recipientName = GetTransferDisplayName(recipient);
                    var senderName = GetTransferDisplayName(sender);
                    var roundTime = _gameTicker.RoundDuration();
                    var commentUi = string.IsNullOrEmpty(comment)
                        ? Loc.GetString("money-transfer-comment-none")
                        : comment;

                    AppendHistory(sender, outgoing: true, recipientName, amount, comment, roundTime);
                    AppendHistory(recipient, outgoing: false, senderName, amount, comment, roundTime);

                    _adminLog.Add(LogType.ATMUsage, LogImpact.Low,
                        $"{ToPrettyString(sender):player} transferred {amount} credits to {ToPrettyString(recipient):player}. Fee: {commission}. Debited: {totalDebit}. Comment: {comment}");

                    successToast = Loc.GetString("money-transfer-success-toast",
                        ("amount", amount),
                        ("recipient", recipientName),
                        ("commission", commission),
                        ("total", totalDebit));

                    NotifyTransferChatMessage(sender, recipient, senderName, recipientName, amount, commission, totalDebit, commentUi);
                }
            }
        }

        UpdateUiState(uid, loaderUid, component, sender, error, successToast);
    }

    // Eclipsion Start - blocking
    /// <summary>
    /// Adds or removes a block. The target is named by entity because that is what the app can see, but
    /// the block is stored against their account so swapping bodies or IDs does not shake it off.
    /// </summary>
    private void HandleBlock(EntityUid uid, MoneyTransferCartridgeComponent component, MoneyTransferBlockUiMessageEvent msg)
    {
        var owner = msg.Actor;
        var loaderUid = GetEntity(msg.LoaderUid);

        if (msg.Block)
        {
            var target = GetEntity(msg.Target);
            if (!target.IsValid() || target == owner || GetUser(target) is not { } user)
            {
                UpdateUiState(uid, loaderUid, component, owner, Loc.GetString("money-transfer-error-block-failed"), null);
                return;
            }

            var list = EnsureComp<TransferBlockListComponent>(owner);
            list.Blocked[user] = GetTransferDisplayName(target);
        }
        else
        {
            // Unblocking works off the stored account id: the person may be dead, gibbed or logged out by
            // now, and an entity reference would leave the entry stuck on the list forever.
            if (!TryComp<TransferBlockListComponent>(owner, out var list))
                return;

            if (msg.User is { } user)
                list.Blocked.Remove(user);
            else if (GetEntity(msg.Target) is { Valid: true } target && GetUser(target) is { } targetUser)
                list.Blocked.Remove(targetUser);
        }

        UpdateUiState(uid, loaderUid, component, owner, null, null);
    }

    /// <summary>Whether <paramref name="recipient"/> is refusing transfers from <paramref name="sender"/>.</summary>
    private bool IsBlockedBy(EntityUid recipient, EntityUid sender)
    {
        return TryComp<TransferBlockListComponent>(recipient, out var list)
               && GetUser(sender) is { } user
               && list.Blocked.ContainsKey(user);
    }

    /// <summary>The account behind a body, or null for anything that isn't a player.</summary>
    private NetUserId? GetUser(EntityUid uid)
    {
        if (_mind.TryGetMind(uid, out _, out var mind) && mind.UserId is { } userId)
            return userId;

        return CompOrNull<ActorComponent>(uid)?.PlayerSession.UserId;
    }
    // Eclipsion End

    private void NotifyTransferChatMessage(
        EntityUid sender,
        EntityUid recipient,
        string senderName,
        string recipientName,
        int amount,
        int commission,
        int total,
        string commentUi)
    {
        var plain = Loc.GetString("money-transfer-chat-transfer",
            ("amount", amount),
            ("sender", senderName),
            ("recipient", recipientName),
            ("commission", commission),
            ("total", total),
            ("comment", commentUi));

        // The wrapped line is markup; the names and above all the comment are player text and must not be
        // able to inject tags into the recipient's chat.
        var wrapped = Loc.GetString("money-transfer-chat-transfer-wrapped",
            ("amount", amount),
            ("sender", FormattedMessage.EscapeText(senderName)),
            ("recipient", FormattedMessage.EscapeText(recipientName)),
            ("commission", commission),
            ("total", total),
            ("comment", FormattedMessage.EscapeText(commentUi)));

        SendTransferChatMessageToPlayer(sender, plain, wrapped);
        SendTransferChatMessageToPlayer(recipient, plain, wrapped);
    }

    private void SendTransferChatMessageToPlayer(EntityUid mob, string plain, string wrapped)
    {
        if (!_mind.TryGetMind(mob, out _, out var mind) || mind.UserId == null)
            return;

        if (!_playerManager.TryGetSessionById(mind.UserId.Value, out var session))
            return;

        _chatManager.ChatMessageToOne(
            ChatChannel.Notifications,
            plain,
            wrapped,
            mob,
            false,
            session.Channel,
            audioPath: "/Audio/Machines/id_insert.ogg");
    }

    private string GetTransferDisplayName(EntityUid uid)
    {
        var name = MetaData(uid).EntityName;
        return string.IsNullOrWhiteSpace(name)
            ? Loc.GetString("money-transfer-unknown-person")
            : name;
    }

    private void AppendHistory(EntityUid uid, bool outgoing, string counterpartyName, int amount, string comment, TimeSpan roundTime)
    {
        var hist = EnsureComp<BankTransferHistoryComponent>(uid);
        hist.Entries.Add(new BankTransferHistoryRecord
        {
            Outgoing = outgoing,
            CounterpartyName = counterpartyName,
            Amount = amount,
            Comment = comment,
            RoundTimestamp = roundTime,
        });

        while (hist.Entries.Count > BankTransferHistoryComponent.MaxEntries)
            hist.Entries.RemoveAt(0);
    }

    private static string SanitizeComment(string raw)
    {
        var s = raw.Trim();
        if (s.Length > MaxCommentLength)
            s = s[..MaxCommentLength];
        return s;
    }

    private static int CalculateCommission(int amount)
    {
        var fee = amount * TransferCommissionRate;
        return (int)Math.Round(fee, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Finds the mob currently using this loader (PDA), walking up transform parents when nested in containers.
    /// </summary>
    private EntityUid? GetLoaderMob(EntityUid loaderUid)
    {
        var uid = loaderUid;
        for (var i = 0; i < 32; i++)
        {
            if (TryComp<ActorComponent>(uid, out var actor) &&
                actor.PlayerSession?.AttachedEntity == uid &&
                HasComp<MobStateComponent>(uid))
                return uid;

            var parent = Transform(uid).ParentUid;
            if (!parent.IsValid())
                break;
            uid = parent;
        }

        return null;
    }

    private void UpdateUiState(
        EntityUid cartridgeUid,
        EntityUid loaderUid,
        MoneyTransferCartridgeComponent? component,
        EntityUid? senderMob,
        string? error,
        string? success = null)
    {
        if (!Resolve(cartridgeUid, ref component))
            return;

        var recipients = new List<MoneyTransferRecipientState>();
        long balance = 0;
        var history = new List<MoneyTransferHistoryEntryState>();
        var blocked = new List<MoneyTransferBlockedState>(); // Eclipsion - blocking

        if (senderMob != null && TryComp<BankAccountComponent>(senderMob.Value, out var bank))
            balance = bank.Balance;

        if (senderMob != null)
        {
            foreach (var session in _playerManager.Sessions)
            {
                if (session.Status != SessionStatus.InGame || session.AttachedEntity is not { } uid)
                    continue;

                if (uid == senderMob.Value)
                    continue;

                if (!_mind.TryGetMind(uid, out _, out _))
                    continue;

                if (_mobState.IsDead(uid))
                    continue;

                if (!TryComp<BankAccountComponent>(uid, out _))
                    continue;

                if (_mobState.IsDead(uid))
                    continue;

                var name = MetaData(uid).EntityName;
                var job = Loc.GetString("money-transfer-unknown-job");
                if (_mind.TryGetMind(uid, out var mindId, out _) && _jobs.MindTryGetJobName(mindId, out var jobName))
                    job = jobName;

                // Eclipsion - the row is flagged so the app can offer Unblock instead of Block without a
                // second lookup, and so a blocked person is visibly marked in the send list.
                var isBlocked = GetUser(uid) is { } recipientUser
                                && CompOrNull<TransferBlockListComponent>(senderMob.Value)?.Blocked.ContainsKey(recipientUser) == true;

                recipients.Add(new MoneyTransferRecipientState(GetNetEntity(uid), name, job, isBlocked));
            }

            recipients.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            if (TryComp<BankTransferHistoryComponent>(senderMob.Value, out var hist))
            {
                for (var i = hist.Entries.Count - 1; i >= 0; i--)
                {
                    var e = hist.Entries[i];
                    history.Add(new MoneyTransferHistoryEntryState(
                        e.Outgoing,
                        e.CounterpartyName,
                        e.Amount,
                        e.Comment,
                        FormatRoundTime(e.RoundTimestamp)));
                }
            }
        }

        // Eclipsion Start - blocking. Read straight off the block list rather than off the online player
        // list, so somebody who logged out can still be seen and unblocked.
        if (senderMob != null && TryComp<TransferBlockListComponent>(senderMob.Value, out var blockList))
        {
            foreach (var (user, blockedName) in blockList.Blocked)
            {
                blocked.Add(new MoneyTransferBlockedState(user, blockedName));
            }

            blocked.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        // Eclipsion End

        var successToast = error != null ? null : success;
        var state = new MoneyTransferUiState(balance, recipients, history, error, successToast, blocked);
        _cartridgeLoader.UpdateCartridgeUiState(loaderUid, state);
    }

    private static string FormatRoundTime(TimeSpan ts)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int) ts.TotalMinutes, ts.Seconds);
    }
}
