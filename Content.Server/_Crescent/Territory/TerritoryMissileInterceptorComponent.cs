namespace Content.Server._Crescent.Territory;

/// <summary>
/// A territory point-defence gun that turns on incoming missiles before anything else, even off a manned console.
/// </summary>
[RegisterComponent]
public sealed partial class TerritoryMissileInterceptorComponent : Component
{
    // while this is ahead, manual and ship fire orders leave the gun alone
    [ViewVariables]
    public TimeSpan EngagedUntil;
}
