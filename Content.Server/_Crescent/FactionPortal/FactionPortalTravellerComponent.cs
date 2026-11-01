namespace Content.Server._Crescent.FactionPortal;

/// <summary>
/// A player's chosen destination for one portal, and the portal they last came out of so they aren't sent
/// straight back.
/// </summary>
[RegisterComponent]
public sealed partial class FactionPortalTravellerComponent : Component
{
    [ViewVariables]
    public EntityUid? Portal;

    [ViewVariables]
    public EntityUid? Destination;

    [ViewVariables]
    public EntityUid? ArrivedAt;

    [ViewVariables]
    public TimeSpan ArrivalGraceEnd;
}
