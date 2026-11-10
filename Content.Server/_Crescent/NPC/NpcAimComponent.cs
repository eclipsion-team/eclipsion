namespace Content.Server._Crescent.NPC;

/// <summary>
/// Human aim for NPCs firing a held gun. Added on first shot; turrets and drones that are their own gun never get it.
/// Spreads are the half-width of a triangular error in degrees.
/// </summary>
[RegisterComponent, Access(typeof(NpcAimSystem))]
public sealed partial class NpcAimComponent : Component
{
    [DataField]
    public float BaseSpread = 5f;

    /// <summary>
    /// Extra spread right after spotting a target, fading out over <see cref="SettleTime"/>.
    /// </summary>
    [DataField]
    public float SettleSpread = 10f;

    [DataField]
    public TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// At <see cref="FullSpeed"/> of its own.
    /// </summary>
    [DataField]
    public float MovingSpread = 8f;

    /// <summary>
    /// At <see cref="FullSpeed"/> of the target across the line of fire.
    /// </summary>
    [DataField]
    public float TargetMovingSpread = 6f;

    [DataField]
    public float FullSpeed = 4.5f;

    [DataField]
    public float HurtSpread = 6f;

    [DataField]
    public TimeSpan HurtTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Fraction of the correct lead on a moving target, rolled per shot.
    /// </summary>
    [DataField]
    public float LeadMin = 0.3f;

    [DataField]
    public float LeadMax = 1.1f;

    [DataField]
    public float ReactionMin = 0.4f;

    [DataField]
    public float ReactionMax = 0.8f;

    /// <summary>
    /// Reaction when the same target only just dropped out of sight.
    /// </summary>
    [DataField]
    public float ReacquireReaction = 0.15f;

    [DataField]
    public TimeSpan ReacquireTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Guns at or above this fire rate are shot in bursts.
    /// </summary>
    [DataField]
    public float BurstFireRate = 4.5f;

    [DataField]
    public int BurstMin = 3;

    [DataField]
    public int BurstMax = 6;

    [DataField]
    public float BurstPauseMin = 0.35f;

    [DataField]
    public float BurstPauseMax = 0.8f;

    [ViewVariables]
    public EntityUid? Target;

    [ViewVariables]
    public TimeSpan AimSince;

    [ViewVariables]
    public TimeSpan LastSeen;

    [ViewVariables]
    public TimeSpan? LastHurt;

    [ViewVariables]
    public int BurstLeft;

    [ViewVariables]
    public TimeSpan BurstPauseUntil;
}
