using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Shuttles.Events;

/// <summary>
/// Sets the console grid's turn rate, as a fraction of its full rotation speed.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShuttleTurnRateMessage(float turnRate) : BoundUserInterfaceMessage
{
    public float TurnRate = turnRate;
}
