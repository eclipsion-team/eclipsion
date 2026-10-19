using System.Linq;
using Content.Server.Crescent.Dispenser;
using Content.Shared._Crescent.Taxation;
using Content.Shared.Access.Systems;
using Content.Shared.Crescent.Dispenser;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Taxation;

/// <summary>
/// Backs the taxation console: lets an authorized faction member set the percentage tax
/// applied to trade goods sold through the station's trade points. Base prices are never
/// touched here — only the tax cut, stored on the station's <see cref="StationTradeMarketComponent"/>.
/// </summary>
public sealed class TaxationConsoleSystem : EntitySystem
{
    [Dependency] private readonly StationTradeMarketSystem _market = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>How often an open console re-reads the treasury and goods list.</summary>
    private const float RefreshInterval = 3f;

    /// <summary>How long a station's built goods list stays reusable before it is rebuilt.</summary>
    private static readonly TimeSpan GoodsCacheTtl = TimeSpan.FromSeconds(15);

    private float _sinceRefresh;

    /// <summary>
    /// Per-station goods lists, memoised.
    /// </summary>
    /// <remarks>
    /// Building one walks every dispenser in the world and resolves each one's owning station (a
    /// transform-parent walk), which was happening on every open, every button press, and now every
    /// refresh tick. What a station sells barely changes within a round, so the list is rebuilt on a
    /// timer and whenever a rate edit makes the cached effective rates wrong. A rate edit clears every
    /// station's list, not just the edited one: rates are per faction, so the edit lands on all of its stations.
    /// </remarks>
    private readonly Dictionary<EntityUid, (TimeSpan Built, List<TaxableGoodEntry> Goods)> _goodsCache = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TaxationConsoleComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<TaxationConsoleComponent, TaxationSetDefaultRateMessage>(OnSetDefault);
        SubscribeLocalEvent<TaxationConsoleComponent, TaxationSetOverrideMessage>(OnSetOverride);
        SubscribeLocalEvent<TaxationConsoleComponent, TaxationClearOverrideMessage>(OnClearOverride);
    }

    private void OnOpened(EntityUid uid, TaxationConsoleComponent comp, BoundUIOpenedEvent args)
    {
        UpdateUi(uid);
    }

    /// <summary>
    /// Keeps an open console current. The treasury figure it shows moves constantly — trade tax, drone
    /// and shipyard purchases, payroll — but the console only ever redrew in response to its own
    /// buttons, so the balance froze at whatever it was when the operator last pressed something.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _sinceRefresh += frameTime;
        if (_sinceRefresh < RefreshInterval)
            return;

        _sinceRefresh = 0f;

        var query = EntityQueryEnumerator<TaxationConsoleComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_ui.IsUiOpen(uid, TaxationConsoleUiKey.Key))
                UpdateUi(uid);
        }
    }

    private void OnSetDefault(EntityUid uid, TaxationConsoleComponent comp, TaxationSetDefaultRateMessage args)
    {
        if (!TryEdit(uid, comp, args.Actor, out var station))
            return;

        _market.SetDefaultTaxRate(station, args.Rate);
        _goodsCache.Clear();
        UpdateUi(uid);
    }

    private void OnSetOverride(EntityUid uid, TaxationConsoleComponent comp, TaxationSetOverrideMessage args)
    {
        if (!TryEdit(uid, comp, args.Actor, out var station))
            return;

        _market.SetTaxOverride(station, args.ProtoId, args.Rate);
        _goodsCache.Clear();
        UpdateUi(uid);
    }

    private void OnClearOverride(EntityUid uid, TaxationConsoleComponent comp, TaxationClearOverrideMessage args)
    {
        if (!TryEdit(uid, comp, args.Actor, out var station))
            return;

        _market.ClearTaxOverride(station, args.ProtoId);
        _goodsCache.Clear();
        UpdateUi(uid);
    }

    /// <summary>Validates edit access and resolves the owning station.</summary>
    private bool TryEdit(EntityUid uid, TaxationConsoleComponent comp, EntityUid actor, out EntityUid station)
    {
        station = default;

        if (!_access.IsAllowed(actor, uid))
        {
            _popup.PopupEntity(Loc.GetString("taxation-console-access-denied"), uid, actor, PopupType.MediumCaution);
            return false;
        }

        var owning = _market.TryGetOwningStation(uid);
        if (owning is null)
            return false;

        station = owning.Value;
        return true;
    }

    /// <summary>
    /// Pushes fresh state to the console's viewer.
    /// </summary>
    /// <remarks>
    /// <c>CanEdit</c> is a per-player answer but <c>SetUiState</c> is shared by every viewer of a key,
    /// with no per-actor overload. The console is therefore <c>singleUser</c> — the same reason the
    /// mainframe is — and the flag is computed from whoever currently holds it rather than from
    /// whoever triggered this update. Without that, a command member opening a console someone else
    /// was already reading handed them an unlocked rate field; the writes were still refused
    /// server-side, but the UI said otherwise, so the buttons argued with the access-denied popup.
    /// </remarks>
    private void UpdateUi(EntityUid uid)
    {
        var station = _market.TryGetOwningStation(uid);
        if (station is null || !TryComp<StationTradeMarketComponent>(station, out var market))
            return;

        var viewer = _ui.GetActors(uid, TaxationConsoleUiKey.Key).FirstOrDefault();
        var canEdit = viewer != default && _access.IsAllowed(viewer, uid);

        _ui.SetUiState(uid, TaxationConsoleUiKey.Key, new TaxationConsoleState(
            market.DefaultTaxRate,
            market.MaxTaxRate,
            _market.GetTreasury(station.Value),
            canEdit,
            GetGoodsList(station.Value, market)));
    }

    /// <summary>Cached view of <see cref="BuildGoodsList"/>. See <see cref="_goodsCache"/>.</summary>
    private List<TaxableGoodEntry> GetGoodsList(EntityUid station, StationTradeMarketComponent market)
    {
        var now = _timing.CurTime;

        if (_goodsCache.TryGetValue(station, out var cached) && now - cached.Built < GoodsCacheTtl)
            return cached.Goods;

        var goods = BuildGoodsList(station, market);
        _goodsCache[station] = (now, goods);
        return goods;
    }

    /// <summary>
    /// Collects every trade good sold through any trade point (dispenser) on this station,
    /// with its base price and the tax rate currently in effect.
    /// </summary>
    private List<TaxableGoodEntry> BuildGoodsList(EntityUid station, StationTradeMarketComponent market)
    {
        // Union the base prices from every dispenser belonging to this station.
        var basePrices = new Dictionary<string, int>();
        var query = EntityQueryEnumerator<DispenserComponent>();
        while (query.MoveNext(out var dispenserUid, out var dispenser))
        {
            if (dispenser.DynamicInventory.Count == 0)
                continue;

            if (_market.TryGetOwningStation(dispenserUid) != station)
                continue;

            foreach (var (protoId, price) in dispenser.DynamicInventory)
            {
                // Keep the highest listed base price if multiple trade points differ.
                if (!basePrices.TryGetValue(protoId, out var existing) || price > existing)
                    basePrices[protoId] = price;
            }
        }

        var result = new List<TaxableGoodEntry>(basePrices.Count);
        foreach (var (protoId, price) in basePrices)
        {
            var name = _proto.TryIndex<EntityPrototype>(protoId, out var proto) ? proto.Name : protoId;
            var hasOverride = market.TaxOverrides.ContainsKey(protoId);

            result.Add(new TaxableGoodEntry
            {
                ProtoId = protoId,
                Name = name,
                BasePrice = price,
                EffectiveRate = _market.GetTaxRate(station, protoId),
                HasOverride = hasOverride,
            });
        }

        return result.OrderByDescending(g => g.BasePrice).ToList();
    }
}
