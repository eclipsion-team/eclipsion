namespace Content.Shared._Crescent.Shipyard;

/// <summary>
/// Stops civilian shipyards on this grid from stamping the station's IFF faction onto the hulls they sell.
/// </summary>
/// <remarks>
/// A bought hull normally inherits its station's IFF faction. Gliess Santo is the CMM's seat but also the
/// spacers' port, so without this every independent hull bought there flew CMM colours into CMM wars.
/// Only consoles that bill the buyer are affected: a yard with <c>UsesFactionTreasury</c> (the CMM
/// mothership console) still issues faction hulls. Applied through the gameMap's <c>gridComponents</c>
/// rather than on the consoles, so it survives the consoles being remapped.
/// </remarks>
[RegisterComponent]
public sealed partial class CivilianShipyardNoFactionIffComponent : Component
{
}
