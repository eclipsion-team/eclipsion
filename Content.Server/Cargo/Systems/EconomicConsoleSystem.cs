using System.Linq;
using Content.Shared._Crescent.Mainframe;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Components;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.Cargo.Systems;

public sealed class EconomicConsoleSystem : SharedEconomicConsoleSystem
{
    [Dependency] private readonly DynamicPricingSystem _dynamicPricing = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;

    private readonly record struct Good(string Id, string Name, string SearchId, string SearchName);

    // sorted by id, built once the goods are known
    private Good[] _goods = [];
    private int _indexedGoods = -1;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EconomicConsoleComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<EconomicConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<EconomicConsoleComponent, EconomicConsoleSearchMessage>(OnSearch);
    }

    private void OnActivate(Entity<EconomicConsoleComponent> ent, ref ActivateInWorldEvent args)
    {
        // On a mainframe, ActivatableUI owns the interaction and opens every tab's key. Handling it
        // here too would race with it: whichever system subscribed first wins, and if it's this one
        // the mainframe never opens at all.
        if (HasComp<MainframeConsoleComponent>(ent))
            return;

        args.Handled = true;

        var user = args.User;

        _uiSystem.OpenUi(ent.Owner, EconomicConsoleUiKey.Key, user);
        SendBuiState(ent);
    }

    // The mainframe path never reaches OnActivate, so this is where its state comes from.
    private void OnUiOpened(Entity<EconomicConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!Equals(args.UiKey, EconomicConsoleUiKey.Key))
            return;

        // the panel opens with an empty search box
        ent.Comp.Search = string.Empty;
        SendBuiState(ent);
    }

    private void OnSearch(Entity<EconomicConsoleComponent> ent, ref EconomicConsoleSearchMessage args)
    {
        var search = args.Search.Trim();
        if (search.Length > EconomicConsoleSearchMessage.MaxLength)
            search = search[..EconomicConsoleSearchMessage.MaxLength];

        ent.Comp.Search = search.ToLowerInvariant();
        SendBuiState(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<EconomicConsoleComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (!component.AutoUpdateEnabled)
                continue;

            component.LastUpdateTimer += frameTime;

            if (component.LastUpdateTimer >= component.RefreshInterval)
            {
                component.LastUpdateTimer = 0f;

                if (_uiSystem.TryGetOpenUi(uid, EconomicConsoleUiKey.Key, out _))
                {
                    SendBuiState((uid, component));
                }
            }
        }
    }

    private string GetLocalizedName(string prototypeId)
    {
        if (_prototype.TryIndex<EntityPrototype>(prototypeId, out var proto))
        {
            var key = proto.Name;
            if (!string.IsNullOrEmpty(key))
                return Loc.GetString(key);
        }
        return prototypeId;
    }

    private Good[] GetGoods()
    {
        var goods = _dynamicPricing.GetGoods();
        if (goods.Count == _indexedGoods)
            return _goods;

        _indexedGoods = goods.Count;
        _goods = goods
            .Select(id =>
            {
                var name = GetLocalizedName(id);
                return new Good(id, name, id.ToLowerInvariant(), name.ToLowerInvariant());
            })
            .OrderBy(good => good.Id, StringComparer.Ordinal)
            .ToArray();

        return _goods;
    }

    private void SendBuiState(Entity<EconomicConsoleComponent> ent)
    {
        var search = ent.Comp.Search;
        var goods = GetGoods();
        var items = new List<ItemPriceData>();
        var matching = 0;

        foreach (var good in goods)
        {
            if (search.Length > 0 && !good.SearchId.Contains(search) && !good.SearchName.Contains(search))
                continue;

            matching++;
            if (items.Count >= EconomicConsoleBuiState.MaxItems ||
                !_dynamicPricing.TryGetPrice(good.Id, out var basePrice, out var multiplier))
            {
                continue;
            }

            items.Add(new ItemPriceData(good.Id, good.Name, basePrice, basePrice * multiplier, multiplier));
        }

        var shuttlePricesRaw = _dynamicPricing.GetAllShuttlePrices();
        var shuttles = new List<ShuttlePriceInfoData>();

        foreach (var info in shuttlePricesRaw.Values)
        {
            if (search.Length > 0 && !info.Name.ToLowerInvariant().Contains(search))
                continue;

            var priceChange = info.BasePrice > 0
                ? (float)(info.CurrentPrice - info.BasePrice) / info.BasePrice
                : 0f;
            shuttles.Add(new ShuttlePriceInfoData(info.Name, info.BasePrice, info.CurrentPrice, priceChange));
        }

        shuttles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var state = new EconomicConsoleBuiState(items, matching, shuttles, goods.Length + shuttlePricesRaw.Count);
        _uiSystem.SetUiState(ent.Owner, EconomicConsoleUiKey.Key, state);
    }
}
