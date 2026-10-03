using System.Linq;
using Content.Server._Crescent.Overwatch;
using Content.Server.Chat.Managers;
using Content.Server.Crescent.Dispenser;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Shipyard;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Shipyard;

/// <summary>
/// Holds ship purchases that are too expensive for one officer to sign off alone, until somebody with
/// treasury access approves them.
/// </summary>
/// <remarks>
/// A faction shipyard spends the faction's money, and the only limit on that used to be a per-player
/// ship count: one captain could commit most of the vault on their own before anyone else heard about
/// it. Anything above <see cref="ShipyardConsoleComponent.ApprovalTreasuryFraction"/> of the current
/// balance now has to be signed off at the treasury console the shipyard banks into, which is where the
/// people who own that money already are.
///
/// Requests are held here rather than on the console entity because a faction has several treasury
/// consoles and any of them should be able to answer, and because a console can be destroyed between the
/// request and the decision without stranding the purchase.
/// </remarks>
public sealed class ShipPurchaseApprovalSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly OverwatchSystem _overwatch = default!;
    [Dependency] private readonly StationTradeMarketSystem _market = default!;

    /// <summary>
    /// How long a request waits for an answer, and how long an approval stays good for once given. Long
    /// enough for command to walk to a console, short enough that an approval signed half a shift ago
    /// cannot be cashed in against a treasury that has since been emptied.
    /// </summary>
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(15);

    private readonly Dictionary<uint, PendingShipPurchase> _pending = new();
    private uint _nextId = 1;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _pending.Clear();
        _nextId = 1;
    }

    /// <summary>
    /// Whether a purchase of <paramref name="price"/> from this station's treasury is big enough to need
    /// sign-off. Measured as a share of the balance rather than a flat number so it scales with how rich
    /// the faction actually is: a cruiser is a routine buy for a full vault and a crisis for an empty one.
    /// </summary>
    public bool RequiresApproval(EntityUid station, int price, ShipyardConsoleComponent console)
    {
        if (console.ApprovalTreasuryFraction <= 0f)
            return false;

        var treasury = _market.GetTreasury(station);
        if (treasury <= 0)
            return true;

        return price > treasury * console.ApprovalTreasuryFraction;
    }

    /// <summary>
    /// Spends an approval, if this buyer has one for this vessel. Approvals are one-shot: an officer
    /// signed off on a ship, not on a standing order.
    /// </summary>
    public bool TryConsumeApproval(string faction, NetUserId buyer, string vesselId)
    {
        Prune();

        foreach (var (id, request) in _pending)
        {
            if (!request.Approved
                || request.Faction != faction
                || request.Buyer != buyer
                || request.VesselId != vesselId)
            {
                continue;
            }

            _pending.Remove(id);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Files a request, or reports that one is already waiting. A repeat click creates nothing: a buyer
    /// hammering the purchase button must not be able to bury the approval list under duplicates.
    /// </summary>
    public ShipPurchaseRequestResult RequestApproval(
        EntityUid station,
        string faction,
        EntityUid console,
        string buyerName,
        NetUserId buyer,
        string vesselId,
        string vesselName,
        int price)
    {
        Prune();

        if (_pending.Values.Any(r => r.Buyer == buyer && r.VesselId == vesselId && r.Faction == faction))
            return ShipPurchaseRequestResult.AlreadyPending;

        var id = _nextId++;
        _pending[id] = new PendingShipPurchase
        {
            Id = id,
            Faction = faction,
            Station = station,
            Console = console,
            Buyer = buyer,
            BuyerName = buyerName,
            VesselId = vesselId,
            VesselName = vesselName,
            Price = price,
            Expires = _timing.CurTime + RequestLifetime,
        };

        // The faction hears about it on its own channel, because a request nobody knows about is a
        // request that expires unanswered.
        _overwatch.SendFactionAnnouncement(faction,
            Loc.GetString("shipyard-approval-announce-request",
                ("buyer", buyerName), ("vessel", vesselName), ("price", price)));

        return ShipPurchaseRequestResult.Filed;
    }

    /// <summary>Signs off a request. The buyer still has to go back to the shipyard and buy the ship.</summary>
    public bool Approve(uint id, string faction, string approver)
    {
        // Ids are sequential, so the faction is checked too: otherwise any vault could answer for any other.
        if (!_pending.TryGetValue(id, out var request) || request.Approved || request.Faction != faction)
            return false;

        request.Approved = true;
        request.Approver = approver;
        request.Expires = _timing.CurTime + RequestLifetime;

        NotifyBuyer(request,
            Loc.GetString("shipyard-approval-buyer-approved",
                ("vessel", request.VesselName), ("approver", approver)));

        return true;
    }

    /// <summary>Refuses a request outright and drops it off the list.</summary>
    public bool Deny(uint id, string faction, string denier)
    {
        if (!_pending.TryGetValue(id, out var request) || request.Faction != faction)
            return false;

        _pending.Remove(id);

        NotifyBuyer(request,
            Loc.GetString("shipyard-approval-buyer-denied",
                ("vessel", request.VesselName), ("approver", denier)));

        return true;
    }

    /// <summary>Everything this faction still has to answer, oldest first.</summary>
    public List<PendingShipPurchase> GetPending(string faction)
    {
        Prune();

        return _pending.Values
            .Where(r => r.Faction == faction)
            .OrderBy(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// Drops requests nobody answered in time. Without this an approval could sit around all round and
    /// be spent against a treasury in a completely different state to the one it was judged against.
    /// </summary>
    private void Prune()
    {
        var now = _timing.CurTime;
        foreach (var (id, request) in _pending.ToArray())
        {
            if (request.Expires <= now)
                _pending.Remove(id);
        }
    }

    /// <summary>Tells the buyer what happened, wherever in the sector they have wandered off to.</summary>
    private void NotifyBuyer(PendingShipPurchase request, string message)
    {
        if (!_players.TryGetSessionById(request.Buyer, out var session))
            return;

        _chat.ChatMessageToOne(
            ChatChannel.Notifications,
            message,
            message,
            EntityUid.Invalid,
            false,
            session.Channel);
    }
}

/// <summary>What happened when a buyer asked for sign-off.</summary>
public enum ShipPurchaseRequestResult : byte
{
    /// <summary>A new request went out to the faction's treasury consoles.</summary>
    Filed,

    /// <summary>This buyer already has this exact request waiting for an answer.</summary>
    AlreadyPending,
}

/// <summary>One ship purchase waiting on somebody with treasury access.</summary>
public sealed class PendingShipPurchase
{
    public uint Id;

    /// <summary>Faction whose treasury pays, and whose consoles can answer.</summary>
    public string Faction = string.Empty;

    public EntityUid Station;

    /// <summary>The shipyard console the request came from.</summary>
    public EntityUid Console;

    public NetUserId Buyer;
    public string BuyerName = string.Empty;

    public string VesselId = string.Empty;
    public string VesselName = string.Empty;
    public int Price;

    /// <summary>Set once somebody signs off; the buyer then has until <see cref="Expires"/> to collect.</summary>
    public bool Approved;

    public string? Approver;

    public TimeSpan Expires;
}
