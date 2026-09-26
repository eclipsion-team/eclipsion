namespace Content.Shared._Crescent.NpcSquad;

/// <summary>
/// Crescent: identify-friend-or-foe for faction soldier AI. Rounds fired by an entity with this component
/// pass through anyone on its own side instead of hitting them.
/// </summary>
/// <remarks>
/// The soldier AI also holds fire while a friendly is standing in its line of fire, but an automatic weapon
/// is already mid-burst when someone steps in front of it, and a round is already in the air when a
/// squadmate walks across its path. This is the part that makes "never shoots its own" actually hold.
/// </remarks>
[RegisterComponent]
public sealed partial class NpcIffComponent : Component
{
    /// <summary>
    /// The player this NPC currently follows as part of their squad, if any. The leader and everyone else
    /// following them count as friendly on top of the NPC's own factions.
    /// </summary>
    [ViewVariables]
    public EntityUid? SquadLeader;
}
