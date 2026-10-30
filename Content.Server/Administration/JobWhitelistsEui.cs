using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Server.Players.JobWhitelist;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Content.Shared.Players;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

public sealed class JobWhitelistsEui : BaseEui
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly JobWhitelistManager _jobWhitelist = default!;
    [Dependency] private readonly PlayTimeTrackingManager _playtime = default!;

    private readonly ISawmill _sawmill;

    public NetUserId PlayerId;
    public string PlayerName;

    public HashSet<ProtoId<JobPrototype>> Whitelists = new();

    private readonly HashSet<string> _keys = new();

    private bool? _serverWhitelisted;

    public JobWhitelistsEui(NetUserId playerId, string playerName)
    {
        IoCManager.InjectDependencies(this);

        _sawmill = _log.GetSawmill("admin.job_whitelists_eui");

        PlayerId = playerId;
        PlayerName = playerName;
    }

    public async void LoadWhitelists()
    {
        _keys.UnionWith(await _db.GetJobWhitelists(PlayerId.UserId));
        _serverWhitelisted = await _db.GetWhitelistStatusAsync(PlayerId);

        RefreshJobs();
        StateDirty();
    }

    public override EuiStateBase GetNewState()
    {
        return new JobWhitelistsEuiState(PlayerName, Whitelists, _serverWhitelisted);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (_serverWhitelisted == null)
            return;

        switch (msg)
        {
            case SetJobWhitelistsMessage jobs:
                if (CheckPermission())
                    SetJobs(jobs.Jobs, jobs.Whitelisting);
                break;
            case SetServerWhitelistedMessage server:
                if (CheckPermission())
                    SetServerWhitelisted(server.Whitelisted);
                break;
        }
    }

    private bool CheckPermission()
    {
        if (_admin.HasAdminFlag(Player, AdminFlags.Whitelist))
            return true;

        _sawmill.Warning($"{Player.Name} ({Player.UserId}) tried to change whitelists for {PlayerName} without whitelists flag");
        return false;
    }

    private void SetJobs(List<ProtoId<JobPrototype>> jobs, bool whitelisting)
    {
        // Grouped jobs share one entry, so toggling any of them toggles the whole group.
        var keys = jobs
            .Select(job => _proto.TryIndex(job, out var proto) ? proto.WhitelistKey : null)
            .OfType<string>()
            .Where(key => _keys.Contains(key) != whitelisting)
            .Distinct()
            .Select(key => new ProtoId<JobPrototype>(key))
            .ToList();

        if (keys.Count == 0)
            return;

        if (whitelisting)
        {
            _jobWhitelist.AddWhitelists(PlayerId, keys);
            _keys.UnionWith(keys.Select(key => key.Id));
        }
        else
        {
            _jobWhitelist.RemoveWhitelists(PlayerId, keys);
            _keys.ExceptWith(keys.Select(key => key.Id));
        }

        var verb = whitelisting ? "added" : "removed";
        _sawmill.Info($"{Player.Name} ({Player.UserId}) {verb} whitelists for {string.Join(", ", keys)} to player {PlayerName} ({PlayerId.UserId})");

        RefreshJobs();
        StateDirty();
    }

    private async void SetServerWhitelisted(bool whitelisted)
    {
        if (_serverWhitelisted == whitelisted)
            return;

        _serverWhitelisted = whitelisted;
        StateDirty();

        if (whitelisted)
            await _db.AddToWhitelistAsync(PlayerId);
        else
            await _db.RemoveFromWhitelistAsync(PlayerId);

        if (_players.TryGetSessionById(PlayerId, out var session) && session.ContentData() is { } data)
        {
            data.Whitelisted = whitelisted;
            _playtime.QueueSendWhitelist(session);
        }

        var verb = whitelisted ? "added" : "removed";
        _sawmill.Info($"{Player.Name} ({Player.UserId}) {verb} server whitelist for player {PlayerName} ({PlayerId.UserId})");
    }

    private void RefreshJobs()
    {
        Whitelists.Clear();
        foreach (var job in _proto.EnumeratePrototypes<JobPrototype>())
        {
            if (_keys.Contains(job.WhitelistKey))
                Whitelists.Add(job.ID);
        }
    }
}
