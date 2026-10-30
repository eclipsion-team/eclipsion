using Content.Shared._Crescent.Economy;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Serializable, NetSerializable]
public sealed class TerritoryPaymentState
{
    public bool HasPersonal;

    public long PersonalBalance;

    public string? Faction;

    public bool HasTreasury;

    public int TreasuryBalance;

    public int TreasuryAllowance;

    public float TreasuryShare;

    public bool CanAfford(PaymentSource source, int price)
    {
        return source switch
        {
            PaymentSource.Personal => HasPersonal && PersonalBalance >= price,
            PaymentSource.Faction => HasTreasury && TreasuryAllowance >= price,
            _ => false,
        };
    }
}
