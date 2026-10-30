using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Economy;

[Serializable, NetSerializable]
public enum PaymentSource : byte
{
    Personal,

    Faction,
}
