using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Administration;
using Content.Shared.Customization.Systems;
using Content.Shared.Database;
using Content.Shared.Eui;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

public sealed partial class PlaytimeEui : BaseEui
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly PlayTimeTrackingManager _playtime = default!;

    private readonly ISawmill _sawmill;

    private NetUserId? _playerId;
    private string? _playerName;
    private Dictionary<string, TimeSpan>? _trackers;
    private string? _status;

    private int _selection;

    public PlaytimeEui()
    {
        IoCManager.InjectDependencies(this);

        _sawmill = _log.GetSawmill("admin.playtime_eui");
    }

    public override EuiStateBase GetNewState()
    {
        var online = _playerId is { } id && _players.TryGetSessionById(id, out _);
        return new PlaytimeEuiState(_playerName, online, _trackers, _status);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        switch (msg)
        {
            case SelectPlaytimePlayerMessage select:
                if (CheckPermission())
                    SelectPlayer(select.Player);
                break;
            case ChangePlaytimeMessage change:
                if (CheckPermission())
                    Change(change.Trackers, change.Time, change.Overwrite);
                break;
            case MeetPlaytimeRequirementsMessage meet:
                if (CheckPermission())
                    Apply(times => MissingRequirements(times, meet.Jobs));
                break;
        }
    }

    private bool CheckPermission()
    {
        if (_admin.HasAdminFlag(Player, AdminFlags.Admin))
            return true;

        _sawmill.Warning($"{Player.Name} ({Player.UserId}) tried to change playtime for {_playerName} without admin flag");
        return false;
    }

    public async void SelectPlayer(string player)
    {
        var selection = ++_selection;
        _trackers = null;
        _status = null;
        StateDirty();

        var located = await _locator.LookupIdByNameOrIdAsync(player.Trim());
        if (IsShutDown || selection != _selection)
            return;

        if (located == null)
        {
            _playerId = null;
            _playerName = null;
            _status = Loc.GetString("playtime-window-player-not-found", ("player", player));
            StateDirty();
            return;
        }

        _playerId = located.UserId;
        _playerName = located.Username;
        await Reload(selection);
    }

    private async Task Reload(int selection)
    {
        if (_playerId is not { } userId)
            return;

        var times = await GetTimes(userId);
        if (IsShutDown || selection != _selection)
            return;

        _trackers = times;
        StateDirty();
    }

    private async Task<Dictionary<string, TimeSpan>> GetTimes(NetUserId userId)
    {
        if (_players.TryGetSessionById(userId, out var session) && _playtime.TryGetTrackerTimes(session, out var times))
        {
            _playtime.FlushTracker(session);
            return new Dictionary<string, TimeSpan>(times);
        }

        return (await _db.GetPlayTimes(userId)).ToDictionary(time => time.Tracker, time => time.TimeSpent);
    }

    private void Change(List<ProtoId<PlayTimeTrackerPrototype>> trackers, TimeSpan time, bool overwrite)
    {
        if (time.Duration() > PlayTimeTrackingShared.MaxAdminTime)
            return;

        var valid = trackers
            .Where(tracker => _proto.HasIndex(tracker))
            .Select(tracker => tracker.Id)
            .Distinct()
            .ToList();

        Apply(times => valid.ToDictionary(
            tracker => tracker,
            tracker =>
            {
                var result = overwrite ? time : times.GetValueOrDefault(tracker) + time;
                return result < TimeSpan.Zero ? TimeSpan.Zero : result;
            }));
    }

    private Dictionary<string, TimeSpan> MissingRequirements(IReadOnlyDictionary<string, TimeSpan> current,
        List<ProtoId<JobPrototype>> jobs)
    {
        var times = new Dictionary<string, TimeSpan>(current);
        var changed = new Dictionary<string, TimeSpan>();

        void TopUp(string tracker, TimeSpan min)
        {
            if (string.IsNullOrEmpty(tracker) || times.GetValueOrDefault(tracker) >= min)
                return;

            times[tracker] = min;
            changed[tracker] = min;
        }

        foreach (var id in jobs.Distinct())
        {
            if (!_proto.TryIndex(id, out var job) || job.Requirements == null)
                continue;

            foreach (var requirement in job.Requirements)
            {
                if (requirement.Inverted)
                    continue;

                switch (requirement)
                {
                    case CharacterPlaytimeRequirement playtime:
                        TopUp(playtime.Tracker, playtime.Min);
                        break;
                    case CharacterOverallTimeRequirement overall:
                        TopUp(PlayTimeTrackingShared.TrackerOverall, overall.Min);
                        break;
                    case CharacterDepartmentTimeRequirement department:
                        if (!_proto.TryIndex(department.Department, out var proto))
                            break;

                        var trackers = proto.Roles
                            .Select(role => _proto.TryIndex(role, out var roleJob) ? roleJob.PlayTimeTracker.Id : null)
                            .Where(tracker => !string.IsNullOrEmpty(tracker))
                            .OfType<string>()
                            .ToList();

                        var total = trackers.Aggregate(TimeSpan.Zero, (sum, tracker) => sum + times.GetValueOrDefault(tracker));
                        if (trackers.Count > 0 && total < department.Min)
                            TopUp(trackers[0], times.GetValueOrDefault(trackers[0]) + department.Min - total);
                        break;
                }
            }
        }

        return changed;
    }

    private async void Apply(Func<IReadOnlyDictionary<string, TimeSpan>, Dictionary<string, TimeSpan>> change)
    {
        if (_playerId is not { } userId || _trackers == null)
            return;

        var selection = _selection;
        var playerName = _playerName;
        Dictionary<string, TimeSpan> changed;

        if (_players.TryGetSessionById(userId, out var session))
        {
            if (!_playtime.TryGetTrackerTimes(session, out var times))
            {
                _status = Loc.GetString("playtime-window-still-loading");
                StateDirty();
                return;
            }

            _playtime.FlushTracker(session);
            changed = Changed(times, change(times));

            foreach (var (tracker, time) in changed)
            {
                _playtime.SetTrackerTime(session, tracker, time);
            }

            _playtime.QueueSendTimers(session);
            _playtime.SaveSession(session);
        }
        else
        {
            _trackers = null;
            StateDirty();

            var times = (await _db.GetPlayTimes(userId)).ToDictionary(time => time.Tracker, time => time.TimeSpent);
            changed = Changed(times, change(times));

            if (changed.Count > 0)
                await _db.UpdatePlayTimes(changed.Select(pair => new PlayTimeUpdate(userId, pair.Key, pair.Value)).ToList());
        }

        if (changed.Count > 0)
        {
            var summary = string.Join(", ", changed.Select(pair => $"{pair.Key} = {pair.Value}"));
            _adminLog.Add(LogType.AdminCommands, LogImpact.Medium,
                $"{Player:actor} changed playtime of {playerName} ({userId}): {summary}");
        }

        if (IsShutDown || selection != _selection)
            return;

        _status = changed.Count == 0 ? Loc.GetString("playtime-window-nothing-changed") : null;
        await Reload(selection);
    }

    private static Dictionary<string, TimeSpan> Changed(IReadOnlyDictionary<string, TimeSpan> current,
        Dictionary<string, TimeSpan> wanted)
    {
        return wanted
            .Where(pair => pair.Value != current.GetValueOrDefault(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
