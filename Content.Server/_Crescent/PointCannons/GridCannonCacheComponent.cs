namespace Content.Server.PointCannons;

[RegisterComponent]
public sealed partial class GridCannonCacheComponent : Component
{
    [ViewVariables]
    public HashSet<EntityUid> CachedCannons = new();

    [ViewVariables]
    public bool CacheDirty = true;
}