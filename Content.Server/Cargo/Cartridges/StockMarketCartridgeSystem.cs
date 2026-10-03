using Content.Server.Bank;
using Content.Server._Crescent.Economy;
using Content.Server.Cargo.Systems;
using Content.Server.CartridgeLoader;
using Content.Shared.Bank.Components;
using Content.Shared.Cargo.Cartridges;
using Content.Shared.Cargo.Components;
using Content.Shared.CartridgeLoader;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Cargo.Cartridges;

public sealed class StockMarketCartridgeSystem : EntitySystem
{
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private readonly StockCompanySystem _stockCompanies = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly StockPortfolioPersistenceSystem _portfolios = default!;
    [Dependency] private readonly StockWarLiquidationSystem _warLiquidation = default!;
    [Dependency] private readonly ISharedPlayerManager _playerManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StockMarketCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<StockMarketCartridgeComponent, CartridgeMessageEvent>(OnMessage);
        SubscribeLocalEvent<StockCompaniesUpdatedEvent>(OnPricesUpdated);
    }

    private void OnPricesUpdated(ref StockCompaniesUpdatedEvent ev)
    {
        var query = EntityQueryEnumerator<StockMarketCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out var uid, out _, out var cartridge))
        {
            if (cartridge.LoaderUid is not { } loader)
                continue;

            if (TryComp<CartridgeLoaderComponent>(loader, out var loaderComp) &&
                loaderComp.ActiveProgram != uid)
                continue;

            SendUiState(uid, loader, GetLoaderMob(loader));
        }
    }

    private void OnUiReady(EntityUid uid, StockMarketCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        var playerUid = GetLoaderMob(args.Loader);
        SendUiState(uid, args.Loader, playerUid);
    }

    private void OnMessage(EntityUid uid, StockMarketCartridgeComponent component, CartridgeMessageEvent args)
    {
        if (args is not StockMarketUiMessageEvent msg)
            return;

        HandleUiMessage(uid, args, msg);
    }

    private void HandleUiMessage(EntityUid uid, CartridgeMessageEvent args, StockMarketUiMessageEvent msg)
    {
        var playerUid = args.Actor;
        var loaderUid = GetEntity(args.LoaderUid);

        if (playerUid == default)
            return;

        switch (msg.Action)
        {
            case StockMarketUiAction.RequestPrices:
                SendUiState(uid, loaderUid, playerUid);
                break;

            case StockMarketUiAction.Buy:
                TryBuyStock(playerUid, msg.CompanyId ?? "", msg.Amount);
                SendUiState(uid, loaderUid, playerUid);
                break;

            case StockMarketUiAction.Sell:
                TrySellStock(playerUid, msg.CompanyId ?? "", msg.Amount);
                SendUiState(uid, loaderUid, playerUid);
                break;
        }
    }

    public bool TryBuyStock(EntityUid playerUid, string companyId, int amount)
    {
        if (amount <= 0 || amount > StockMarketTrading.MaxStockAmount)
            return false;

        if (!_warLiquidation.CanTradeStock(playerUid, companyId))
            return false;

        var company = _stockCompanies.GetCompany(companyId);
        if (company is not { Active: true })
            return false;

        var pricePerShare = company.CurrentPrice;
        // Crescent: buy rounds up and sell rounds down. With Math.Round (banker's rounding) buying one share at
        // a time and selling in bulk was a risk-free arbitrage. Computed in double/long so a large order can't
        // overflow into a negative cost.
        var exactCost = Math.Ceiling((double) pricePerShare * amount);
        if (!double.IsFinite(exactCost) || exactCost <= 0 || exactCost > int.MaxValue)
            return false;

        var cost = (int) exactCost;
        if (!_bank.TryBankWithdraw(playerUid, cost))
            return false;

        var portfolio = EnsureComp<PlayerStockPortfolioComponent>(playerUid);
        if (!portfolio.OwnedShares.TryGetValue(companyId, out var current))
            current = 0;
        portfolio.OwnedShares[companyId] = current + amount;
        portfolio.TotalInvested += cost;
        RecordTrade(portfolio, companyId, amount, pricePerShare, isBuy: true);
        Dirty(playerUid, portfolio);
        _portfolios.Store(playerUid, portfolio);

        return true;
    }

    public bool TrySellStock(EntityUid playerUid, string companyId, int amount)
    {
        if (amount <= 0 || amount > StockMarketTrading.MaxStockAmount)
            return false;

        if (!_warLiquidation.CanTradeStock(playerUid, companyId))
            return false;

        if (!TryComp<PlayerStockPortfolioComponent>(playerUid, out var portfolio))
            return false;

        if (!portfolio.OwnedShares.TryGetValue(companyId, out var owned) || owned < amount)
            return false;

        var company = _stockCompanies.GetCompany(companyId);
        if (company is not { Active: true })
            return false;

        var pricePerShare = company.CurrentPrice;
        var exactProfit = Math.Floor((double) pricePerShare * amount);
        if (!double.IsFinite(exactProfit) || exactProfit < 0 || exactProfit > long.MaxValue / 2)
            return false;

        var profit = (long) exactProfit;
        if (profit > 0 && !_bank.TryBankDeposit(playerUid, profit))
            return false;

        var newOwned = owned - amount;
        if (newOwned <= 0)
            portfolio.OwnedShares.Remove(companyId);
        else
            portfolio.OwnedShares[companyId] = newOwned;
        RecordTrade(portfolio, companyId, amount, pricePerShare, isBuy: false);
        Dirty(playerUid, portfolio);
        _portfolios.Store(playerUid, portfolio);

        return true;
    }

    private void RecordTrade(PlayerStockPortfolioComponent portfolio, string companyId, int amount, double price, bool isBuy)
    {
        portfolio.TradeHistory.Add(new StockTradeRecord(companyId, amount, price, isBuy, _timing.CurTime));
        while (portfolio.TradeHistory.Count > PlayerStockPortfolioComponent.MaxTradeHistory)
            portfolio.TradeHistory.RemoveAt(0);
    }

    private EntityUid? GetLoaderMob(EntityUid loaderUid)
    {
        var uid = loaderUid;
        for (var i = 0; i < 8; i++)
        {
            var parent = Transform(uid).ParentUid;
            if (!parent.IsValid())
                break;
            if (_playerManager.TryGetSessionByEntity(parent, out _))
                return parent;
            uid = parent;
        }
        return null;
    }

    private void SendUiState(EntityUid cartridgeUid, EntityUid loaderUid, EntityUid? playerUid)
    {
        var priceData = new Dictionary<string, StockPriceData>();
        var portfolio = new Dictionary<string, int>();
        var history = new List<StockTradeRecord>();
        long? balance = null;

        var companies = _stockCompanies.GetCompanies();
        foreach (var company in companies)
        {
            // A faction that is not fielded this round is delisted: its price is frozen and meaningless,
            // so quoting it would only invite trades that cannot be settled against anything real.
            // Existing holdings are untouched and still show up under the portfolio tab.
            if (!company.Active)
                continue;

            var (pendingShift, pendingTicks) = _stockCompanies.GetPendingPressure(company);

            priceData[company.Id] = new StockPriceData(
                company.Id,
                company.BasePrice,
                company.CurrentPrice,
                company.PriceMultiplier,
                company.PriceChange,
                new List<float>(company.PriceHistory),
                pendingShift,
                pendingTicks,
                company.Neutral
            );
        }

        if (playerUid != null)
        {
            if (TryComp<PlayerStockPortfolioComponent>(playerUid.Value, out var portfolioComp))
            {
                portfolio = new Dictionary<string, int>(portfolioComp.OwnedShares);
                history = new List<StockTradeRecord>(portfolioComp.TradeHistory);
            }

            if (TryComp<BankAccountComponent>(playerUid.Value, out var bank))
                balance = bank.Balance;
        }

        var news = new List<StockNewsRecord>(_stockCompanies.GetNews());
        var state = new StockMarketUiState(priceData, portfolio, balance, history, news);
        _cartridgeLoader.UpdateCartridgeUiState(loaderUid, state);
    }
}
