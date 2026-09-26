namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: hostility a squad leader ordered onto this NPC by pointing someone out - that one person, and
/// for a while everyone of their faction too. Both wear off on their own.
/// </summary>
/// <remarks>
/// Like the friendly-fire grudges, it works through the NPC's FactionException hostiles, which is what both
/// the target queries and NpcIff read: while it lasts the NPC picks them as targets and its rounds stop
/// passing through them, friend or not.
/// </remarks>
[RegisterComponent, Access(typeof(NpcSquadHostilitySystem))]
public sealed partial class NpcSquadHostilityComponent : Component
{
    /// <summary>
    /// Factions this NPC has been turned against, and until when.
    /// </summary>
    [ViewVariables]
    public Dictionary<string, TimeSpan> Factions = new();

    /// <summary>
    /// Everyone this NPC has been set on - pointed out, or met while their faction was hated - and until when.
    /// </summary>
    [ViewVariables]
    public Dictionary<EntityUid, TimeSpan> Targets = new();
}
