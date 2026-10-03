using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Religion.Components;

/// <summary>
///     A faction's faith figure. Gets an action that calls out to the faith; every believer nearby answers, so
///     whoever stays silent gives themselves away.
/// </summary>
[RegisterComponent, Access(typeof(ReligiousCallSystem))]
public sealed partial class ReligiousCallerComponent : Component
{
    /// <summary>
    ///     The call action. Which faith it calls is set on the action's event.
    /// </summary>
    [DataField(required: true)]
    public EntProtoId Action;

    [DataField]
    public EntityUid? ActionEntity;
}
