using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Bank;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Crescent.Dispenser;
using Content.Server.Preferences.Managers;
using Content.Server._Crescent.Taxation;
using Content.Shared.Bank.Components;
using Content.Shared._NF.Cargo.Components;
using Content.Shared._Crescent.Economy;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Preferences;
using Content.Shared.Shipyard.Prototypes;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Economy;

/// <summary>
/// Runtime economy price overrides editable from the admin menu.
/// </summary>
public sealed class EconomyPriceSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly StationTradeMarketSystem _market = default!;
    [Dependency] private readonly StockCompanySystem _stocks = default!;
    [Dependency] private readonly IServerPreferencesManager _prefs = default!;
    [Dependency] private readonly FactionTreasuryConsoleSystem _treasuryConsole = default!;

    private readonly Dictionary<string, double> _itemOverrides = new();
    private readonly Dictionary<string, int> _vesselOverrides = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<EconomyAdminRequestListEvent>(OnRequestList);
        SubscribeNetworkEvent<EconomyAdminSetPriceEvent>(OnSetPrice);
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Connected)
            return;

        RaiseNetworkEvent(new EconomyPriceSyncEvent
        {
            ItemOverrides = new Dictionary<string, double>(_itemOverrides),
            VesselOverrides = new Dictionary<string, int>(_vesselOverrides),
        }, args.Session);
    }

    public bool TryGetItemOverride(string protoId, out double price) =>
        _itemOverrides.TryGetValue(protoId, out price);

    public int GetVesselPrice(VesselPrototype vessel) =>
        _vesselOverrides.TryGetValue(vessel.ID, out var price) ? price : vessel.Price;

    public double GetEffectiveItemPrice(string protoId, double basePrice) =>
        _itemOverrides.TryGetValue(protoId, out var price) ? price : basePrice;

    private void OnRequestList(EconomyAdminRequestListEvent msg, EntitySessionEventArgs args)
    {
        if (!IsEconomyAdmin(args.SenderSession))
            return;

        var entries = msg.Category switch
        {
            EconomyListCategory.Items => BuildItemEntries(msg.SearchFilter),
            EconomyListCategory.Vessels => BuildVesselEntries(msg.SearchFilter),
            EconomyListCategory.Treasury => BuildTreasuryEntries(msg.SearchFilter),
            EconomyListCategory.Players => BuildPlayerEntries(msg.SearchFilter),
            EconomyListCategory.Stocks => BuildStockEntries(msg.SearchFilter),
            _ => new List<EconomyPriceEntry>(),
        };

        RaiseNetworkEvent(new EconomyAdminListEvent
        {
            Category = msg.Category,
            Entries = entries,
        }, args.SenderSession);
    }

    private void OnSetPrice(EconomyAdminSetPriceEvent msg, EntitySessionEventArgs args)
    {
        if (!IsEconomyAdmin(args.SenderSession))
            return;

        // NaN slips past a plain "< 0" check and would poison every price it touched; infinities overflow the
        // integer conversions below.
        if (!double.IsFinite(msg.Price) || msg.Price < 0)
            return;

        // Treasury and player-bank balances are live entity state, not prototype price overrides.
        switch (msg.Category)
        {
            case EconomyListCategory.Treasury:
                SetTreasuryBalance(msg, args.SenderSession);
                return;
            case EconomyListCategory.Players:
                SetPlayerBalance(msg, args.SenderSession);
                return;
            case EconomyListCategory.Stocks:
                SetStockPrice(msg, args.SenderSession);
                return;
        }

        double oldPrice;
        double basePrice;
        var resetToBase = false;

        switch (msg.Category)
        {
            case EconomyListCategory.Items:
                if (!_prototypeManager.TryIndex<EntityPrototype>(msg.Id, out var proto)
                    || !TryGetItemPriceInfo(proto, out _, out basePrice))
                {
                    return;
                }

                oldPrice = GetEffectiveItemPrice(msg.Id, basePrice);
                resetToBase = Math.Abs(msg.Price - basePrice) < 0.001;

                if (resetToBase)
                    _itemOverrides.Remove(msg.Id);
                else
                    _itemOverrides[msg.Id] = msg.Price;

                ApplyItemPriceToWorld(msg.Id, msg.Price);
                break;
            case EconomyListCategory.Vessels:
                if (!_prototypeManager.TryIndex<VesselPrototype>(msg.Id, out var vessel))
                    return;

                if (msg.Price > int.MaxValue)
                    return;

                basePrice = vessel.Price;
                oldPrice = GetVesselPrice(vessel);
                resetToBase = (int) Math.Round(msg.Price) == vessel.Price;

                _vesselOverrides[msg.Id] = (int) Math.Round(msg.Price);
                if (resetToBase)
                    _vesselOverrides.Remove(msg.Id);
                break;
            default:
                return;
        }

        var category = msg.Category == EconomyListCategory.Items ? "item" : "vessel";
        var newPrice = resetToBase ? basePrice : msg.Price;

        _adminLog.Add(
            LogType.AdminCommands,
            LogImpact.Medium,
            $"{args.SenderSession:player} changed economy {category} price for {msg.Id} from {oldPrice:0.##} to {newPrice:0.##} (base {basePrice:0.##})");

        BroadcastPriceSync();
        // Confirmation is only consumed by the acting admin's economy panel; send it to them rather than
        // broadcasting (which needlessly shipped balances/treasury to every client).
        RaiseNetworkEvent(new EconomyAdminPriceUpdatedEvent
        {
            Category = msg.Category,
            Id = msg.Id,
            Price = newPrice,
        }, args.SenderSession);
    }

    private void BroadcastPriceSync()
    {
        var sync = new EconomyPriceSyncEvent
        {
            ItemOverrides = new Dictionary<string, double>(_itemOverrides),
            VesselOverrides = new Dictionary<string, int>(_vesselOverrides),
        };

        RaiseNetworkEvent(sync);
    }

    private void ApplyItemPriceToWorld(string protoId, double price)
    {
        var staticQuery = EntityQueryEnumerator<StaticPriceComponent, MetaDataComponent>();
        while (staticQuery.MoveNext(out var uid, out var comp, out var meta))
        {
            if (meta.EntityPrototype?.ID != protoId)
                continue;

            comp.Price = price;
            Dirty(uid, comp);
        }

        var stackQuery = EntityQueryEnumerator<StackPriceComponent, MetaDataComponent>();
        while (stackQuery.MoveNext(out var uid, out var comp, out var meta))
        {
            if (meta.EntityPrototype?.ID != protoId)
                continue;

            comp.Price = price;
            Dirty(uid, comp);
        }

        var vendQuery = EntityQueryEnumerator<VendPriceComponent, MetaDataComponent>();
        while (vendQuery.MoveNext(out var uid, out var comp, out var meta))
        {
            if (meta.EntityPrototype?.ID != protoId)
                continue;

            comp.Price = price;
            Dirty(uid, comp);
        }

        var mobQuery = EntityQueryEnumerator<MobPriceComponent, MetaDataComponent>();
        while (mobQuery.MoveNext(out var uid, out var comp, out var meta))
        {
            if (meta.EntityPrototype?.ID != protoId)
                continue;

            comp.Price = price;
            Dirty(uid, comp);
        }
    }

    private List<EconomyPriceEntry> BuildItemEntries(string searchFilter)
    {
        var filter = searchFilter.Trim();
        var entries = new List<EconomyPriceEntry>();

        foreach (var proto in _prototypeManager.EnumeratePrototypes<EntityPrototype>())
        {
            if (!TryGetItemPriceInfo(proto, out var kind, out var basePrice))
                continue;

            var name = proto.Name;
            if (filter.Length > 0
                && !proto.ID.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var current = GetEffectiveItemPrice(proto.ID, basePrice);
            entries.Add(new EconomyPriceEntry(
                proto.ID,
                name,
                EconomyListCategory.Items,
                kind,
                basePrice,
                current));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    private List<EconomyPriceEntry> BuildVesselEntries(string searchFilter)
    {
        var filter = searchFilter.Trim();
        var entries = new List<EconomyPriceEntry>();

        foreach (var vessel in _prototypeManager.EnumeratePrototypes<VesselPrototype>())
        {
            if (filter.Length > 0
                && !vessel.ID.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !vessel.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var current = GetVesselPrice(vessel);
            entries.Add(new EconomyPriceEntry(
                vessel.ID,
                vessel.Name,
                EconomyListCategory.Vessels,
                null,
                vessel.Price,
                current));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    private bool TryGetItemPriceInfo(EntityPrototype proto, out EconomyPriceKind kind, out double basePrice)
    {
        if (proto.Components.TryGetValue(_factory.GetComponentName(typeof(StaticPriceComponent)), out var staticProto))
        {
            kind = EconomyPriceKind.Static;
            basePrice = ((StaticPriceComponent) staticProto.Component).Price;
            return true;
        }

        if (proto.Components.TryGetValue(_factory.GetComponentName(typeof(StackPriceComponent)), out var stackProto))
        {
            kind = EconomyPriceKind.Stack;
            basePrice = ((StackPriceComponent) stackProto.Component).Price;
            return true;
        }

        if (proto.Components.TryGetValue(_factory.GetComponentName(typeof(VendPriceComponent)), out var vendProto))
        {
            kind = EconomyPriceKind.Vend;
            basePrice = ((VendPriceComponent) vendProto.Component).Price;
            return true;
        }

        if (proto.Components.TryGetValue(_factory.GetComponentName(typeof(MobPriceComponent)), out var mobProto))
        {
            kind = EconomyPriceKind.Mob;
            basePrice = ((MobPriceComponent) mobProto.Component).Price;
            return true;
        }

        kind = default;
        basePrice = 0;
        return false;
    }

    private List<EconomyPriceEntry> BuildTreasuryEntries(string searchFilter)
    {
        var filter = searchFilter.Trim();
        var entries = new List<EconomyPriceEntry>();

        var query = EntityQueryEnumerator<StationTradeMarketComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var market, out var meta))
        {
            var name = meta.EntityName;
            var id = uid.Id.ToString();

            if (filter.Length > 0
                && !id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new EconomyPriceEntry(
                id,
                name,
                EconomyListCategory.Treasury,
                null,
                market.TreasuryBalance,
                market.TreasuryBalance));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    private List<EconomyPriceEntry> BuildPlayerEntries(string searchFilter)
    {
        var filter = searchFilter.Trim();
        var entries = new List<EconomyPriceEntry>();

        foreach (var session in _playerManager.Sessions)
        {
            // Players in the round are InGame, not Connected; filtering on Connected alone listed only the lobby.
            if (session.Status is not (SessionStatus.Connected or SessionStatus.InGame))
                continue;

            // Prefer the live bank component on the piloted mob; fall back to the selected character's
            // persisted profile so lobby/ghost/observer players are listed and editable too. Reading the
            // live component alone hid everyone who was not currently controlling a banked mob.
            long balance;
            string charName;
            if (session.AttachedEntity is { } mob && TryComp<BankAccountComponent>(mob, out var bank))
            {
                balance = bank.Balance;
                charName = Comp<MetaDataComponent>(mob).EntityName;
            }
            else if (_prefs.TryGetCachedPreferences(session.UserId, out var prefs)
                     && prefs.SelectedCharacter is HumanoidCharacterProfile profile)
            {
                balance = profile.BankBalance;
                charName = profile.Name;
            }
            else
            {
                continue;
            }

            var name = $"{charName} [{session.Name}]";
            var id = session.UserId.ToString();

            if (filter.Length > 0
                && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !session.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new EconomyPriceEntry(
                id,
                name,
                EconomyListCategory.Players,
                null,
                balance,
                balance));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    private List<EconomyPriceEntry> BuildStockEntries(string searchFilter)
    {
        var filter = searchFilter.Trim();
        var entries = new List<EconomyPriceEntry>();

        foreach (var company in _stocks.GetCompanies())
        {
            var name = Loc.GetString(company.Id);

            if (filter.Length > 0
                && !company.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Market share is what the admin actually wants to see; it is the underlying quantity and
            // it is the thing that has to add up across the whole list.
            var status = company.Active ? string.Empty : " [delisted]";
            entries.Add(new EconomyPriceEntry(
                company.Id,
                $"{name} — {company.Share * 100f:0.0}% share{status}",
                EconomyListCategory.Stocks,
                null,
                company.BasePrice,
                Math.Round(company.CurrentPrice, 2)));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    private void SetStockPrice(EconomyAdminSetPriceEvent msg, ICommonSession session)
    {
        var company = _stocks.GetCompany(msg.Id);
        if (company == null)
            return;

        var oldPrice = company.CurrentPrice;
        if (!_stocks.SetPrice(msg.Id, msg.Price))
            return;

        _adminLog.Add(
            LogType.AdminCommands,
            LogImpact.High,
            $"{session:player} set stock {msg.Id} price from {oldPrice:0.00} to {company.CurrentPrice:0.00} cr");

        RaiseNetworkEvent(new EconomyAdminPriceUpdatedEvent
        {
            Category = EconomyListCategory.Stocks,
            Id = msg.Id,
            Price = Math.Round(company.CurrentPrice, 2),
        }, session);
    }

    private void SetTreasuryBalance(EconomyAdminSetPriceEvent msg, ICommonSession session)
    {
        if (msg.Price > int.MaxValue)
            return;

        var newBalance = (int) Math.Round(msg.Price);

        var query = EntityQueryEnumerator<StationTradeMarketComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var market, out var meta))
        {
            if (uid.Id.ToString() != msg.Id)
                continue;

            var oldBalance = market.TreasuryBalance;
            // Route through the market system so the change is mirrored into the cross-round store.
            _market.SetTreasury(uid, newBalance);
            // Push the new balance into any open treasury console for this station; the console only
            // refreshes on its own actions, so without this an open UI keeps showing the old value.
            _treasuryConsole.RefreshStationConsoles(uid);

            _adminLog.Add(
                LogType.AdminCommands,
                LogImpact.High,
                $"{session:player} set {meta.EntityName} faction treasury from {oldBalance} to {newBalance} cr");

            RaiseNetworkEvent(new EconomyAdminPriceUpdatedEvent
            {
                Category = EconomyListCategory.Treasury,
                Id = msg.Id,
                Price = newBalance,
            }, session);
            return;
        }
    }

    private void SetPlayerBalance(EconomyAdminSetPriceEvent msg, ICommonSession session)
    {
        var newBalance = (long) Math.Round(msg.Price);
        if (newBalance < 0)
            return;

        foreach (var target in _playerManager.Sessions)
        {
            if (target.UserId.ToString() != msg.Id)
                continue;

            long oldBalance;

            if (target.AttachedEntity is { } mob && TryComp<BankAccountComponent>(mob, out var bank))
            {
                // Live mob: BankSystem mirrors the component change back into the saved profile.
                oldBalance = bank.Balance;
                if (!_bank.TrySetBankBalance(mob, newBalance))
                    return;
            }
            else if (_prefs.TryGetCachedPreferences(target.UserId, out var prefs)
                     && prefs.SelectedCharacter is HumanoidCharacterProfile profile)
            {
                // No live account (lobby/ghost): write the selected character's persisted profile so the
                // change sticks and is applied when they next spawn.
                var index = prefs.IndexOfCharacter(profile);
                if (index < 0)
                    return;

                oldBalance = profile.BankBalance;
                _prefs.SetProfileNoChecks(target.UserId, index, profile.WithBank(newBalance));
            }
            else
            {
                return;
            }

            _adminLog.Add(
                LogType.AdminCommands,
                LogImpact.High,
                $"{session:player} set {target.Name}'s bank balance from {oldBalance} to {newBalance} cr");

            RaiseNetworkEvent(new EconomyAdminPriceUpdatedEvent
            {
                Category = EconomyListCategory.Players,
                Id = msg.Id,
                Price = newBalance,
            }, session);
            return;
        }
    }

    private bool IsEconomyAdmin(ICommonSession session) =>
        _adminManager.HasAdminFlag(session, AdminFlags.Admin);
}
