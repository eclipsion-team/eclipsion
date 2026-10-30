using System.Diagnostics.CodeAnalysis;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Content.Shared.Players.RateLimiting;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Chat.Managers
{
    public interface IChatManager : ISharedChatManager
    {
        /// <summary>
        ///     Dispatch a server announcement to every connected player.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="colorOverride">Override the color of the message being sent.</param>
        void DispatchServerAnnouncement(string message, Color? colorOverride = null);

        void DispatchServerMessage(ICommonSession player, string message, bool suppressLog = false);

        void TrySendOOCMessage(ICommonSession player, string message, OOCChatType type);

        void SendHookOOC(string sender, string message);
        void SendAdminAnnouncement(string message, AdminFlags? flagBlacklist = null, AdminFlags? flagWhitelist = null);
        void SendAdminAnnouncementMessage(ICommonSession player, string message, bool suppressLog = true);

        void ChatMessageToOne(ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat,
            INetChannel client, Color? colorOverride = null, bool recordReplay = false, string? audioPath = null, float audioVolume = 0, NetUserId? author = null, bool ignoreChatStack = false);

        void ChatMessageToMany(ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat, bool recordReplay,
            IEnumerable<INetChannel> clients, Color? colorOverride = null, string? audioPath = null, float audioVolume = 0, NetUserId? author = null, bool ignoreChatStack = false);

        void ChatMessageToManyFiltered(Filter filter, ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat, bool recordReplay, Color? colorOverride, string? audioPath = null, float audioVolume = 0, bool ignoreChatStack = false);

        void ChatMessageToAll(ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat, bool recordReplay, Color? colorOverride = null, string? audioPath = null, float audioVolume = 0, NetUserId? author = null, bool ignoreChatStack = false);

        bool MessageCharacterLimit(ICommonSession player, string message);

        void DeleteMessagesBy(ICommonSession player);

        [return: NotNullIfNotNull(nameof(author))]
        ChatUser? EnsurePlayer(NetUserId? author);

        /// <summary>
        /// Called when a player sends a chat message to handle rate limits.
        /// Will update counts and do necessary actions if breached.
        /// </summary>
        /// <param name="player">The player sending a chat message.</param>
        /// <returns>False if the player has violated rate limits and should be blocked from sending further messages.</returns>
        RateLimitStatus HandleRateLimit(ICommonSession player);

        /// <summary>
        /// Crescent - checks a player's message against the chat filter, and if it's caught,
        /// tells the player, logs it and alerts admins for slurs.
        /// </summary>
        /// <returns>True if the message was blocked and must not be sent.</returns>
        bool IsMessageFiltered(ICommonSession player, string message, string? source = null);

        IReadOnlyDictionary<NetUserId, SootProtocolEntry> SootProtocolEntries { get; }
        void ActivateSootProtocol(NetUserId user, SootProtocolEntry entry);
        bool SetSootProtocolAdminFun(NetUserId user, bool enabled);
        bool DeactivateSootProtocol(NetUserId user);
        bool TryGetSootProtocol(NetUserId user, [NotNullWhen(true)] out SootProtocolEntry? entry);

        bool IsUnderSootProtocol(ICommonSession player);

        bool SootProtocolBlocks(ICommonSession player, string message, string blockedLocId = "soot-protocol-blocked");
    }
}
