using Content.Shared.Shipyard.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Crescent.Vouchers;

[RegisterComponent, NetworkedComponent]
public sealed partial class ShipVoucherComponent : Component
{
    [DataField("ship")]
    public ProtoId<VesselPrototype> Ship;

    [DataField("requiresShipInConsole")]
    public bool RequiresShipInConsole;

    /// <summary>
    /// Crescent: whether the ship this redeems can be sold back for money. Off for vouchers handed out free, which
    /// would otherwise be a free money printer.
    /// </summary>
    [DataField]
    public bool Resellable = true;
}
