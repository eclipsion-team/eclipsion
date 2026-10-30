namespace Content.Server._Crescent.NPC;

[RegisterComponent, Access(typeof(NpcGarrisonDormancySystem))]
public sealed partial class NpcGarrisonDormancyComponent : Component
{
    [DataField]
    public float Range = 40f;

    [DataField]
    public TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    [ViewVariables]
    public TimeSpan NextCheck;

    [DataField]
    public bool Dormant;
}
