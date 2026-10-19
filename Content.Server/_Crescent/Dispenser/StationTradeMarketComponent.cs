namespace Content.Server.Crescent.Dispenser;

[RegisterComponent]
public sealed partial class StationTradeMarketComponent : Component
{
    [DataField]
    public Dictionary<string, float> SalesAccumulator = new();

    /// <summary>Local price reduction added by one sale of the same good.</summary>
    [DataField]
    public float PriceDropPerSale = 0.01f;

    /// <summary>Lowest local saturation multiplier. The final payout is also floored by the dispenser.</summary>
    [DataField]
    public float MinMultiplier = 0.5f;


    /// <summary>Sales worth of saturation removed per second (one sale every 30 seconds by default).</summary>
    [DataField]
    public float RecoveryRatePerSecond = 1f / 30f;

    // --- Taxation ---------------------------------------------------------

    /// <summary>
    /// Station-wide default tax rate (0..1) applied to every trade good sold through
    /// this station's trade points, unless a per-good override exists in <see cref="TaxOverrides"/>.
    /// Set from the taxation console. For a faction station this is a live copy of the faction's rates in
    /// <c>FactionTaxRateSystem</c>, which carries them across rounds; write through
    /// <c>StationTradeMarketSystem</c> so the change is saved.
    /// </summary>
    [DataField]
    public float DefaultTaxRate = 0f;

    /// <summary>
    /// Per-trade-good tax rate overrides (0..1), keyed by trade good prototype id.
    /// Takes precedence over <see cref="DefaultTaxRate"/> for that specific good.
    /// </summary>
    [DataField]
    public Dictionary<string, float> TaxOverrides = new();

    /// <summary>
    /// Hard ceiling on any tax rate so a console operator can never confiscate the
    /// entire payout (which would make trading pointless).
    /// </summary>
    [DataField]
    public float MaxTaxRate = 0.95f;

    /// <summary>
    /// Tax revenue for an <b>unaligned</b> station only, and per-round only.
    /// </summary>
    /// <remarks>
    /// A station belonging to a faction does not keep a balance here: its money lives in
    /// <c>FactionTreasurySystem</c> under <see cref="Faction"/>, because a faction routinely owns
    /// several stations at once (its home station plus every shipyard-bought hull that becomes its own
    /// station) and a per-station copy of the balance both duplicated and destroyed money as the copies
    /// wrote over each other. Read and write through <c>StationTradeMarketSystem</c>, never this field.
    /// </remarks>
    [DataField]
    public int TreasuryBalance = 0;

    /// <summary>
    /// Faction key whose treasury this station banks into (e.g. "SHI", "NCWL", "DSM"), resolved from
    /// the station grid's IFF faction. Empty means unaligned: the station keeps its own per-round
    /// balance in <see cref="TreasuryBalance"/> instead.
    /// </summary>
    [ViewVariables]
    public string Faction = string.Empty;

    /// <summary>
    /// Whether <see cref="Faction"/> has been resolved this round. Guards the lookup so it runs once
    /// per station rather than every tick, and makes the first writer win.
    /// </summary>
    [ViewVariables]
    public bool FactionResolved = false;
}
