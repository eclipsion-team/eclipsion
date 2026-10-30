namespace Content.Server.Emp;

/// <summary>
/// Upon being triggered will EMP area around it.
/// </summary>
[RegisterComponent]
[Access(typeof(EmpSystem))]
public sealed partial class EmpOnTriggerComponent : Component
{
    [DataField("range"), ViewVariables(VVAccess.ReadWrite)]
    public float Range = 1.0f;

    /// <summary>
    /// How much energy will be consumed per battery in range
    /// </summary>
    [DataField("energyConsumption"), ViewVariables(VVAccess.ReadWrite)]
    public float EnergyConsumption;

    /// <summary>
    /// How long it disables targets in seconds
    /// </summary>
    [DataField("disableDuration"), ViewVariables(VVAccess.ReadWrite)]
    public float DisableDuration = 10f; // eclipsion - was 60, grenades left whole rooms dead for a minute

    // eclipsion - one hit takes at most this share of a battery, so a single emp never empties it outright
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float MaxDrainFraction = 0.5f;

    // eclipsion - hits within EmpSystem.BuildupWindow before a target shuts down. ship guns use 2-3, the hits
    // before that only drain
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int HitsToDisable = 1;
}
