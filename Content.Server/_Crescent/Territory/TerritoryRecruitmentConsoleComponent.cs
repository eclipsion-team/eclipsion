namespace Content.Server._Crescent.Territory;

[RegisterComponent]
public sealed partial class TerritoryRecruitmentConsoleComponent : Component
{
    // per soldier, pod included
    [DataField]
    public int GarrisonPrice = 2000;

    [DataField]
    public int EscortPrice = 3000;

    // most of the treasury one member may draw on in a round
    [DataField]
    public float TreasuryShare = 0.25f;
}
