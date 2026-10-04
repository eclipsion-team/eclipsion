using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.Crescent.Psionics;

/// <summary>
/// Hunter's Sense. Tells a psion hunter whether a psion is close, and roughly where. A trained hunter's instinct,
/// not a psionic power: it needs no psionics, makes no glimmer and works inside a null field.
/// </summary>
public sealed partial class PsionHunterSenseActionEvent : InstantActionEvent;

/// <summary>
/// Scrutiny. The hunter studies one person until they can tell whether the noosphere touches them.
/// </summary>
public sealed partial class PsionHunterScrutinyActionEvent : EntityTargetActionEvent;

[Serializable, NetSerializable]
public sealed partial class PsionHunterScrutinyDoAfterEvent : SimpleDoAfterEvent;

/// <summary>
/// Null Ward. A hunt leader holds a null field around themselves for a while, cutting every psion in it off from
/// the noosphere.
/// </summary>
public sealed partial class NullWardActionEvent : InstantActionEvent;
