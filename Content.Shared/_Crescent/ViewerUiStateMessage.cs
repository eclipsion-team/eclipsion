using Robust.Shared.Serialization;

namespace Content.Shared._Crescent;

// state that differs per person looking at the ui (rank, own money), sent to just that one
[Serializable, NetSerializable]
public sealed class ViewerUiStateMessage(BoundUserInterfaceState state) : BoundUserInterfaceMessage
{
    public BoundUserInterfaceState State { get; private set; } = state;
}
