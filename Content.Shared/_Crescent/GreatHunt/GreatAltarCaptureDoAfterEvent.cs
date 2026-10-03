using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.GreatHunt;

/// <summary>
/// Raised on a Great Altar when someone finishes — or breaks off — claiming it for their faction.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class GreatAltarCaptureDoAfterEvent : SimpleDoAfterEvent
{
}
