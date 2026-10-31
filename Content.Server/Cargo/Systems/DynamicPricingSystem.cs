using Content.Server.Cargo.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Cargo.Systems;

public sealed class DynamicPricingSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ShuttlePricingSystem _shuttlePricing = default!;

    private readonly Dictionary<string, DynamicPriceComponent> _globalPrices = new();
    private readonly List<string> _goodsList = new();
    private int _currentBatchIndex = 0;
    private const int BatchSize = 50;
    
    private readonly Dictionary<string, TransactionHistory> _transactionHistory = new();
    private const float TransactionDecayRate = 0.95f;
    private const int TransactionHistoryWindow = 100;
    
    private TimeSpan _lastBatchUpdate;
    private const float BatchUpdateInterval = 2f;
    
    private bool _initialized;

    public override void Initialize()
    {
        base.Initialize();
        _lastBatchUpdate = _timing.CurTime;
        
        SubscribeLocalEvent<DynamicPriceComponent, PriceCalculationEvent>(OnPriceCalculation);
    }

    private void InitializeAllGoods()
    {
        if (_initialized)
            return;
        _initialized = true;

        foreach (var proto in _prototype.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.Abstract)
                continue;

            if (proto.TryGetComponent<StaticPriceComponent>(out var staticPrice) && staticPrice.Price > 0)
            {
                if (!_globalPrices.ContainsKey(proto.ID))
                {
                    _globalPrices[proto.ID] = new DynamicPriceComponent
                    {
                        BasePrice = staticPrice.Price
                    };
                    _goodsList.Add(proto.ID);
                }
            }
        }

        Log.Info($"[DynamicPricing] Initialized {_globalPrices.Count} tradeable goods");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_initialized)
            InitializeAllGoods();

        if (_goodsList.Count == 0)
            return;

        var curTime = _timing.CurTime;
        
        if ((curTime - _lastBatchUpdate).TotalSeconds >= BatchUpdateInterval)
        {
            UpdateBatch();
            DecayTransactionHistory();
            _lastBatchUpdate = curTime;
        }

        var query = EntityQueryEnumerator<DynamicPriceComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            component.LastUpdateTimer += frameTime;
            
            if (component.LastUpdateTimer >= component.UpdateInterval)
            {
                UpdateEntityPrice(uid, component);
                component.LastUpdateTimer = 0f;
            }
        }
    }

    private void DecayTransactionHistory()
    {
        foreach (var (_, history) in _transactionHistory)
        {
            history.SoldCount = (int)(history.SoldCount * TransactionDecayRate);
            history.BoughtCount = (int)(history.BoughtCount * TransactionDecayRate);
        }
    }

    private void UpdateBatch()
    {
        if (_goodsList.Count == 0)
            return;

        var batchEnd = Math.Min(_currentBatchIndex + BatchSize, _goodsList.Count);
        
        for (var i = _currentBatchIndex; i < batchEnd; i++)
        {
            var goodId = _goodsList[i];
            if (!_globalPrices.TryGetValue(goodId, out var priceComp))
                continue;

            var randomChange = _random.NextFloat(-0.05f, 0.05f);
            var demandAdjustment = CalculateDemandAdjustment(goodId);
            var totalChange = randomChange + demandAdjustment;
            
            priceComp.PriceMultiplier += totalChange;
            priceComp.PriceMultiplier = Math.Clamp(priceComp.PriceMultiplier, 
                priceComp.MinMultiplier, priceComp.MaxMultiplier);

            priceComp.PriceMultiplier += (1.0f - priceComp.PriceMultiplier) * priceComp.RecoveryRate;
            priceComp.PriceDeviation = priceComp.PriceMultiplier - 1.0f;
        }

        _currentBatchIndex = batchEnd >= _goodsList.Count ? 0 : batchEnd;
    }

    private float CalculateDemandAdjustment(string goodId)
    {
        if (!_transactionHistory.TryGetValue(goodId, out var history))
            return 0f;

        var totalTransactions = history.SoldCount + history.BoughtCount;
        if (totalTransactions == 0)
            return -0.005f;

        var sellRatio = (float)history.SoldCount / totalTransactions;
        var buyRatio = (float)history.BoughtCount / totalTransactions;
        
        var volumeScale = Math.Min(totalTransactions / 10f, 1f);
        
        var adjustment = (sellRatio - buyRatio) * 0.1f * volumeScale;
        
        return Math.Clamp(adjustment, -0.08f, 0.08f);
    }

    private void UpdateEntityPrice(EntityUid uid, DynamicPriceComponent component)
    {
        var change = _random.NextFloat(-0.05f, 0.05f);
        component.PriceMultiplier += change;
        component.PriceMultiplier = Math.Clamp(component.PriceMultiplier, 
            component.MinMultiplier, component.MaxMultiplier);

        component.PriceMultiplier += (1.0f - component.PriceMultiplier) * component.RecoveryRate;
        component.PriceDeviation = component.PriceMultiplier - 1.0f;
    }

    private void OnPriceCalculation(EntityUid uid, DynamicPriceComponent component, ref PriceCalculationEvent args)
    {
        if (component.BasePrice > 0)
        {
            args.Price = component.BasePrice * component.PriceMultiplier;
            args.Handled = true;
        }
    }

    public float GetPriceMultiplier(string goodId)
    {
        if (_globalPrices.TryGetValue(goodId, out var priceComp))
            return priceComp.PriceMultiplier;
        
        return 1.0f;
    }

    public double GetCurrentPrice(string goodId, double basePrice)
    {
        return basePrice * GetPriceMultiplier(goodId);
    }

    public void RecordSell(string goodId, int amount)
    {
        if (!_transactionHistory.TryGetValue(goodId, out var history))
        {
            history = new TransactionHistory();
            _transactionHistory[goodId] = history;
        }

        history.BoughtCount += amount;
        history.TotalTransactions++;

        if (_globalPrices.TryGetValue(goodId, out var priceComp))
        {
            var impact = -amount * 0.01f;
            priceComp.PriceMultiplier += impact;
            priceComp.PriceMultiplier = Math.Clamp(priceComp.PriceMultiplier, 
                priceComp.MinMultiplier, priceComp.MaxMultiplier);
        }
    }

    public void RecordBuy(string goodId, int amount)
    {
        if (!_transactionHistory.TryGetValue(goodId, out var history))
        {
            history = new TransactionHistory();
            _transactionHistory[goodId] = history;
        }

        history.SoldCount += amount;
        history.TotalTransactions++;

        if (_globalPrices.TryGetValue(goodId, out var priceComp))
        {
            var impact = amount * 0.015f;
            priceComp.PriceMultiplier += impact;
            priceComp.PriceMultiplier = Math.Clamp(priceComp.PriceMultiplier, 
                priceComp.MinMultiplier, priceComp.MaxMultiplier);
        }
    }

    public void RecordTransaction(string goodId, int amount, bool isBuy)
    {
        if (isBuy)
            RecordBuy(goodId, amount);
        else
            RecordSell(goodId, amount);
    }

    public Dictionary<string, float> GetAllPrices()
    {
        if (!_initialized)
            InitializeAllGoods();

        var result = new Dictionary<string, float>();
        foreach (var (goodId, priceComp) in _globalPrices)
        {
            result[goodId] = priceComp.PriceMultiplier;
        }
        return result;
    }

    public Dictionary<string, (float Multiplier, float Deviation)> GetAllPricesWithDeviation()
    {
        if (!_initialized)
            InitializeAllGoods();

        var result = new Dictionary<string, (float, float)>();
        foreach (var (goodId, priceComp) in _globalPrices)
        {
            result[goodId] = (priceComp.PriceMultiplier, priceComp.PriceDeviation);
        }
        return result;
    }

    public IReadOnlyList<string> GetGoods()
    {
        if (!_initialized)
            InitializeAllGoods();

        return _goodsList;
    }

    public bool TryGetPrice(string goodId, out double basePrice, out float multiplier)
    {
        basePrice = 0;
        multiplier = 1f;

        if (!_globalPrices.TryGetValue(goodId, out var priceComp))
            return false;

        basePrice = priceComp.BasePrice;
        multiplier = priceComp.PriceMultiplier;
        return true;
    }

    public Dictionary<string, (double BasePrice, float Multiplier, double CurrentPrice)> GetAllPricesFull()
    {
        if (!_initialized)
            InitializeAllGoods();

        var result = new Dictionary<string, (double, float, double)>();
        foreach (var (goodId, priceComp) in _globalPrices)
        {
            var currentPrice = priceComp.BasePrice * priceComp.PriceMultiplier;
            result[goodId] = (priceComp.BasePrice, priceComp.PriceMultiplier, currentPrice);
        }
        return result;
    }

    public Dictionary<string, ShuttlePriceInfo> GetAllShuttlePrices()
    {
        var result = new Dictionary<string, ShuttlePriceInfo>();
        
        var query = EntityQueryEnumerator<ShuttlePricingComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            var name = MetaData(uid).EntityName;
            if (string.IsNullOrEmpty(name))
                name = $"Shuttle-{uid}";

            result[$"{uid}"] = new ShuttlePriceInfo(
                name,
                component.BasePrice,
                component.CurrentPrice,
                new List<int>(component.PriceHistory)
            );
        }
        
        return result;
    }

    public void ForceUpdatePrices()
    {
        if (!_initialized)
            InitializeAllGoods();

        _currentBatchIndex = 0;
        var fullCycles = (int)Math.Ceiling((double)_goodsList.Count / BatchSize);
        for (var i = 0; i < fullCycles; i++)
        {
            UpdateBatch();
        }
        
        var query = EntityQueryEnumerator<DynamicPriceComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            UpdateEntityPrice(uid, component);
            component.LastUpdateTimer = 0f;
        }
    }

    public void ResetAllPrices()
    {
        foreach (var (_, priceComp) in _globalPrices)
        {
            priceComp.PriceMultiplier = 1.0f;
            priceComp.PriceDeviation = 0f;
        }
        
        _transactionHistory.Clear();
    }
}

public sealed class TransactionHistory
{
    public int SoldCount;
    public int BoughtCount;
    public int TotalTransactions;
}

public record struct ShuttlePriceInfo(
    string Name,
    int BasePrice,
    int CurrentPrice,
    List<int> PriceHistory
);
