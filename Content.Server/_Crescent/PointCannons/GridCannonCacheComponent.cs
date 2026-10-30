namespace Content.Server.PointCannons;

[RegisterComponent]
public sealed partial class GridCannonCacheComponent : Component
{
    [ViewVariables]
    public HashSet<EntityUid> CachedCannons = new();

    [ViewVariables]
    public bool CacheDirty = true;

    [ViewVariables]
    public int Revision;

    [ViewVariables]
    public HashSet<EntityUid> ControlledCannons = new();

    [ViewVariables]
    public int ControlledRevision = -1;

    [ViewVariables]
    public int ControlledLimit = -1;
}
