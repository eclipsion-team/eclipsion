using Content.Shared._Crescent.Economy;
using Content.Shared.Shipyard.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Shipyard;

/* HULLROT - EDITED OUT
[Serializable, NetSerializable]
public enum ShipyardConsoleUiKey : byte
{
    Key
}

*/
[Serializable, NetSerializable]
public sealed class ShipyardConsoleState : BoundUserInterfaceState
{
    public readonly int Balance;

    public ShipyardConsoleState(int balance)
    {
        Balance = balance;
    }
}

/// <summary>
/// Ask the server to purchase a vessel.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsolePurchaseMessage : BoundUserInterfaceMessage
{
    public readonly ProtoId<VesselPrototype> Vessel;
    public readonly string? CustomName;

    public readonly PaymentSource Source;

    public ShipyardConsolePurchaseMessage(string vessel, string? customName = null, PaymentSource source = PaymentSource.Faction)
    {
        Vessel = vessel;
        CustomName = customName;
        Source = source;
    }
}
