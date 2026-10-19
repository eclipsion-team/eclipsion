using System.Text.Json;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Taxation;

/// <summary>
/// Cross-round store for the tax rates a faction sets at its taxation console.
/// </summary>
/// <remarks>
/// <para>
/// The live rates sit on <c>StationTradeMarketComponent</c>, and station entities are recreated every round,
/// so every rate a faction set went back to zero at round end while the treasury it fed carried on. Rates are
/// therefore recorded here per faction and copied back onto each of the faction's stations when that station
/// resolves its faction (see <c>StationTradeMarketSystem</c>).
/// </para>
/// <para>
/// Keyed by faction rather than station for the same reason the treasury is: a faction owns several stations,
/// and station names and uids do not survive a round. Unaligned stations have nothing stable to key on and keep
/// per-round rates only.
/// </para>
/// </remarks>
public sealed class FactionTaxRateSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _res = default!;

    private static readonly ResPath SavePath = new("/faction_tax_rates.json");
    private const int SaveVersion = 1;

    private readonly Dictionary<string, FactionTaxRates> _rates = new();

    public override void Initialize()
    {
        base.Initialize();

        Load();
    }

    /// <summary>The saved rates for a faction, or null if it has never set any.</summary>
    public FactionTaxRates? Get(string faction)
    {
        return string.IsNullOrEmpty(faction) ? null : _rates.GetValueOrDefault(faction);
    }

    public void SetDefault(string faction, float rate)
    {
        if (string.IsNullOrEmpty(faction))
            return;

        _rates.GetOrNew(faction).DefaultRate = rate;
        Save();
    }

    public void SetOverride(string faction, string tradeGoodId, float rate)
    {
        if (string.IsNullOrEmpty(faction))
            return;

        _rates.GetOrNew(faction).Overrides[tradeGoodId] = rate;
        Save();
    }

    public void ClearOverride(string faction, string tradeGoodId)
    {
        if (string.IsNullOrEmpty(faction) || !_rates.TryGetValue(faction, out var rates))
            return;

        if (rates.Overrides.Remove(tradeGoodId))
            Save();
    }

    private void Load()
    {
        try
        {
            if (!_res.UserData.TryReadAllText(SavePath, out var json))
                return;

            var saved = JsonSerializer.Deserialize<FactionTaxRateSaveData>(json);
            if (saved is null || saved.Version != SaveVersion)
                return;

            _rates.Clear();
            foreach (var (faction, rates) in saved.Factions)
            {
                if (!string.IsNullOrEmpty(faction))
                    _rates[faction] = rates;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load faction tax rates: {e}");
        }
    }

    /// <summary>
    /// Written straight out on every change. Rates only move when someone works a taxation console, so this is a
    /// handful of writes a round, and a server that dies mid-round keeps them.
    /// </summary>
    private void Save()
    {
        try
        {
            var data = new FactionTaxRateSaveData { Version = SaveVersion, Factions = _rates };
            _res.UserData.WriteAllText(SavePath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save faction tax rates: {e}");
        }
    }
}

public sealed class FactionTaxRates
{
    public float DefaultRate { get; set; }

    /// <summary>Per-trade-good rates, keyed by trade good prototype id.</summary>
    public Dictionary<string, float> Overrides { get; set; } = new();
}

internal sealed class FactionTaxRateSaveData
{
    public int Version { get; set; }

    public Dictionary<string, FactionTaxRates> Factions { get; set; } = new();
}
