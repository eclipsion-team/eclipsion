using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Chat;

// soot contingency protocol adminfun: the player blurts out something nice every now and then, talking or not
public sealed partial class SootProtocolAdminFunSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ChatSystem _chat = default!;

    private const int MinDelay = 25;
    private const int MaxDelay = 75;

    private readonly Dictionary<NetUserId, TimeSpan> _next = new();
    private readonly List<NetUserId> _stale = new();
    private readonly List<NetUserId> _fun = new();
    private TimeSpan _nextCheck;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextCheck)
            return;

        _nextCheck = _timing.CurTime + TimeSpan.FromSeconds(1);

        // copied out first, speaking runs other systems that may touch the protocol list
        var entries = _chatManager.SootProtocolEntries;
        _fun.Clear();
        foreach (var (user, entry) in entries)
        {
            if (entry.AdminFun)
                _fun.Add(user);
        }

        foreach (var user in _fun)
        {
            // first one comes quick so the admin sees it working
            if (!_next.TryGetValue(user, out var next))
            {
                _next[user] = _timing.CurTime + TimeSpan.FromSeconds(_random.Next(5, 15));
                continue;
            }

            if (_timing.CurTime < next)
                continue;

            _next[user] = _timing.CurTime + TimeSpan.FromSeconds(_random.Next(MinDelay, MaxDelay));

            if (!_players.TryGetSessionById(user, out var session) || session.AttachedEntity is not { } body)
                continue;

            _chat.TrySendInGameICMessage(body, _chat.PickSootProtocolPhrase(true), InGameICChatType.Speak,
                ChatTransmitRange.Normal, checkRadioPrefix: false);
        }

        _stale.Clear();
        foreach (var user in _next.Keys)
        {
            if (!_fun.Contains(user))
                _stale.Add(user);
        }

        foreach (var user in _stale)
        {
            _next.Remove(user);
        }
    }
}
