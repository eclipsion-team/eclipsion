using Content.Shared.Actions;
using Content.Shared.Chat;
using Content.Shared.Magic;
using Robust.Shared.Prototypes;

namespace Content.Shared.Psionics.Events;

/// <summary>
/// Conjures one entity per named slot into the performer's hands, equipping it when the slot name is a
/// real one. Originally part of the blood cult spell set; kept after that was removed because the
/// mantis' Summon Black Blade is the only thing that still uses it.
/// </summary>
public sealed partial class SummonEquipmentEvent : InstantActionEvent, ISpeakSpell
{
    /// <summary>
    /// Slot name -> what to conjure into it.
    /// </summary>
    [DataField]
    public Dictionary<string, EntProtoId> Prototypes = new();

    [DataField]
    public string? Speech { get; set; }

    /// <summary>
    /// When false, the summon is abandoned if the performer has no free hand to take it.
    /// </summary>
    [DataField]
    public bool Force { get; set; } = true;

    [DataField]
    public InGameICChatType InvokeChatType = InGameICChatType.Whisper;

    public InGameICChatType ChatType => InGameICChatType.Whisper;
}
