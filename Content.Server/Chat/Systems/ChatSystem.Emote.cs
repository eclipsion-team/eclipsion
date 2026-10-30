using System.Collections.Frozen;
using System.Linq;
using Content.Shared.Chat;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Dataset; // eclipsion
using Content.Shared.Random.Helpers; // eclipsion
using Content.Shared.Speech;
using Robust.Shared.Player; // eclipsion
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Chat.Systems;

// emotes using emote prototype
public partial class ChatSystem
{
    private static readonly ProtoId<LocalizedDatasetPrototype> SootProtocolPhrases = "SootProtocolNicePhrases"; // eclipsion
    private static readonly ProtoId<LocalizedDatasetPrototype> SootProtocolFunPhrases = "SootProtocolAdminFunPhrases"; // eclipsion

    private FrozenDictionary<string, EmotePrototype> _wordEmoteDict = FrozenDictionary<string, EmotePrototype>.Empty;
    private IReadOnlyList<string> Punctuation { get; } = new List<string> { ",", ".", "!", "?", "-", "~", "'", "\"", };

    protected override void OnPrototypeReload(PrototypesReloadedEventArgs obj)
    {
        base.OnPrototypeReload(obj);
        if (obj.WasModified<EmotePrototype>())
            CacheEmotes();
    }

    private void CacheEmotes()
    {
        var dict = new Dictionary<string, EmotePrototype>();
        var emotes = _prototypeManager.EnumeratePrototypes<EmotePrototype>();
        foreach (var emote in emotes)
        {
            foreach (var word in emote.ChatTriggers)
            {
                var lowerWord = word.ToLower();
                if (dict.TryGetValue(lowerWord, out var value))
                {
                    var errMsg = $"Duplicate of emote word {lowerWord} in emotes {emote.ID} and {value.ID}";
                    Log.Error(errMsg);
                    continue;
                }

                dict.Add(lowerWord, emote);
            }
        }

        _wordEmoteDict = dict.ToFrozenDictionary();
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> and sends message to chat.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="emoteId">The id of emote prototype. Should has valid <see cref="EmotePrototype.ChatMessages"/></param>
    /// <param name="hideLog">Whether or not this message should appear in the adminlog window</param>
    /// <param name="range">Conceptual range of transmission, if it shows in the chat window, if it shows to far-away ghosts or ghosts at all...</param>
    /// <param name="nameOverride">The name to use for the speaking entity. Usually this should just be modified via <see cref="TransformSpeakerNameEvent"/>. If this is set, the event will not get raised.</param>
    /// <param name="forceEmote">Bypasses whitelist/blacklist/availibility checks for if the entity can use this emote</param>
    public void TryEmoteWithChat(
        EntityUid source,
        string emoteId,
        ChatTransmitRange range = ChatTransmitRange.Normal,
        bool hideLog = false,
        string? nameOverride = null,
        bool ignoreActionBlocker = false,
        bool forceEmote = false
        )
    {
        if (!_prototypeManager.TryIndex<EmotePrototype>(emoteId, out var proto))
            return;
        TryEmoteWithChat(source, proto, range, hideLog: hideLog, nameOverride, ignoreActionBlocker: ignoreActionBlocker, forceEmote: forceEmote);
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> and sends message to chat.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="emote">The emote prototype. Should has valid <see cref="EmotePrototype.ChatMessages"/></param>
    /// <param name="hideLog">Whether or not this message should appear in the adminlog window</param>
    /// <param name="hideChat">Whether or not this message should appear in the chat window</param>
    /// <param name="range">Conceptual range of transmission, if it shows in the chat window, if it shows to far-away ghosts or ghosts at all...</param>
    /// <param name="nameOverride">The name to use for the speaking entity. Usually this should just be modified via <see cref="TransformSpeakerNameEvent"/>. If this is set, the event will not get raised.</param>
    /// <param name="forceEmote">Bypasses whitelist/blacklist/availibility checks for if the entity can use this emote</param>
    public void TryEmoteWithChat(
        EntityUid source,
        EmotePrototype emote,
        ChatTransmitRange range = ChatTransmitRange.Normal,
        bool hideLog = false,
        string? nameOverride = null,
        bool ignoreActionBlocker = false,
        bool forceEmote = false
        )
    {
        if (!forceEmote && !AllowedToUseEmote(source, emote))
            return;

        // check if proto has valid message for chat
        if (emote.ChatMessages.Count != 0)
        {
            // not all emotes are loc'd, but for the ones that are we pass in entity
            var action = Loc.GetString(_random.Pick(emote.ChatMessages), ("entity", source));
            var language = _language.GetLanguage(source);
            SendEntityEmote(source, action, range, nameOverride, language, hideLog: hideLog, checkEmote: false, ignoreActionBlocker: ignoreActionBlocker);
        }

        // do the rest of emote event logic here
        TryEmoteWithoutChat(source, emote, ignoreActionBlocker);
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> without sending any messages to chat.
    /// </summary>
    public void TryEmoteWithoutChat(EntityUid uid, string emoteId, bool ignoreActionBlocker = false)
    {
        if (!_prototypeManager.TryIndex<EmotePrototype>(emoteId, out var proto))
            return;

        TryEmoteWithoutChat(uid, proto, ignoreActionBlocker);
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> without sending any messages to chat.
    /// </summary>
    public void TryEmoteWithoutChat(EntityUid uid, EmotePrototype proto, bool ignoreActionBlocker = false)
    {
        if (!_actionBlocker.CanEmote(uid) && !ignoreActionBlocker)
            return;

        InvokeEmoteEvent(uid, proto);
    }

    /// <summary>
    ///     Tries to find and play relevant emote sound in emote sounds collection.
    /// </summary>
    /// <returns>True if emote sound was played.</returns>
    public bool TryPlayEmoteSound(EntityUid uid, EmoteSoundsPrototype? proto, EmotePrototype emote)
    {
        return TryPlayEmoteSound(uid, proto, emote.ID);
    }

    /// <summary>
    ///     Tries to find and play relevant emote sound in emote sounds collection.
    /// </summary>
    /// <returns>True if emote sound was played.</returns>
    public bool TryPlayEmoteSound(EntityUid uid, EmoteSoundsPrototype? proto, string emoteId)
    {
        if (proto == null)
            return false;

        // try to get specific sound for this emote
        if (!proto.Sounds.TryGetValue(emoteId, out var sound))
        {
            // no specific sound - check fallback
            sound = proto.FallbackSound;
            if (sound == null)
                return false;
        }

        // if general params for all sounds set - use them
        var param = proto.GeneralParams ?? sound.Params;
        _audio.PlayPvs(sound, uid, param);
        return true;
    }
    /// <summary>
    /// Checks if a valid emote was typed, to play sounds and etc and invokes an event.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="textInput"></param>
    private void TryEmoteChatInput(EntityUid uid, string textInput)
    {
        var actionLower = textInput.ToLower();
        // Replace ending punctuation with nothing
        if (Punctuation.Any(punctuation => actionLower.EndsWith(punctuation)))
            actionLower = actionLower.Remove(actionLower.Length - 1);

        if (!_wordEmoteDict.TryGetValue(actionLower, out var emote))
            return;

        if (!AllowedToUseEmote(uid, emote))
            return;

        InvokeEmoteEvent(uid, emote);
    }
    /// <summary>
    /// Checks if we can use this emote based on the emotes whitelist, blacklist, and availibility to the entity.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="emote">The emote being used</param>
    /// <returns></returns>
    private bool AllowedToUseEmote(EntityUid source, EmotePrototype emote)
    {
        // If emote is in AllowedEmotes, it will bypass whitelist and blacklist
        if (TryComp<SpeechComponent>(source, out var speech) &&
            speech.AllowedEmotes.Contains(emote.ID))
            return true;

        // Check the whitelist and blacklist
        if (_whitelistSystem.IsWhitelistFail(emote.Whitelist, source) ||
            _whitelistSystem.IsBlacklistPass(emote.Blacklist, source))
            return false;

        // Check if the emote is available for all
        if (!emote.Available)
            return false;

        return true;
    }

    private bool IsSootProtocolEmote(EntityUid source, string message, InGameICChatType type)
    {
        var trimmed = message.Trim();

        if (type == InGameICChatType.Emote)
        {
            var actionLower = trimmed.ToLower();
            if (Punctuation.Any(punctuation => actionLower.EndsWith(punctuation)))
                actionLower = actionLower.Remove(actionLower.Length - 1);

            return _wordEmoteDict.TryGetValue(actionLower, out var emote) && AllowedToUseEmote(source, emote);
        }

        return _sanitizer.TrySanitizeOutSmilies(trimmed, source, out var rest, out _)
            && string.IsNullOrWhiteSpace(rest);
    }

    public string PickSootProtocolPhrase(ICommonSession player)
    {
        var adminFun = _chatManager.TryGetSootProtocol(player.UserId, out var entry) && entry.AdminFun;
        return PickSootProtocolPhrase(adminFun);
    }

    public string PickSootProtocolPhrase(bool adminFun)
    {
        // adminfun mixes the short ones (thank you, of course, spread love) in with the long ones
        var dataset = adminFun && _random.Prob(0.6f) ? SootProtocolFunPhrases : SootProtocolPhrases;
        return Loc.GetString(_random.Pick(_prototypeManager.Index(dataset)));
    }

    private void InvokeEmoteEvent(EntityUid uid, EmotePrototype proto)
    {
        var ev = new EmoteEvent(proto);
        RaiseLocalEvent(uid, ref ev, true); // goob edit
    }
}

/// <summary>
///     Raised by chat system when entity made some emote.
///     Use it to play sound, change sprite or something else.
/// </summary>
[ByRefEvent]
public struct EmoteEvent
{
    public bool Handled;
    public readonly EmotePrototype Emote;

    public EmoteEvent(EmotePrototype emote)
    {
        Emote = emote;
        Handled = false;
    }
}
