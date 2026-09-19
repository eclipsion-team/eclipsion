using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.HeatSeeking;

/// <summary>
/// Sits on a grid while at least one heat seeker is tracking it, so the shuttle console can flag the lock.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MissileLockWarningComponent : Component
{
    /// <summary>
    /// How many seekers are tracking this grid right now.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int Seekers;
}
