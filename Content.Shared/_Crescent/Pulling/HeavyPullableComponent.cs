using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.Pulling;

/// <summary>
/// Lets something far heavier than a person (a mech, say) still be dragged around by one, while making
/// it obvious that it weighs a lot.
/// </summary>
/// <remarks>
/// Pulling works through a physics distance joint, and the joint solver splits its correction by inverse
/// mass. Against a 13 tonne body a 70 kg puller simply cannot move - the joint cancels all of their
/// velocity every tick. So while the entity is being pulled its fixtures are scaled down to
/// <see cref="DragMass"/>, and restored the moment the pull ends. The weight is then conveyed through
/// the joint lag at that mass plus the puller speed penalties below.
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HeavyPullableComponent : Component
{
    /// <summary>
    /// The mass, in kg, the body pretends to have while being dragged. Bodies already lighter than this are left alone.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float DragMass = 150f;

    /// <summary>Walk speed multiplier for whoever is dragging this.</summary>
    [DataField, AutoNetworkedField]
    public float DragWalkSpeedMultiplier = 0.6f;

    /// <summary>Sprint speed multiplier for whoever is dragging this. Harsher than walking; you cannot run with it.</summary>
    [DataField, AutoNetworkedField]
    public float DragSprintSpeedMultiplier = 0.4f;

    /// <summary>
    /// Whether this can be dragged while something is piloting it. Only meaningful on mechs.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool PullableWhilePiloted;

    // --- Runtime state (not authored in YAML) -----------------------------

    /// <summary>
    /// The factor every fixture density was multiplied by when the pull started, or 1 when nothing is applied.
    /// Networked together with the fixtures so a prediction re-run never scales the body twice.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public float AppliedDensityScale = 1f;
}
