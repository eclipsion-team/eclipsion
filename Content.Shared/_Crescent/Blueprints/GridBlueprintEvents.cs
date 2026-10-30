using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Blueprints;

[Serializable, NetSerializable]
public sealed class RequestGridBlueprintEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class GridBlueprintEvent : EntityEventArgs
{
    public string GridName;
    public byte[] Data;

    public GridBlueprintEvent(string gridName, byte[] data)
    {
        GridName = gridName;
        Data = data;
    }
}

[Serializable, NetSerializable]
public sealed class GridBlueprintFailedEvent : EntityEventArgs
{
    public string Reason;

    public GridBlueprintFailedEvent(string reason)
    {
        Reason = reason;
    }
}
