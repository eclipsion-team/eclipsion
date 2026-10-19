using Content.Server._Crescent.Taxation;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Shuttles.Components;
using JetBrains.Annotations;
using Robust.Shared.Network;
  
namespace Content.Server.Crescent.Dispenser;

[UsedImplicitly]  
public sealed class StationTradeMarketSystem : EntitySystem  
{  
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly FactionTreasurySystem _treasury = default!;
    [Dependency] private readonly FactionTaxRateSystem _taxRates = default!;

    public override void Initialize()  
    {
        base.Initialize();  
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);  
    }
  
    private void OnStationPostInit(ref StationPostInitEvent ev)  
    {
        EnsureComp<StationTradeMarketComponent>(ev.Station);  
    }
  
    public override void Update(float frameTime)  
    {
        base.Update(frameTime);  
  
        var query = EntityQueryEnumerator<StationTradeMarketComponent>();
        while (query.MoveNext(out var uid, out var market))
        {
            // Resolve each station's faction as soon as it exists, so the treasury accrues and persists
            // without any console ever being placed. Cheap after the first success — FactionResolved
            // short-circuits it.
            EnsureFactionResolved(uid, market);

            if (market.SalesAccumulator.Count == 0)
                continue;
  
            var toRemove = new List<string>();  
            foreach (var (goodId, accumulated) in market.SalesAccumulator)
            {  
                var newValue = accumulated - market.RecoveryRatePerSecond * frameTime;
                if (newValue <= 0f)  
                    toRemove.Add(goodId);  
                else  
                    market.SalesAccumulator[goodId] = newValue;  
            }  
  
            foreach (var key in toRemove)  
                market.SalesAccumulator.Remove(key);  
        }  
    }
	
    public float GetPriceMultiplier(EntityUid stationUid, string tradeGoodId)  
    {  
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))  
            return 1.0f;  
  
        if (!market.SalesAccumulator.TryGetValue(tradeGoodId, out var accumulated))  
            return 1.0f;  
  
        return MathF.Max(market.MinMultiplier, 1.0f - accumulated * market.PriceDropPerSale);  
    }  

    public void RecordSale(EntityUid stationUid, string tradeGoodId)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return;

        market.SalesAccumulator.TryGetValue(tradeGoodId, out var current);
        market.SalesAccumulator[tradeGoodId] = current + 1.0f;
    }

    public EntityUid? TryGetOwningStation(EntityUid entityUid)
    {
        return _station.GetOwningStation(entityUid);
    }

    // --- Taxation ---------------------------------------------------------

    /// <summary>
    /// Resolves the effective tax rate (0..MaxTaxRate) for a trade good on this station:
    /// a per-good override if present, otherwise the station-wide default.
    /// </summary>
    public float GetTaxRate(EntityUid stationUid, string tradeGoodId)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0f;

        var rate = market.TaxOverrides.TryGetValue(tradeGoodId, out var over)
            ? over
            : market.DefaultTaxRate;

        return SanitizeRate(rate, market);
    }

    public void SetDefaultTaxRate(EntityUid stationUid, float rate)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return;

        // Resolved before the write, so a faction resolving afterwards cannot load last round's rates over it.
        EnsureFactionResolved(stationUid, market);

        market.DefaultTaxRate = SanitizeRate(rate, market);

        if (string.IsNullOrEmpty(market.Faction))
            return;

        _taxRates.SetDefault(market.Faction, market.DefaultTaxRate);
        SyncFactionTaxRates(stationUid, market.Faction);
    }

    public void SetTaxOverride(EntityUid stationUid, string tradeGoodId, float rate)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return;

        EnsureFactionResolved(stationUid, market);

        var sanitized = SanitizeRate(rate, market);
        market.TaxOverrides[tradeGoodId] = sanitized;

        if (string.IsNullOrEmpty(market.Faction))
            return;

        _taxRates.SetOverride(market.Faction, tradeGoodId, sanitized);
        SyncFactionTaxRates(stationUid, market.Faction);
    }

    /// <summary>
    /// Copies a faction's saved rates onto a station that has just been tied to it. Called once per station per
    /// round, when its faction is first resolved.
    /// </summary>
    private void ApplySavedTaxRates(StationTradeMarketComponent market)
    {
        var saved = _taxRates.Get(market.Faction);
        if (saved is null)
            return;

        market.DefaultTaxRate = SanitizeRate(saved.DefaultRate, market);

        market.TaxOverrides.Clear();
        foreach (var (good, rate) in saved.Overrides)
            market.TaxOverrides[good] = SanitizeRate(rate, market);
    }

    /// <summary>
    /// Pushes a rate change made at one of a faction's stations to every other station it owns this round. The
    /// saved record is per faction, so without this the stations would disagree until the next round collapsed
    /// them all onto whichever console was worked last.
    /// </summary>
    private void SyncFactionTaxRates(EntityUid source, string faction)
    {
        var query = EntityQueryEnumerator<StationTradeMarketComponent>();
        while (query.MoveNext(out var uid, out var market))
        {
            if (uid != source && market.FactionResolved && market.Faction == faction)
                ApplySavedTaxRates(market);
        }
    }

    /// <summary>
    /// Brings a rate coming off the wire into 0..<see cref="StationTradeMarketComponent.MaxTaxRate"/>.
    /// </summary>
    /// <remarks>
    /// Math.Clamp alone is not enough: NaN fails every comparison inside it and comes back out unchanged, so a
    /// NaN rate would be stored live. From there the payout maths turns the station's whole cut into zero
    /// without erroring, and the console just displays "NaN" - so it is rejected outright rather than clamped.
    /// </remarks>
    private static float SanitizeRate(float rate, StationTradeMarketComponent market)
    {
        if (!float.IsFinite(rate))
            return 0f;

        var max = float.IsFinite(market.MaxTaxRate) ? Math.Clamp(market.MaxTaxRate, 0f, 1f) : 1f;

        return Math.Clamp(rate, 0f, max);
    }

    public void ClearTaxOverride(EntityUid stationUid, string tradeGoodId)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return;

        EnsureFactionResolved(stationUid, market);

        market.TaxOverrides.Remove(tradeGoodId);

        if (string.IsNullOrEmpty(market.Faction))
            return;

        _taxRates.ClearOverride(market.Faction, tradeGoodId);
        SyncFactionTaxRates(stationUid, market.Faction);
    }

    /// <summary>
    /// Forces a station onto a faction's treasury. Normally the faction is resolved automatically from
    /// the station's IFF (see <see cref="EnsureFactionResolved"/>); this lets a console name the faction
    /// for a station whose grid carries no IFF faction of its own.
    /// </summary>
    /// <remarks>
    /// Binding no longer copies a balance anywhere: the faction's single balance lives in
    /// <see cref="FactionTreasurySystem"/> and every station bound to that faction reads and writes it
    /// directly. Several stations sharing a faction is normal — a faction's home station and each of its
    /// shipyard-bought hulls all become stations — and they must all see the same number.
    /// </remarks>
    public void BindFactionTreasury(EntityUid stationUid, string faction)
    {
        if (string.IsNullOrEmpty(faction))
            return;

        var market = EnsureComp<StationTradeMarketComponent>(stationUid);

        // First writer wins for the round, so a console can't yank a station off the faction its own
        // grid declares.
        if (market.FactionResolved)
            return;

        market.Faction = faction;
        market.FactionResolved = true;
        ApplySavedTaxRates(market);
    }

    /// <summary>
    /// Resolves which faction's treasury this station banks into, so it accrues whether or not any
    /// console is ever placed. Taken from the station grid's IFF faction; stations with no faction
    /// ("Neutral") keep a per-round balance of their own on the component. Runs once per round per
    /// station — <see cref="StationTradeMarketComponent.FactionResolved"/> guards it.
    /// </summary>
    private void EnsureFactionResolved(EntityUid stationUid, StationTradeMarketComponent market)
    {
        if (market.FactionResolved)
            return;

        var faction = ResolveStationFaction(stationUid);
        if (string.IsNullOrEmpty(faction))
            return;

        market.Faction = faction;
        market.FactionResolved = true;
        ApplySavedTaxRates(market);
    }

    /// <summary>
    /// The faction a station belongs to, read from its grids' IFF faction (set from the game map on
    /// spawn). Returns empty for unaligned stations. Only set after the station's grids exist, which is
    /// why loading is deferred to <see cref="Update"/> rather than done at station post-init.
    /// </summary>
    private string ResolveStationFaction(EntityUid stationUid)
    {
        if (!TryComp<StationDataComponent>(stationUid, out var data))
            return string.Empty;

        foreach (var gridUid in data.Grids)
        {
            if (TryComp<IFFComponent>(gridUid, out var iff)
                && !string.IsNullOrEmpty(iff.Faction)
                && iff.Faction != "Neutral")
            {
                return iff.Faction;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Adds tax revenue to the treasury this station banks into. Returns the new balance.
    /// </summary>
    public int AddTreasury(EntityUid stationUid, int amount)
    {
        if (amount <= 0 || !TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0;

        EnsureFactionResolved(stationUid, market);

        if (string.IsNullOrEmpty(market.Faction))
            return market.TreasuryBalance = Math.Max(0, market.TreasuryBalance + amount);

        return _treasury.Add(market.Faction, amount);
    }

    public int GetTreasury(EntityUid stationUid)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0;

        EnsureFactionResolved(stationUid, market);

        return string.IsNullOrEmpty(market.Faction)
            ? market.TreasuryBalance
            : _treasury.Get(market.Faction);
    }

    /// <summary>
    /// Returns the faction a station's treasury is bound to, or null if the station has no faction
    /// treasury (unaligned/Neutral). Used to decide whether a purchase should draw from the faction
    /// vault rather than a personal bank account.
    /// </summary>
    public string? GetStationFaction(EntityUid stationUid)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return null;

        EnsureFactionResolved(stationUid, market);
        return string.IsNullOrEmpty(market.Faction) ? null : market.Faction;
    }

    /// <summary>Overwrites the treasury balance this station banks into (admin). Returns the new balance.</summary>
    public int SetTreasury(EntityUid stationUid, int value)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0;

        EnsureFactionResolved(stationUid, market);

        if (string.IsNullOrEmpty(market.Faction))
            return market.TreasuryBalance = Math.Max(0, value);

        return _treasury.Set(market.Faction, value);
    }

    /// <summary>
    /// Removes up to <paramref name="amount"/> from the treasury, clamped to the available
    /// balance. Returns the amount actually removed. Uncapped — used for robbery/looting.
    /// </summary>
    public int TryWithdrawTreasury(EntityUid stationUid, int amount)
    {
        if (amount <= 0 || !TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0;

        EnsureFactionResolved(stationUid, market);

        if (!string.IsNullOrEmpty(market.Faction))
            return _treasury.TryWithdraw(market.Faction, amount);

        var taken = Math.Min(amount, market.TreasuryBalance);
        market.TreasuryBalance -= taken;
        return taken;
    }

    /// <summary>
    /// Withdraws cash for a specific player, enforcing their per-round share of the faction vault.
    /// Returns the amount actually withdrawn. Unaligned stations have no per-person cap because they
    /// have no faction to share the vault between.
    /// </summary>
    public int TryWithdrawTreasuryCapped(EntityUid stationUid, NetUserId user, int amount, float maxFraction)
    {
        if (amount <= 0 || !TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0;

        EnsureFactionResolved(stationUid, market);

        return string.IsNullOrEmpty(market.Faction)
            ? TryWithdrawTreasury(stationUid, amount)
            : _treasury.TryWithdrawCapped(market.Faction, user, amount, maxFraction);
    }

    /// <summary>
    /// Returns money taken by <see cref="TryWithdrawTreasuryCapped"/> that could not be delivered,
    /// restoring the player's per-round budget along with it.
    /// </summary>
    public void RefundTreasuryCapped(EntityUid stationUid, NetUserId user, int amount)
    {
        if (amount <= 0 || !TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return;

        EnsureFactionResolved(stationUid, market);

        if (string.IsNullOrEmpty(market.Faction))
            AddTreasury(stationUid, amount);
        else
            _treasury.RefundCapped(market.Faction, user, amount);
    }

    /// <summary>How much more this player may draw by hand from this station's vault this round.</summary>
    public int GetRemainingWithdrawal(EntityUid stationUid, NetUserId user, float maxFraction)
    {
        if (!TryComp<StationTradeMarketComponent>(stationUid, out var market))
            return 0;

        EnsureFactionResolved(stationUid, market);

        return string.IsNullOrEmpty(market.Faction)
            ? market.TreasuryBalance
            : _treasury.GetRemainingWithdrawal(market.Faction, user, maxFraction);
    }

    /// <summary>
    /// Finds a station banking into a faction's treasury this round, regardless of where the caller is.
    /// Callers that are faction-scoped rather than station-scoped must use this instead of
    /// <c>GetOwningStation</c>, which would resolve to whichever station they happen to sit on.
    /// </summary>
    /// <remarks>
    /// Several stations may match; any of them is fine, because they all read and write the one balance
    /// held by <see cref="FactionTreasurySystem"/>. Prefer the faction-keyed methods on that system for
    /// pure money movement — this only exists for callers that genuinely need a station entity.
    /// </remarks>
    public EntityUid? TryGetFactionTreasuryStation(string faction)
    {
        if (string.IsNullOrEmpty(faction))
            return null;

        var query = EntityQueryEnumerator<StationTradeMarketComponent>();
        while (query.MoveNext(out var uid, out var market))
        {
            // Resolve lazily here too, so a faction's own station is found on the very first frame even
            // before Update has run — e.g. payroll paying out immediately at round start.
            EnsureFactionResolved(uid, market);

            if (market.FactionResolved && market.Faction == faction)
                return uid;
        }

        return null;
    }
}