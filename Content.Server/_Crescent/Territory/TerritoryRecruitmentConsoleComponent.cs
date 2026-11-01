namespace Content.Server._Crescent.Territory;

[RegisterComponent]
public sealed partial class TerritoryRecruitmentConsoleComponent : Component
{
    // on top of the role's price, per escort
    [DataField]
    public int EscortSurcharge = 1000;

    // most of the treasury one member may draw on in a round
    [DataField]
    public float TreasuryShare = 0.25f;
}
