namespace Content.Server._Crescent.PoliticalMap;

[RegisterComponent]
public sealed partial class PoliticalMapStationComponent : Component
{
    [DataField]
    public string Faction = string.Empty;
}
