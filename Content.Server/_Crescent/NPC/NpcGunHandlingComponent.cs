namespace Content.Server._Crescent.NPC;

/// <summary>
/// Optional tuning for <see cref="NpcGunHandlingSystem"/>. The system works on every NPC without this
/// component using the defaults below; add it to change them, or to let a mob resupply its own weapon.
/// </summary>
[RegisterComponent]
public sealed partial class NpcGunHandlingComponent : Component
{
    /// <summary>
    /// Wield the held gun. Practically every Hullrot primary has GunRequiresWield, which silently blocks
    /// every shot an NPC tries to take otherwise.
    /// </summary>
    [DataField]
    public bool Wield = true;

    /// <summary>
    /// Close the bolt on chamber-fed guns and work the action on pump/bolt-action ones.
    /// </summary>
    [DataField]
    public bool Cycle = true;

    /// <summary>
    /// Refill the gun once it runs dry, as if the NPC were carrying spare magazines. Off by default so
    /// ordinary NPCs still burn through what they spawned with and then go looking for another gun.
    /// </summary>
    /// <remarks>
    /// Crescent: real magazines the NPC carries always go in first; this only kicks in once those are gone.
    /// </remarks>
    [DataField]
    public bool Resupply;

    /// <summary>
    /// Crescent: how many spare magazines for its starting gun the NPC is handed on spawn. They go into
    /// whatever storage it is wearing.
    /// </summary>
    [DataField]
    public int SpareMagazines;

    /// <summary>
    /// Crescent: for a gun that loads loose rounds instead of taking magazines - pump shotguns, bolt-action
    /// rifles - how many boxes of its ammunition the NPC is handed on spawn instead.
    /// </summary>
    [DataField]
    public int SpareAmmoBoxes;

    /// <summary>
    /// How long a resupply takes. Roughly a magazine change.
    /// </summary>
    [DataField]
    public TimeSpan ResupplyDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many refills are left, or null for unlimited.
    /// </summary>
    [DataField]
    public int? ResupplyCount;

    /// <summary>
    /// When the reload currently in progress finishes.
    /// </summary>
    [ViewVariables]
    public TimeSpan? ResupplyEnd;

    /// <summary>
    /// Earliest time the next handling step may be attempted.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextAttempt;
}
