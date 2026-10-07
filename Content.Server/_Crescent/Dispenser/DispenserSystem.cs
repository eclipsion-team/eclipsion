using System.Text;
using Content.Server.Body.Components;
using Content.Server.Paper;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Contractors.Components;
using Content.Shared._Crescent.Mind;
using Content.Shared.Body.Part;
using Content.Shared.Crescent.Dispenser;
using Content.Shared.Interaction;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Popups;
using Content.Shared._Crescent.Dispenser;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.VendingMachines;
using Content.Server.Cargo.Systems;
using Content.Server.Station.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Crescent.Dispenser;

public sealed class DispenserSystem : SharedDispenserSystem
{
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItemSystem = default!;
    [Dependency] private readonly StationTradeMarketSystem _marketSystem = default!;
    [Dependency] private readonly Stack.StackSystem _stackSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly DynamicPricingSystem _dynamicPricing = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly PaperSystem _paper = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;

    private const string RecordPrintoutPrototype = "PaperPassportRecord";
    internal const float MinimumTradePayoutMultiplier = 0.5f;

    /// <summary>
    /// Cheapest cargo purchase price per product entity-prototype id, built once from every
    /// <see cref="CargoProductPrototype"/>. Used to cap trade-market payouts so an item bought
    /// from cargo can never be sold back for more than it cost — closing the arbitrage loop.
    /// </summary>
    private Dictionary<string, int>? _cargoBuyPrices;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DispenserComponent, ActivateInWorldEvent>(OnActivateInWorld);
        SubscribeLocalEvent<DispenserComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnActivateInWorld(EntityUid uid, DispenserComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled || component.Dispensing)
            return;

        // Cargo chutes used to spit out a paper trade-deed stub when bumped with an empty hand.
        // Instead of handing out free paper, mock the player for trying to sell their bare hand.
        if (component.DefaultItem == "TradeDeedStub")
        {
            args.Handled = true;
            _popup.PopupEntity(
                Loc.GetString("dispenser-empty-hand-deny"),
                uid, args.User, PopupType.MediumCaution);
            _audioSystem.PlayPvs(component.DenySound, uid);
            return;
        }

        if (!string.IsNullOrEmpty(component.DefaultItem))
        {
            args.Handled = true;
            TryDispenseItem(uid, component, component.DefaultItem);
        }
        else
        {
            _audioSystem.PlayPvs(component.DenySound, uid);
        }
    }

    private void OnInteractUsing(EntityUid uid, DispenserComponent component, InteractUsingEvent args)
    {
        if (args.Handled || component.Dispensing)
            return;

        EntityUid used;
        if (TryComp<VirtualItemComponent>(args.Used, out var virtualItem))
            used = virtualItem.BlockingEntity;
        else
            used = args.Used;

        // Modern passports carry structured identity and issuer data. A checker reads that data
        // in place so checking a document no longer destroys it. The legacy prototype mappings
        // below are intentionally left intact for old bare legit/fake passports.
        if (HasComp<PassportCheckerComponent>(uid)
            && TryComp<PassportComponent>(used, out var passport))
        {
            args.Handled = true;

            if (!_power.IsPowered(uid))
            {
                _popup.PopupEntity(Loc.GetString("passport-checker-no-power"), uid, args.User,
                    PopupType.MediumCaution);
                _audioSystem.PlayPvs(component.DenySound, uid);
                return;
            }

            PrintPassportRecord(uid, component, passport, args.User);
            return;
        }

        // Check if the dispenser is HuntersBounty and validate the head
        if (TryComp<MetaDataComponent>(uid, out var meta) &&
            meta.EntityPrototype?.ID == "HuntersBounty")
        {
            if (HasComp<BodyPartComponent>(used) && !IsValidBountyHead(used))
            {
                _popup.PopupEntity(
                    Loc.GetString("hunters-bounty-invalid-head"),
                    uid, args.User, PopupType.MediumCaution);
                _audioSystem.PlayPvs(component.DenySound, uid);
                return;
            }
        }

        if (TryComp<DispenserRejectedComponentsComponent>(uid, out var rejector))
        {
            foreach (var name in rejector.RejectedComponents)
            {
                if (!_componentFactory.TryGetRegistration(name, out var registration))
                    continue;

                if (!HasComp(used, registration.Type))
                    continue;

                args.Handled = true;
                _popup.PopupEntity(
                    Loc.GetString(rejector.DenyPopup),
                    uid, args.User, PopupType.MediumCaution);
                _audioSystem.PlayPvs(component.DenySound, uid);
                return;
            }
        }

        if (!TryPrototype(used, out var prototype))
        {
            _audioSystem.PlayPvs(component.DenySound, uid);
            return;
        }

        if (component.DynamicInventory.TryGetValue(prototype.ID, out var baseAmount))
        {
            args.Handled = true;

            var stationUid = _marketSystem.TryGetOwningStation(uid);

            // Get base multiplier from station trade market
            float marketMultiplier = stationUid.HasValue
                ? _marketSystem.GetPriceMultiplier(stationUid.Value, prototype.ID)
                : 1.0f;

            // Apply dynamic pricing multiplier
            float dynamicMultiplier = _dynamicPricing.GetPriceMultiplier(prototype.ID);

            // Both systems can move down at once, so floor their combined result as well as the
            // station's local saturation. Otherwise two individually reasonable 50% floors become 25%.
            float finalMultiplier = CalculateTradePayoutMultiplier(marketMultiplier, dynamicMultiplier);

            int finalAmount = (int) MathF.Round(baseAmount * finalMultiplier);

            // Anti-arbitrage: an item that can be bought from cargo must never sell back for more
            // than its cargo purchase price, no matter how high the dynamic multiplier climbs.
            // Only the sale value is capped here; the station tax below still applies on top.
            if (GetCargoBuyPrice(prototype.ID) is { } buyPrice)
                finalAmount = Math.Min(finalAmount, buyPrice);

            // Same for a vending machine on this station: buying a good next to the chute and
            // turning it straight back in must not pay out more than it cost (e.g. Old Gliess,
            // whose dispensary stands right beside its chute). Hauling between stations is unaffected.
            if (GetLocalVendPrice(uid, stationUid, prototype) is { } vendPrice)
                finalAmount = Math.Min(finalAmount, vendPrice);

            if (stationUid.HasValue)
                _marketSystem.RecordSale(stationUid.Value, prototype.ID);

            // Keep the sector-wide supply/demand curve in sync with the local station market.
            _dynamicPricing.RecordTransaction(prototype.ID, 1, isBuy: false);

            // Apply the station/faction tax. The base sale value stays fixed; the faction
            // simply takes a percentage cut of the payout into its treasury.
            float taxRate = stationUid.HasValue
                ? _marketSystem.GetTaxRate(stationUid.Value, prototype.ID)
                : 0f;
            int taxAmount = (int) MathF.Round(finalAmount * taxRate);
            int payout = Math.Max(0, finalAmount - taxAmount);

            if (stationUid.HasValue && taxAmount > 0)
                _marketSystem.AddTreasury(stationUid.Value, taxAmount);

            int pct = (int) MathF.Round(finalMultiplier * 100f);
            int taxPct = (int) MathF.Round(taxRate * 100f);
            _popup.PopupEntity(
                Loc.GetString("rat-station-trade-market",
                    ("finalAmount", payout),
                    ("pct", pct),
                    ("baseAmount", baseAmount),
                    ("taxAmount", taxAmount),
                    ("taxPct", taxPct)),
                uid, args.User, PopupType.Medium);

            component.PendingDynamicAmount = payout;
            TryDispenseItem(uid, component, string.Empty);

            if (virtualItem != null)
                _virtualItemSystem.DeleteVirtualItem((args.Used, virtualItem), args.User);
            QueueDel(used);
            return;
        }
        if (TryGetDispenseItem(component, prototype.ID, out string itemId))
        {
            args.Handled = true;
            TryDispenseItem(uid, component, itemId);

            if (virtualItem != null)
                _virtualItemSystem.DeleteVirtualItem((args.Used, virtualItem), args.User);
            QueueDel(used);
        }
        else
        {
            _audioSystem.PlayPvs(component.DenySound, uid);
        }
    }

    internal static float CalculateTradePayoutMultiplier(float marketMultiplier, float dynamicMultiplier)
    {
        var combined = marketMultiplier * dynamicMultiplier;
        return float.IsFinite(combined)
            ? MathF.Max(MinimumTradePayoutMultiplier, combined)
            : MinimumTradePayoutMultiplier;
    }

    /// <summary>
    /// Prints the issuing registry's copy of a document rather than ruling on it. The machine
    /// reports only what the issuer recorded; whether the passport in the reader's other hand
    /// still says the same thing is for the reader to work out.
    /// </summary>
    private void PrintPassportRecord(EntityUid uid, DispenserComponent component, PassportComponent passport,
        EntityUid user)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.GetString("passport-record-header"));
        text.AppendLine(Loc.GetString("passport-record-query", ("pid", Printable(passport.PassportId))));
        text.AppendLine();

        if (passport.Record is not { } record)
        {
            // Nothing was ever filed for this document: a forgery, or a blank booklet that no
            // issuer ever touched. Saying so is a statement about the registry, not a verdict.
            text.AppendLine(Loc.GetString("passport-record-missing"));
            text.AppendLine();
            text.AppendLine(Loc.GetString("passport-record-missing-note"));
        }
        else
        {
            text.AppendLine(Loc.GetString("passport-record-found"));
            text.AppendLine();
            text.AppendLine(Loc.GetString("passport-record-name", ("name", Printable(record.FullName))));
            text.AppendLine(Loc.GetString("passport-record-age", ("age", record.Age)));
            text.AppendLine(Loc.GetString("passport-record-species", ("species", Printable(record.Species))));
            text.AppendLine(Loc.GetString("passport-record-sex", ("sex", Printable(record.Sex))));
            text.AppendLine(Loc.GetString("passport-record-height", ("height", record.HeightCm)));
            text.AppendLine(Loc.GetString("passport-record-nationality",
                ("nationality", Printable(record.Nationality))));
            text.AppendLine(Loc.GetString("passport-record-employer", ("employer", Printable(record.Employer))));
            text.AppendLine(Loc.GetString("passport-record-lifepath", ("lifepath", Printable(record.Lifepath))));
            text.AppendLine(Loc.GetString("passport-record-pid", ("pid", Printable(record.PassportId))));
            text.AppendLine(Loc.GetString("passport-record-issued", ("year", record.IssueYear)));
            text.AppendLine(Loc.GetString("passport-record-expires", ("year", record.ExpirationYear)));
            text.AppendLine();
            text.AppendLine(Loc.GetString("passport-record-footer"));
        }

        component.PendingPrintout = text.ToString();
        TryDispenseItem(uid, component, RecordPrintoutPrototype);

        _popup.PopupEntity(Loc.GetString("passport-checker-printed"), uid, user, PopupType.Medium);
    }

    /// <summary>
    /// Registry values are player-authored text, so they are escaped before reaching the paper's
    /// markup parser. A blank field prints a visible placeholder instead of an empty line, so an
    /// unrecorded value can never be misread as matching a blank field on the document.
    /// </summary>
    private string Printable(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Loc.GetString("passport-unspecified")
            : FormattedMessage.EscapeText(value);
    }

    /// <summary>
    /// Returns the cheapest cargo purchase price for the given product entity-prototype id, or
    /// <c>null</c> if the item is not sold by cargo. The lookup is built lazily on first use and
    /// cached, since cargo product prototypes are static for the lifetime of the process.
    /// </summary>
    private int? GetCargoBuyPrice(string prototypeId)
    {
        if (_cargoBuyPrices == null)
        {
            _cargoBuyPrices = new Dictionary<string, int>();
            foreach (var product in _prototypeManager.EnumeratePrototypes<CargoProductPrototype>())
            {
                if (product.Abstract || product.Cost <= 0 || string.IsNullOrEmpty(product.Product.Id))
                    continue;

                var id = product.Product.Id;
                if (!_cargoBuyPrices.TryGetValue(id, out var existing) || product.Cost < existing)
                    _cargoBuyPrices[id] = product.Cost;
            }
        }

        return _cargoBuyPrices.TryGetValue(prototypeId, out var cost) ? cost : null;
    }

    /// <summary>
    /// Returns the cheapest price a vending machine on the dispenser's grid (or on any grid of the
    /// given station) sells the given good for, or <c>null</c> if no local vendor stocks it.
    /// Grid-based so it also covers event-spawned grids that are not registered as stations.
    /// </summary>
    public int? GetLocalVendPrice(EntityUid dispenser, EntityUid? station, EntityPrototype prototype)
    {
        var grids = new HashSet<EntityUid>();
        if (Transform(dispenser).GridUid is { } grid)
            grids.Add(grid);
        if (TryComp<StationDataComponent>(station, out var stationData))
            grids.UnionWith(stationData.Grids);

        if (grids.Count == 0)
            return null;

        double? cheapest = null;
        var query = EntityQueryEnumerator<VendingMachineComponent, TransformComponent>();
        while (query.MoveNext(out _, out var vend, out var xform))
        {
            if (xform.GridUid is not { } vendGrid || !grids.Contains(vendGrid))
                continue;

            if (!vend.Inventory.ContainsKey(prototype.ID)
                && !vend.EmaggedInventory.ContainsKey(prototype.ID)
                && !vend.ContrabandInventory.ContainsKey(prototype.ID))
                continue;

            // Mirrors the price VendingMachineSystem charges on purchase.
            var price = _pricing.GetEstimatedPrice(prototype) * vend.GlobalPriceMod;
            if (cheapest == null || price < cheapest)
                cheapest = price;
        }

        return cheapest is { } value ? (int) value : null;
    }

    public bool TryGetDispenseItem(DispenserComponent component, string itemId, out string dispenseItemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            dispenseItemId = string.Empty;
            return false;
        }

        foreach (var kvp in component.Inventory)
        {
            if (kvp.Key == itemId)
            {
                dispenseItemId = kvp.Value;
                return !string.IsNullOrEmpty(dispenseItemId);
            }
        }

        dispenseItemId = string.Empty;
        return false;
    }

    public void TryDispenseItem(EntityUid uid, DispenserComponent component, string itemId)
    {
        component.Dispensing = true;
        component.DispensingItemId = itemId;
        component.DispenseTimer = 0f;

        _audioSystem.PlayPvs(component.DispenseSound, uid);
    }

    public void Dispense(EntityUid uid, DispenserComponent component, string itemId)
    {
        if (component.PendingDynamicAmount > 0)
        {
            _stackSystem.SpawnMultiple("SpaceCash", component.PendingDynamicAmount, Transform(uid).Coordinates);
            component.PendingDynamicAmount = 0;
            return;
        }

        if (string.IsNullOrEmpty(itemId))
            return;

        var spawned = Spawn(itemId, Transform(uid).Coordinates);

        if (component.PendingPrintout is { } printout)
        {
            _paper.SetContent(spawned, printout);
            component.PendingPrintout = null;
        }
    }

    /// <summary>
    ///     Checks if the given entity is a valid severed head with HadMindComponent.
    ///     A valid head must:
    ///     1. Have a BodyPartComponent with PartType = Head
    ///     2. Not be attached to a body (Body is null)
    ///     3. Have HadMindComponent (was once player-controlled)
    /// </summary>
    private bool IsValidBountyHead(EntityUid entity)
    {
        // Must have BodyPartComponent and be a Head
        if (!TryComp<BodyPartComponent>(entity, out var bodyPart) ||
            bodyPart.PartType != BodyPartType.Head)
        {
            return false;
        }

        // Must be detached from a body (severed)
        if (bodyPart.Body != null)
        {
            return false;
        }

        // Must have had a mind at some point (was player-controlled)
        if (!HasComp<HadMindComponent>(entity))
        {
            return false;
        }

        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<DispenserComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (!component.Dispensing)
                continue;

            component.DispenseTimer += frameTime;
            if (component.DispenseTimer >= component.DispenseTime)
            {
                component.DispenseTimer = 0f;
                component.Dispensing = false;

                Dispense(uid, component, component.DispensingItemId);
            }
        }
    }
}
