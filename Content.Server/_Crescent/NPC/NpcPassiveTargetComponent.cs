namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: faction soldier AI leaves this alone - doesn't shoot it, hide from it or barricade against it -
/// until it hurts one of them. Then that soldier and everyone on its side go after it.
/// </summary>
/// <remarks>
/// Meant for anti-boarder PD turrets: they are mobs of an NPC faction, so a hostile squad walking past one
/// would otherwise open up on it and pick a fight with the ship's defences it never needed to have. A squad
/// leader pointing at it still has the squad take it out.
/// </remarks>
[RegisterComponent, Access(typeof(NpcPassiveTargetSystem))]
public sealed partial class NpcPassiveTargetComponent : Component
{
    /// <summary>
    /// How long after it last hurt someone they stop fighting it and go back to leaving it alone.
    /// </summary>
    [DataField]
    public TimeSpan ForgetAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The soldier AI it has hurt, and when it last did.
    /// </summary>
    [ViewVariables]
    public Dictionary<EntityUid, TimeSpan> Victims = new();
}
