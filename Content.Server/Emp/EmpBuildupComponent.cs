namespace Content.Server.Emp;

// eclipsion - counts emp hits that need several in a row to shut the target down (ship gun rounds)
[RegisterComponent, Access(typeof(EmpSystem))]
public sealed partial class EmpBuildupComponent : Component
{
    [ViewVariables]
    public int Hits;

    [ViewVariables]
    public TimeSpan LastHit;
}
