using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared.Shipyard.Components;

/// <summary>
///   Eclipsion - recolours a shipyard console's screen to its faction's accent. The client points the
///   computerLayerScreen layer at _Crescent/Structures/Machines/ShipyardScreens/{faction}.rsi.
///   Visual only; lives on the parent so tabletop variants inherit it.
/// </summary>
[RegisterComponent]
public sealed partial class ShipyardScreenComponent : Component
{
    [DataField(required: true)]
    public ProtoId<FactionPrototype> Faction;
}
