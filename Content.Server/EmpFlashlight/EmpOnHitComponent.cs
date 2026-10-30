namespace Content.Server.EmpFlashlight;

/// <summary>
///     Upon being triggered will EMP target.
/// </summary>
[RegisterComponent]
[Access(typeof(EmpOnHitSystem))]

public sealed partial class EmpOnHitComponent : Component
{
    [DataField]
    public float Range = 1.0f;

    [DataField]
    public float EnergyConsumption;

    [DataField]
    public float DisableDuration = 10f; // eclipsion - was 60

    [DataField]
    public float MaxDrainFraction = 0.5f; // eclipsion - see EmpOnTriggerComponent
}
