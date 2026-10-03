namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Put on the station of a Great Hunt vessel (its gameMap entry). Once the ship is bought, every IFF console aboard
/// gets a cloak that never overheats, and the ship leaves the yard already cloaked.
/// </summary>
[RegisterComponent]
public sealed partial class GreatHuntCloakedVesselComponent : Component
{
    /// <summary>Whether the cloak is switched on at purchase, not just made unlimited.</summary>
    [DataField]
    public bool StartCloaked = true;
}
