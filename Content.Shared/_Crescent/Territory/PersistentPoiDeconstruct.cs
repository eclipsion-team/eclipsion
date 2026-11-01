using Content.Shared.DoAfter;
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

[Serializable, NetSerializable]
public sealed partial class PersistentPoiDeconstructDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class PersistentPoiDeconstructTileDoAfterEvent : DoAfterEvent
{
    [DataField(required: true)]
    public NetCoordinates Location { get; private set; }

    private PersistentPoiDeconstructTileDoAfterEvent() { }

    public PersistentPoiDeconstructTileDoAfterEvent(NetCoordinates location)
    {
        Location = location;
    }

    public override DoAfterEvent Clone() => this;
}
