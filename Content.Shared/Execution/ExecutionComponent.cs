using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared.Execution;

/// <summary>
/// Added to entities that can be used to execute another target.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ExecutionComponent : Component
{
    /// <summary>
    /// How long the execution duration lasts.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float DoAfterDuration = 5f;

    /// <summary>
    /// Arbitrarily chosen number to multiply damage by, used to deal reasonable amounts of damage to a victim of an execution.
    /// /// </summary>
    [DataField, AutoNetworkedField]
    public float DamageMultiplier = 9f;

    /// <summary>
    /// Shown to the person performing the melee execution (attacker) upon starting a melee execution.
    /// </summary>
    [DataField]
    public LocId InternalMeleeExecutionMessage = "execution-popup-melee-initial-internal";

    /// <summary>
    /// Shown to bystanders and the victim of a melee execution when a melee execution is started.
    /// </summary>
    [DataField]
    public LocId ExternalMeleeExecutionMessage = "execution-popup-melee-initial-external";

    /// <summary>
    /// Shown to the attacker upon completion of a melee execution.
    /// </summary>
    [DataField]
    public LocId CompleteInternalMeleeExecutionMessage = "execution-popup-melee-complete-internal";

    /// <summary>
    /// Shown to bystanders and the victim of a melee execution when a melee execution is completed.
    /// </summary>
    [DataField]
    public LocId CompleteExternalMeleeExecutionMessage = "execution-popup-melee-complete-external";

    /// <summary>
    /// Shown to the attacker when readying a gun execution.
    /// </summary>
    [DataField]
    public LocId InternalGunExecutionMessage = "execution-popup-gun-initial-internal";

    /// <summary>
    /// Shown to bystanders and the victim of a gun execution when it is started.
    /// </summary>
    [DataField]
    public LocId ExternalGunExecutionMessage = "execution-popup-gun-initial-external";

    /// <summary>
    /// Shown to the attacker upon completion of a gun execution.
    /// </summary>
    [DataField]
    public LocId CompleteInternalGunExecutionMessage = "execution-popup-gun-complete-internal";

    /// <summary>
    /// Shown to bystanders and the victim of a gun execution when it is completed.
    /// </summary>
    [DataField]
    public LocId CompleteExternalGunExecutionMessage = "execution-popup-gun-complete-external";

    /// <summary>
    /// Shown to the attacker when a gun execution fails because the weapon has nothing to fire.
    /// </summary>
    [DataField]
    public LocId EmptyGunExecutionMessage = "execution-popup-gun-empty";

    /// <summary>
    /// Shown to the attacker when a loaded gun refuses to fire (safety, needs wielding, pacified...).
    /// </summary>
    [DataField]
    public LocId CannotFireGunExecutionMessage = "execution-popup-gun-cannot-fire";

    /// <summary>
    /// Shown to the person performing the self execution when starting one.
    /// </summary>
    [DataField]
    public LocId InternalSelfExecutionMessage = "execution-popup-self-initial-internal";

    /// <summary>
    /// Shown to bystanders near a self execution when one is started.
    /// </summary>
    [DataField]
    public LocId ExternalSelfExecutionMessage = "execution-popup-self-initial-external";

    /// <summary>
    /// Shown to the person performing a self execution upon completion of a do-after or on use of /suicide with a weapon that has the Execution component.
    /// </summary>
    [DataField]
    public LocId CompleteInternalSelfExecutionMessage = "execution-popup-self-complete-internal";

    /// <summary>
    /// Shown to bystanders when a self execution is completed or a suicide via execution weapon happens nearby.
    /// </summary>
    [DataField]
    public LocId CompleteExternalSelfExecutionMessage = "execution-popup-self-complete-external";

    /// <summary>
    /// Track played for the whole execution channel. Stops as soon as the execution completes or is
    /// interrupted.
    /// </summary>
    [DataField]
    public SoundSpecifier? ExecutionSound = new SoundPathSpecifier("/Audio/_Crescent/execution.ogg");

    /// <summary>
    /// The currently-playing execution track entity, kept so it can be stopped on interrupt/completion.
    /// Server-side bookkeeping; not networked.
    /// </summary>
    public EntityUid? ExecutionStream;

    // Not networked because this is transient inside of a tick.
    /// <summary>
    /// True if it is currently executing for handlers.
    /// </summary>
    [DataField]
    public bool Executing = false;
}
