using System.Numerics;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: gets an NPC that ends up floating in space back onto a grid with the jetpack it is wearing.
/// </summary>
/// <remarks>
/// Steering and pathfinding only work on a grid, so an NPC blown out of a breach or knocked off the edge just
/// drifts away for good. Once it is off every grid it lights the jetpack it wears and flies back to where it
/// last stood - or to its squad leader, if it follows one and they are standing on a grid - and shuts the
/// jetpack off again as soon as it is back on one.
/// <para/>
/// The jetpack has to be worn (suit storage or back), not carried in a bag: a jetpack moves whoever it is
/// directly on.
/// </remarks>
[RegisterComponent, Access(typeof(NpcJetpackSystem))]
public sealed partial class NpcJetpackComponent : Component
{
    /// <summary>
    /// How often it looks for its jetpack and notes where it stands.
    /// </summary>
    [DataField]
    public TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// How close to where it is headed counts as there, so it stops thrusting instead of circling the spot.
    /// </summary>
    [DataField]
    public float ArriveDistance = 0.5f;

    [ViewVariables]
    public TimeSpan NextCheck;

    /// <summary>
    /// The grid it last stood on.
    /// </summary>
    [ViewVariables]
    public EntityUid? LastGrid;

    /// <summary>
    /// Where on <see cref="LastGrid"/> it last stood, in that grid's own coordinates so it follows a moving ship.
    /// </summary>
    [ViewVariables]
    public Vector2 LastGridPosition;
}
