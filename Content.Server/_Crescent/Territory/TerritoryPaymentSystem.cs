using Content.Shared._Crescent.Economy;
using System.Diagnostics.CodeAnalysis;
using Content.Server._Crescent.Taxation;
using Content.Server.Administration.Logs;
using Content.Server.Bank;
using Content.Shared._Crescent.Territory;
using Content.Shared.Bank.Components;
using Content.Shared.Database;
using Robust.Shared.Player;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryPaymentSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly FactionTreasurySystem _treasury = default!;

    public TerritoryPaymentState GetState(EntityUid payer, string? faction, float treasuryShare)
    {
        var state = new TerritoryPaymentState
        {
            Faction = faction,
            TreasuryShare = Math.Clamp(treasuryShare, 0f, 1f),
        };

        if (TryComp<BankAccountComponent>(payer, out var bank))
        {
            state.HasPersonal = true;
            state.PersonalBalance = bank.Balance;
        }

        if (faction != null && IsMember(payer, faction) && TryComp<ActorComponent>(payer, out var actor))
        {
            state.HasTreasury = true;
            state.TreasuryBalance = _treasury.Get(faction);
            state.TreasuryAllowance = _treasury.GetRemainingWithdrawal(faction, actor.PlayerSession.UserId, state.TreasuryShare);
        }

        return state;
    }

    public bool TryCharge(
        EntityUid payer,
        string faction,
        PaymentSource source,
        int amount,
        float treasuryShare,
        string reason,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (amount <= 0)
            return true;

        switch (source)
        {
            case PaymentSource.Personal:
                if (!HasComp<BankAccountComponent>(payer))
                {
                    error = "territory-payment-no-account";
                    return false;
                }

                if (!_bank.TryBankWithdraw(payer, amount))
                {
                    error = "territory-payment-personal-short";
                    return false;
                }

                break;

            case PaymentSource.Faction:
                if (!IsMember(payer, faction) || !TryComp<ActorComponent>(payer, out var actor))
                {
                    error = "territory-payment-not-member";
                    return false;
                }

                var user = actor.PlayerSession.UserId;
                var share = Math.Clamp(treasuryShare, 0f, 1f);
                if (_treasury.GetRemainingWithdrawal(faction, user, share) < amount)
                {
                    error = _treasury.Get(faction) < amount
                        ? "territory-payment-treasury-short"
                        : "territory-payment-share-spent";
                    return false;
                }

                var taken = _treasury.TryWithdrawCapped(faction, user, amount, share);
                if (taken < amount)
                {
                    _treasury.RefundCapped(faction, user, taken);
                    error = "territory-payment-treasury-short";
                    return false;
                }

                break;

            default:
                error = "territory-payment-no-account";
                return false;
        }

        _adminLog.Add(LogType.Action, source == PaymentSource.Faction ? LogImpact.Medium : LogImpact.Low,
            $"{ToPrettyString(payer):player} paid {amount} from {(source == PaymentSource.Faction ? $"the {faction} treasury" : "their own account")} for {reason}");
        return true;
    }

    private bool IsMember(EntityUid payer, string faction)
    {
        return string.Equals(PersistentTerritoryFactions.GetTeam(EntityManager, payer), faction, StringComparison.Ordinal);
    }
}
