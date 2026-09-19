namespace Content.Server._Crescent.DroneControl;

/// <summary>
///     Prevents a newly produced drone grid from dealing or receiving shuttle impact damage.
///     Kept on the grid so protection survives the loss of its control server.
/// </summary>
[RegisterComponent]
public sealed partial class DroneSpawnProtectionComponent : Component
{
    [ViewVariables]
    public TimeSpan ExpiresAt;
}
