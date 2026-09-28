using Content.Shared.EventScheduler;

namespace Content.Server._Crescent.HullrotSelfDeleteTimer;

[RegisterComponent]
public sealed partial class SelfDeleteInSpaceComponent : Component
{
    /// <summary>
    /// A cleanup attempt is already queued. Every trip into space used to queue another one, so an item drifting
    /// across grids piled up attempts that all fired together.
    /// </summary>
    [ViewVariables]
    public bool CleanupPending;
}
