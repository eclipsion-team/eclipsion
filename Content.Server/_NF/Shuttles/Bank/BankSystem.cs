using System.Threading;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.GameTicking;
using Content.Shared.Bank.Components;
using Content.Shared.Preferences;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Content.Server.Cargo.Components;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Mind.Components;
using Content.Shared.Mind;

namespace Content.Server.Bank;

public sealed partial class BankSystem : EntitySystem
{
    [Dependency] private readonly IServerPreferencesManager _prefsManager = default!;
    [Dependency] private readonly IServerDbManager _dbManager = default!;

    private ISawmill _log = default!;

    /// <summary>
    ///     Users we have already complained about, so the once-per-state-send handler below doesn't spam the log.
    /// </summary>
    private readonly HashSet<NetUserId> _brokenPrefsWarned = new();
    private readonly HashSet<NetUserId> _crossCharacterWriteWarned = new();

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("bank");
        SubscribeLocalEvent<BankAccountComponent, ComponentGetState>(OnBankAccountChanged);
        SubscribeLocalEvent<BankAccountComponent, MindAddedMessage>(OnMindAdded);
        InitializeATM();
        InitializeStationATM();
    }

    private void OnMindAdded(EntityUid uid, BankAccountComponent bank, ref MindAddedMessage args)
    {
        var mind = args.Mind.Comp;
        if (mind.UserId == null)
            return;

        if (!_prefsManager.TryGetCachedPreferences(mind.UserId.Value, out var prefs)
            || prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
            return;

        if (bank.Balance != profile.BankBalance)
        {
            bank.Balance = profile.BankBalance;
            EntityManager.Dirty(uid, bank);
            _log.Info($"Mind transfer to {ToPrettyString(uid)}: bank balance reset to profile value {profile.BankBalance}");
        }
    }

    // To ensure that bank account data gets saved, we are going to update the db every time the component changes
    // I at first wanted to try to reduce database calls, however notafet suggested I just do it every time the account changes
    // TODO: stop it from running 5 times every time
    private void OnBankAccountChanged(EntityUid mobUid, BankAccountComponent bank, ref ComponentGetState args)
    {
        var user = args.Player?.UserId;

        if (user == null || args.Player?.AttachedEntity != mobUid)
        {
            return;
        }

        args.State = new BankAccountComponentState
        {
            Balance = bank.Balance,
        };

        // This runs inside component state serialization, on every state send. Anything that throws here (a
        // prefs row whose selected slot has no profile behind it makes SelectedCharacter throw) would take out
        // the player's state send rather than surfacing as a normal error, so resolve it defensively and log.
        // On reconnect the session is re-attached to its mob (and state starts flowing) before the prefs DB load
        // finishes. That's a normal transient window, not a broken profile: skip quietly, the next dirty saves it.
        if (!_prefsManager.TryGetCachedPreferences((NetUserId) user, out var prefs))
            return;

        ICharacterProfile character;
        try
        {
            character = prefs.SelectedCharacter;
        }
        catch (Exception e)
        {
            if (_brokenPrefsWarned.Add((NetUserId) user))
                _log.Error($"Could not resolve the selected character for {user} while saving their bank balance: {e}");
            return;
        }

        var index = prefs.IndexOfCharacter(character);

        if (character is not HumanoidCharacterProfile profile)
        {
            return;
        }

        // State gets serialized several times a second, so don't hit the DB unless something actually moved.
        if (bank.Balance == profile.BankBalance)
            return;

        // The balance is written to whichever slot is selected *right now*, not to the character this mob
        // actually is. If the player switched slots in the lobby while still attached to a mob, this quietly
        // stamps one character's money onto another one's saved profile.
        if (profile.Name != MetaData(mobUid).EntityName && _crossCharacterWriteWarned.Add((NetUserId) user))
        {
            _log.Error(
                $"Saving bank balance {bank.Balance} for {user} onto selected slot {index} ('{profile.Name}'), " +
                $"but their attached mob is '{MetaData(mobUid).EntityName}'. These are different characters.");
        }

        var balanceDiff = (long)bank.Balance - profile.BankBalance;

        var newProfile = profile.WithBank((long)bank.Balance);

        _prefsManager.SetProfileNoChecks((NetUserId) user, index,(ICharacterProfile)newProfile);
        _log.Info($"Character {profile.Name} saved");
        if (balanceDiff > 250000)
        {
            _log.Info($"Character {profile.Name} had a major balance change of {balanceDiff} credits!");
        }
    }

    /// <summary>
    /// Attempts to remove money from a character's bank account. This should always be used instead of attempting to modify the bankaccountcomponent directly
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is attached to, typically the player controlled mob</param>
    /// <param name="amount">The integer amount of which to decrease the bank account</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public bool TryBankWithdraw(EntityUid mobUid, int amount)
    {
        if (amount <= 0)
        {
            _log.Info($"{amount} is invalid");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
        {
            _log.Info($"{mobUid} has no bank account");
            return false;
        }

        if (bank.Balance < amount)
        {
            _log.Info($"{mobUid} has insufficient funds");
            return false;
        }

        bank.Balance -= amount;
        _log.Info($"{mobUid} withdrew {amount}");
        EntityManager.Dirty(mobUid, bank);
        return true;
    }

    /// <summary>
    /// Attempts to add money to a character's bank account. This should always be used instead of attempting to modify the bankaccountcomponent directly
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is connected to, typically the player controlled mob</param>
    /// <param name="amount">The integer amount of which to increase the bank account</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public bool TryBankDeposit(EntityUid mobUid, long amount)
    {
        if (amount <= 0)
        {
            _log.Info($"{amount} is invalid");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
        {
            _log.Info($"{mobUid} has no bank account");
            return false;
        }

        if (bank.Balance > long.MaxValue - amount)
        {
            _log.Warning($"Deposit of {amount} would overflow the bank account for {mobUid}");
            return false;
        }

        bank.Balance += amount;
        _log.Info($"{mobUid} deposited {amount}");
        EntityManager.Dirty(mobUid, bank);
        return true;
    }

    /// <summary>
    /// Sets a character's bank balance to an absolute value. Intended for admin tooling.
    /// Dirtying the component routes the new value back into the saved character profile.
    /// </summary>
    /// <param name="mobUid">The UID the bank account is attached to, typically the player mob.</param>
    /// <param name="amount">The absolute balance to set. Negative values are rejected.</param>
    /// <returns>true if the balance was set, false otherwise.</returns>
    public bool TrySetBankBalance(EntityUid mobUid, long amount)
    {
        if (amount < 0)
            return false;

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
            return false;

        bank.Balance = amount;
        _log.Info($"{mobUid} balance set to {amount}");
        EntityManager.Dirty(mobUid, bank);
        return true;
    }
}
