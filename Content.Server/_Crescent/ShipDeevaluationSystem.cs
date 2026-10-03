namespace Content.Server._Crescent;

/// <summary>
/// This handles...
/// </summary>
///
[RegisterComponent]
public sealed partial class ShipPriceMultiplierComponent : Component
{
    public float priceMultiplier = 0.1f;

    /// <summary>
    /// Crescent: what was paid for the hull at the shipyard, if it was bought. The resale value is capped at this,
    /// otherwise a hull appraising above its list price (guns, cargo and fittings all count) could be bought and
    /// sold straight back at a profit, over and over.
    /// </summary>
    public int? PurchasePrice;
}
public sealed class ShipPriceMultiplierSystem : EntitySystem
{
    /// <inheritdoc/>
    public override void Initialize()
    {


    }
}
