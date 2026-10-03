namespace Content.Server.Shuttles.Components;

/// <summary>
/// Crescent: a grid that FTL proximity placement looks straight through. Proximity sets an arriving ship down clear
/// of the bounds of every grid near its target, so a long, thin grid such as one side of the Great Hunt's
/// preparation wall would otherwise drag the whole placement outside the area it fences off.
/// </summary>
[RegisterComponent]
public sealed partial class FTLProximityIgnoreComponent : Component
{
}
