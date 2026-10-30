using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Managers;

public sealed record SootProtocolEntry(string Name, string Admin, string? Reason, DateTime Started, DateTime? Expires, bool AdminFun = false);

internal sealed partial class ChatManager
{
    [Dependency] private readonly IResourceManager _resources = default!;

    private static readonly ResPath SootProtocolSavePath = new("/soot_contingency_protocol.json");

    private readonly Dictionary<NetUserId, SootProtocolEntry> _sootProtocol = new();

    public IReadOnlyDictionary<NetUserId, SootProtocolEntry> SootProtocolEntries
    {
        get
        {
            PruneExpiredSootProtocols();
            return _sootProtocol;
        }
    }

    public void ActivateSootProtocol(NetUserId user, SootProtocolEntry entry)
    {
        _sootProtocol[user] = entry;
        SaveSootProtocols();
    }

    public bool SetSootProtocolAdminFun(NetUserId user, bool enabled)
    {
        if (!TryGetSootProtocol(user, out var entry))
            return false;

        _sootProtocol[user] = entry with { AdminFun = enabled };
        SaveSootProtocols();
        return true;
    }

    public bool DeactivateSootProtocol(NetUserId user)
    {
        if (!_sootProtocol.Remove(user))
            return false;

        SaveSootProtocols();
        return true;
    }

    public bool TryGetSootProtocol(NetUserId user, [NotNullWhen(true)] out SootProtocolEntry? entry)
    {
        if (!_sootProtocol.TryGetValue(user, out entry))
            return false;

        if (entry.Expires is not { } expires || expires > DateTime.UtcNow)
            return true;

        _sootProtocol.Remove(user);
        SaveSootProtocols();
        entry = null;
        return false;
    }

    public bool IsUnderSootProtocol(ICommonSession player)
    {
        if (!_sootProtocol.ContainsKey(player.UserId))
            return false;

        if (TryGetSootProtocol(player.UserId, out _))
            return true;

        DispatchServerMessage(player, Loc.GetString("soot-protocol-expired"), suppressLog: true);
        return false;
    }

    public bool SootProtocolBlocks(ICommonSession player, string message, string blockedLocId = "soot-protocol-blocked")
    {
        if (!IsUnderSootProtocol(player) || IsEmojiOnly(message))
            return false;

        DispatchServerMessage(player, Loc.GetString(blockedLocId), suppressLog: true);
        return true;
    }

    private void PruneExpiredSootProtocols()
    {
        var now = DateTime.UtcNow;
        var pruned = false;
        foreach (var (user, entry) in _sootProtocol)
        {
            if (entry.Expires is { } expires && expires <= now)
                pruned |= _sootProtocol.Remove(user);
        }

        if (pruned)
            SaveSootProtocols();
    }

    private void LoadSootProtocols()
    {
        try
        {
            if (!_resources.UserData.TryReadAllText(SootProtocolSavePath, out var json))
                return;

            var data = JsonSerializer.Deserialize<Dictionary<Guid, SootProtocolEntry>>(json)
                ?? throw new JsonException("Missing Soot Contingency Protocol entries.");

            foreach (var (user, entry) in data)
            {
                _sootProtocol[new NetUserId(user)] = entry;
            }

            PruneExpiredSootProtocols();
            _sawmill.Info($"Loaded {_sootProtocol.Count} Soot Contingency Protocol entries.");
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to load Soot Contingency Protocol entries: {e}");

            try
            {
                var broken = SootProtocolSavePath.WithName($"soot_contingency_protocol.broken-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                _resources.UserData.Rename(SootProtocolSavePath, broken);
                _sawmill.Error($"Moved the unreadable file to {broken}.");
            }
            catch (Exception renameError)
            {
                _sawmill.Error($"Couldn't move the unreadable Soot Contingency Protocol file aside: {renameError}");
            }
        }
    }

    private void SaveSootProtocols()
    {
        try
        {
            var data = _sootProtocol.ToDictionary(kv => kv.Key.UserId, kv => kv.Value);
            _resources.UserData.WriteAllText(SootProtocolSavePath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to save Soot Contingency Protocol entries: {e}");
        }
    }

    public static bool IsEmojiOnly(string message)
    {
        var any = false;
        foreach (var rune in message.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
                continue;

            var value = rune.Value;

            if (value is 0x200D or 0xFE0E or 0xFE0F or 0x20E3 or (>= 0xE0020 and <= 0xE007F))
                continue;

            if (!IsEmojiRune(value))
                return false;

            any = true;
        }

        return any;
    }

    private static bool IsEmojiRune(int value)
    {
        if (value is >= 0x1F100 and <= 0x1F1FF or >= 0x2776 and <= 0x2793)
            return false;

        return value is >= 0x1F000 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF // misc symbols, dingbats
            or >= 0x2300 and <= 0x23FF // misc technical (⌚ ⏰ ⏳)
            or >= 0x2B00 and <= 0x2BFF // misc symbols and arrows (⭐ ⬆)
            or >= 0x2190 and <= 0x21FF // arrows
            or >= 0x25A0 and <= 0x25FF // geometric shapes (▶ ◀)
            or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x3030 or 0x303D or 0x3297 or 0x3299;
    }
}
