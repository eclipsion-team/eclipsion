using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.GreatHunt;

/// <summary>
/// Appearance keys for the Great Altar. <see cref="Holder"/> carries the holding faction's ID as a string, so the
/// prototype's GenericVisualizer can swap the altar's RSI per faction. Unset while nobody holds it.
/// </summary>
[Serializable, NetSerializable]
public enum GreatAltarVisuals : byte
{
    Holder,
}
