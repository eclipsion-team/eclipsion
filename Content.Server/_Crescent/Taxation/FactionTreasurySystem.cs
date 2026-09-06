using System.Text.Json;
using Content.Shared.GameTicking;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Taxation;

/// <summary>
/// The single authority for faction treasury balances.
/// </summary>
/// <remarks>
/// <para>
/// A faction owns exactly one balance, held here. Station entities are recreated every round and a
/// faction can own several of them at once (its home station plus every shipyard-bought hull that
/// becomes its own station), so a balance mirrored onto station components would be duplicated: each
/// copy would load the full faction balance and then race the others writing back, which both
/// duplicated and destroyed money. Everything therefore reads and writes through this system, and
/// <c>StationTradeMarketComponent.TreasuryBalance</c> is only used by unaligned stations, which have
/// no faction to bank into.
/// </para>
/// <para>
/// The dictionary lives for the whole server process, so balances survive round restarts, and is
/// mirrored to a JSON file under the server's user-data directory so they also survive full restarts.
/// </para>
/// </remarks>
public sealed class FactionTreasurySystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _res = default!;

    private static readonly ResPath SavePath = new("/faction_treasury.json");

    /// <summary>How often, at most, the in-memory balances are flushed to disk while dirty.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, int> _balances = new();

    /// <summary>
    /// Per-faction, per-player cumulative hand withdrawals this round. Backs the treasury console's
    /// per-person cap. Kept here rather than on a station component so the cap follows the faction:
    /// on the component a player could withdraw their full share once per station the faction owns.
    /// </summary>
    private readonly Dictionary<string, Dictionary<NetUserId, long>> _withdrawnThisRound = new();

    private bool _dirty;
    private float _sinceSave;

    public override void Initialize()
    {
        base.Initialize();

        // Flush on round cleanup so a round's earnings survive even if the process is killed shortly
        // after, and drop the per-round withdrawal ledger so caps reset with the round.
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            Save();
            _withdrawnThisRound.Clear();
        });

        Load();
    }

    /// <summary>Current balance for a faction (0 if the faction has never banked anything).</summary>
    public int Get(string faction)
    {
        return string.IsNullOrEmpty(faction) ? 0 : _balances.GetValueOrDefault(faction);
    }

    /// <summary>Overwrites a faction's balance. Clamped at zero. Returns the new balance.</summary>
    public int Set(string faction, int value)
    {
        if (string.IsNullOrEmpty(faction))
            return 0;

        value = Math.Max(0, value);

        if (_balances.TryGetValue(faction, out var current) && current == value)
            return value;

        _balances[faction] = value;
        _dirty = true;
        return value;
    }

    /// <summary>
    /// Drops a faction from the ledger entirely, balance and withdrawal record alike. Unlike
    /// <see cref="Set"/> with a zero, this purges the key from <see cref="SavePath"/> on the next flush
    /// rather than persisting a zero balance for a faction that no longer exists.
    /// </summary>
    public void Forget(string faction)
    {
        if (string.IsNullOrEmpty(faction))
            return;

        _withdrawnThisRound.Remove(faction);

        if (_balances.Remove(faction))
            _dirty = true;
    }

    /// <summary>Adds to a faction's balance, up to the account limit. Returns the new balance.</summary>
    public int Add(string faction, int amount)
    {
        if (string.IsNullOrEmpty(faction) || amount <= 0)
            return Get(faction);

        return Set(faction, (int) Math.Min((long) Get(faction) + amount, int.MaxValue));
    }

    /// <summary>
    /// Removes up to <paramref name="amount"/>, clamped to the available balance. Returns what was
    /// actually taken. Uncapped — for robbery, payroll and machine purchases.
    /// </summary>
    public int TryWithdraw(string faction, int amount)
    {
        if (string.IsNullOrEmpty(faction) || amount <= 0)
            return 0;

        var balance = Get(faction);
        var taken = Math.Min(amount, balance);
        if (taken <= 0)
            return 0;

        Set(faction, balance - taken);
        return taken;
    }

    /// <summary>
    /// How much more this player may withdraw by hand this round, given a per-person share of
    /// <paramref name="maxFraction"/>. Measured against the vault as it stood before they started
    /// (current balance + their prior withdrawals), so coming back repeatedly cannot beat the cap.
    /// </summary>
    public int GetRemainingWithdrawal(string faction, NetUserId user, float maxFraction)
    {
        if (string.IsNullOrEmpty(faction) || !float.IsFinite(maxFraction))
            return 0;

        var balance = Get(faction);
        if (balance <= 0)
            return 0;

        var already = GetWithdrawnThisRound(faction, user);
        var cap = Math.Floor(((double) balance + already) * Math.Clamp(maxFraction, 0f, 1f));

        return (int) Math.Clamp(cap - already, 0, balance);
    }

    /// <summary>Credits this player has already drawn by hand from this faction's vault this round.</summary>
    public long GetWithdrawnThisRound(string faction, NetUserId user)
    {
        return _withdrawnThisRound.TryGetValue(faction, out var ledger) && ledger.TryGetValue(user, out var already)
            ? already
            : 0;
    }

    /// <summary>
    /// Withdraws for a specific player, enforcing their per-round share of the vault.
    /// Returns the amount actually withdrawn.
    /// </summary>
    public int TryWithdrawCapped(string faction, NetUserId user, int amount, float maxFraction)
    {
        if (amount <= 0)
            return 0;

        var allowed = Math.Min(amount, GetRemainingWithdrawal(faction, user, maxFraction));
        if (allowed <= 0)
            return 0;

        var taken = TryWithdraw(faction, allowed);
        if (taken <= 0)
            return 0;

        _withdrawnThisRound.GetOrNew(faction)[user] = GetWithdrawnThisRound(faction, user) + taken;
        return taken;
    }

    /// <summary>
    /// Puts back money taken by <see cref="TryWithdrawCapped"/> that could not be delivered, and gives
    /// the player their budget back with it. Without the second half, a payment that failed after the
    /// debit would silently cost the operator part of their round's allowance.
    /// </summary>
    public void RefundCapped(string faction, NetUserId user, int amount)
    {
        if (string.IsNullOrEmpty(faction) || amount <= 0)
            return;

        Add(faction, amount);

        if (_withdrawnThisRound.TryGetValue(faction, out var ledger) && ledger.TryGetValue(user, out var already))
            ledger[user] = Math.Max(0, already - amount);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_dirty)
            return;

        _sinceSave += frameTime;
        if (_sinceSave >= SaveInterval.TotalSeconds)
            Save();
    }

    private void Load()
    {
        try
        {
            if (!_res.UserData.TryReadAllText(SavePath, out var json))
                return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (loaded is null)
                return;

            _balances.Clear();
            foreach (var (faction, balance) in loaded)
                _balances[faction] = Math.Max(0, balance);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load faction treasury balances: {e}");
        }
    }

    private void Save()
    {
        _sinceSave = 0f;
        if (!_dirty)
            return;

        try
        {
            var json = JsonSerializer.Serialize(_balances);
            _res.UserData.WriteAllText(SavePath, json);
            _dirty = false;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save faction treasury balances: {e}");
        }
    }
}
