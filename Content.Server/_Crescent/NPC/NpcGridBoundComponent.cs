namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: keeps an NPC on the grid it was put on. Steering won't walk it off the edge of that grid - out
/// into space, or across onto a docked ship - whatever it is chasing, so a mapped garrison stays at its post.
/// </summary>
/// <remarks>
/// Only steering is held back: something that throws or drags it off the grid still can. While it follows a
/// player's squad it goes wherever its leader takes it, and once let go it stays on the grid it is on then.
/// </remarks>
[RegisterComponent, Access(typeof(NpcGridBoundSystem))]
public sealed partial class NpcGridBoundComponent : Component
{
    /// <summary>
    /// The grid it stays on: the first one it steers on.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Grid;

    /// <summary>
    /// How far past its own edge a step is checked for still having floor under it.
    /// </summary>
    [DataField]
    public float Lookahead = 0.5f;
}
