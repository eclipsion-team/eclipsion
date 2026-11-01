using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Serializable, NetSerializable]
public sealed class PersistentPoiDeconstructRequestEvent : EntityEventArgs
{
    public NetEntity Target;

    public PersistentPoiDeconstructRequestEvent(NetEntity target)
    {
        Target = target;
    }
}

[Serializable, NetSerializable]
public sealed class PersistentPoiDeconstructTileRequestEvent : EntityEventArgs
{
    public NetCoordinates Location;

    public PersistentPoiDeconstructTileRequestEvent(NetCoordinates location)
    {
        Location = location;
    }
}
