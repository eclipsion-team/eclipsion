using System.Linq;
using System.Text;
using Content.Server._Crescent.Territory;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.NPC.HTN;
using Content.Shared._Crescent.LagMonitor;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.NPC;
using Prometheus;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._Crescent.LagMonitor;

// reads the engine's prometheus timings every tick to see what caused a freeze
public sealed partial class LagMonitorManager
{
    [Dependency] private readonly IEntitySystemManager _sysMan = default!;
    [Dependency] private readonly IEntityManager _ents = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IAdminManager _admins = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    // same names as the engine's so we get its instances
    private static readonly Histogram SystemsHist = Metrics.CreateHistogram("robust_entity_systems_update_usage",
        "Amount of time spent processing each entity system", new HistogramConfiguration
        {
            LabelNames = new[] { "system" },
            Buckets = Histogram.ExponentialBuckets(0.000_001, 1.5, 25)
        });

    private static readonly Histogram TickHist = Metrics.CreateHistogram("robust_server_update_usage",
        "Time usage of the main loop Update()s", new HistogramConfiguration
        {
            LabelNames = new[] { "area" },
            Buckets = Histogram.ExponentialBuckets(0.000_01, 2, 13)
        });

    private static readonly Histogram PvsHist = Metrics.CreateHistogram("robust_game_state_update_usage",
        "Amount of time spent processing different parts of the game state update", new HistogramConfiguration
        {
            LabelNames = new[] { "area" },
            Buckets = Histogram.ExponentialBuckets(0.000_001, 1.5, 25)
        });

    private static readonly Counter SentBytes = Metrics.CreateCounter("robust_net_sent_bytes",
        "Number of bytes sent since server startup.");

    private static readonly Counter RecvBytes = Metrics.CreateCounter("robust_net_recv_bytes",
        "Number of bytes received since server startup.");

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private const int HistoryLength = 120; // 10 min of samples
    private const int MaxHitches = 30;
    private const int TopSystems = 15;
    private const float NetPerPlayerWarnKb = 64f;
    private const int PingWarn = 250;

    private sealed class Tracked
    {
        public string Name = string.Empty;
        public IHistogram Child = default!;
        public double Last;
        public double Delta;
        public double SampleBase;
        public double WindowDelta;
    }

    private readonly List<Tracked> _systems = new();
    private readonly List<Tracked> _areas = new();
    private readonly List<Tracked> _pvs = new();
    private readonly HashSet<string> _known = new();
    private long _selfTime;

    private readonly Stopwatch _watch = new();
    private ISawmill _sawmill = default!;

    private bool _enabled;
    private int _hitchMs;
    private int _alertMs;
    private bool _alertAdmins;
    private int _alertCooldown;

    private TimeSpan _lastTick;
    private bool _hadPlayers;
    private TimeSpan _lastGcPause;
    private TimeSpan _nextSample;
    private TimeSpan _lastSampleTime;
    private uint _lastSampleTick;
    private double _lastSent;
    private double _lastRecv;
    private TimeSpan _sampleGcPause;
    private int _lastGen2;
    private TimeSpan _nextAlert;
    private bool _wasOverloaded;

    private readonly List<LagSample> _samples = new();
    private readonly List<LagHitch> _hitches = new();
    private readonly List<string> _findings = new();
    private readonly HashSet<LagMonitorEui> _open = new();

    public void Initialize()
    {
        _sawmill = _logManager.GetSawmill("lagmonitor");
        _cfg.OnValueChanged(CCVars.LagMonitorEnabled, v => _enabled = v, true);
        _cfg.OnValueChanged(CCVars.LagMonitorHitchMs, v => _hitchMs = v, true);
        _cfg.OnValueChanged(CCVars.LagMonitorAlertMs, v => _alertMs = v, true);
        _cfg.OnValueChanged(CCVars.LagMonitorAlertAdmins, v => _alertAdmins = v, true);
        _cfg.OnValueChanged(CCVars.LagMonitorAlertCooldown, v => _alertCooldown = v, true);
        _watch.Start();
    }

    public void TickStarted()
    {
        if (!_enabled)
        {
            _lastTick = TimeSpan.Zero;
            return;
        }

        // what the monitor itself costs
        var start = Stopwatch.GetTimestamp();
        Measure();
        _selfTime += Stopwatch.GetTimestamp() - start;
    }

    private void Measure()
    {
        // per system timings need this on
        if (!_sysMan.MetricsEnabled)
            _sysMan.MetricsEnabled = true;

        if (_systems.Count == 0)
            RefreshChildren();

        ReadTick(_systems);
        ReadTick(_areas);
        ReadTick(_pvs);

        var gcPause = GC.GetTotalPauseDuration();
        var gcDelta = gcPause - _lastGcPause;
        _lastGcPause = gcPause;

        var now = _watch.Elapsed;
        if (_lastTick != TimeSpan.Zero)
        {
            var gap = (now - _lastTick).TotalMilliseconds;
            // empty server pauses itself, that gap is not a freeze
            if (gap >= _hitchMs && _hadPlayers)
                RecordHitch((float) gap, (float) gcDelta.TotalMilliseconds);
        }

        _lastTick = now;
        _hadPlayers = _players.PlayerCount > 0;

        if (now >= _nextSample)
        {
            Sample(now);
            _nextSample = now + SampleInterval;
        }
    }

    private static void ReadTick(List<Tracked> list)
    {
        foreach (var t in list)
        {
            var sum = t.Child.Sum;
            t.Delta = sum - t.Last;
            t.Last = sum;
        }
    }

    private void RefreshChildren()
    {
        Refresh(SystemsHist, _systems);
        Refresh(TickHist, _areas);
        Refresh(PvsHist, _pvs);
    }

    private void Refresh(Histogram hist, List<Tracked> list)
    {
        _known.Clear();
        foreach (var t in list)
        {
            _known.Add(t.Name);
        }

        foreach (var labels in hist.GetAllLabelValues())
        {
            if (labels.Length == 0)
                continue;

            var name = labels[0];
            if (!_known.Add(name))
                continue;

            var child = hist.WithLabels(labels);
            var sum = child.Sum;
            list.Add(new Tracked { Name = name, Child = child, Last = sum, SampleBase = sum });
        }
    }

    private bool IsRoundLoading()
    {
        if (!_sysMan.TryGetEntitySystem<GameTicker>(out var ticker))
            return true;

        return ticker.RunLevel != GameRunLevel.InRound || _timing.CurTime - ticker.RoundStartTimeSpan < TimeSpan.FromSeconds(30);
    }

    private void RecordHitch(float ms, float gcMs)
    {
        var hitch = new LagHitch
        {
            Time = DateTime.Now,
            Ms = ms,
            GcMs = gcMs,
            RoundLoading = IsRoundLoading(),
        };

        var entries = new List<LagEntry>();
        foreach (var t in _systems)
        {
            if (t.Delta > 0.002)
                entries.Add(new LagEntry(t.Name, (float) (t.Delta * 1000)));
        }

        foreach (var t in _areas)
        {
            // these two are just the sums of the systems and the pvs parts
            if (t.Name is "EntitySystems" or "GameState")
                continue;

            if (t.Delta > 0.002)
                entries.Add(new LagEntry(t.Name, (float) (t.Delta * 1000)));
        }

        foreach (var t in _pvs)
        {
            if (t.Delta > 0.002)
                entries.Add(new LagEntry("PVS " + t.Name, (float) (t.Delta * 1000)));
        }

        if (gcMs >= 2f)
            entries.Add(new LagEntry(Loc.GetString("lag-monitor-gc"), gcMs));

        hitch.Top = entries.OrderByDescending(e => e.Ms).Take(5).ToList();

        _hitches.Add(hitch);
        if (_hitches.Count > MaxHitches)
            _hitches.RemoveAt(0);

        if (ms < _alertMs || hitch.RoundLoading)
            return;

        var cause = DescribeTop(hitch.Top, 3);
        _sawmill.Warning($"Server froze for {ms:0} ms: {cause}");
        Alert(Loc.GetString("lag-monitor-alert-hitch", ("ms", (int) ms), ("cause", cause)));
    }

    private static string DescribeTop(List<LagEntry> top, int count)
    {
        if (top.Count == 0)
            return Loc.GetString("lag-monitor-unknown-cause");

        return string.Join(", ", top.Take(count).Select(e => $"{e.Name} {e.Ms:0} ms"));
    }

    private void Alert(string message)
    {
        if (!_alertAdmins || _watch.Elapsed < _nextAlert)
            return;

        _nextAlert = _watch.Elapsed + TimeSpan.FromSeconds(_alertCooldown);
        _chat.SendAdminAlert(message);
    }

    private void Sample(TimeSpan now)
    {
        RefreshChildren();

        var tick = _timing.CurTick.Value;
        var first = _lastSampleTime == TimeSpan.Zero;
        var elapsed = (float) (now - _lastSampleTime).TotalSeconds;
        var ticks = Math.Max(1, (int) (tick - _lastSampleTick));
        var selfMs = (float) (_selfTime * 1000.0 / Stopwatch.Frequency / ticks);
        _selfTime = 0;

        foreach (var list in new[] { _systems, _areas, _pvs })
        {
            foreach (var t in list)
            {
                t.WindowDelta = t.Last - t.SampleBase;
                t.SampleBase = t.Last;
            }
        }

        var sent = SentBytes.Value;
        var recv = RecvBytes.Value;
        var gcPause = GC.GetTotalPauseDuration();
        var gen2 = GC.CollectionCount(2);

        var sample = new LagSample
        {
            Time = DateTime.Now,
            Seconds = elapsed,
            TargetTickRate = _timing.TickRate,
            TickRate = ticks / elapsed,
            NetOutKb = (float) ((sent - _lastSent) / 1024 / elapsed),
            NetInKb = (float) ((recv - _lastRecv) / 1024 / elapsed),
            GcPauseMs = (float) (gcPause - _sampleGcPause).TotalMilliseconds,
            Gen2 = gen2 - _lastGen2,
        };

        _lastSampleTime = now;
        _lastSampleTick = tick;
        _lastSent = sent;
        _lastRecv = recv;
        _sampleGcPause = gcPause;
        _lastGen2 = gen2;

        // the first one covers everything since startup
        if (first)
            return;

        // main loop areas don't overlap so the sum is the busy time
        var busy = 0.0;
        foreach (var t in _areas)
        {
            busy += t.WindowDelta;
        }

        sample.Load = (float) (busy / elapsed);
        sample.TickMs = (float) (busy * 1000 / ticks);

        sample.Systems = _systems
            .Where(t => t.WindowDelta > 0)
            .OrderByDescending(t => t.WindowDelta)
            .Take(TopSystems)
            .Select(t => new LagEntry(t.Name, (float) (t.WindowDelta * 1000 / ticks)))
            .ToList();

        foreach (var t in _areas.OrderByDescending(t => t.WindowDelta))
        {
            sample.Parts.Add(new LagEntry(t.Name, (float) (t.WindowDelta * 1000 / ticks)));
        }

        foreach (var t in _pvs.OrderByDescending(t => t.WindowDelta))
        {
            sample.Parts.Add(new LagEntry("PVS " + t.Name, (float) (t.WindowDelta * 1000 / ticks)));
        }

        sample.MonitorMs = selfMs;
        CountThings(sample);

        var windowStart = sample.Time - TimeSpan.FromSeconds(elapsed);
        foreach (var hitch in _hitches)
        {
            if (hitch.Time < windowStart || hitch.RoundLoading)
                continue;

            sample.Hitches++;
            sample.WorstHitchMs = Math.Max(sample.WorstHitchMs, hitch.Ms);
        }

        _samples.Add(sample);
        if (_samples.Count > HistoryLength)
            _samples.RemoveAt(0);

        var overloaded = sample.Load >= 0.95f || sample.TickRate < sample.TargetTickRate * 0.9f;
        if (overloaded && _wasOverloaded && !IsRoundLoading())
        {
            var cause = DescribeTop(sample.Systems, 3);
            _sawmill.Warning($"Server can't keep up: load {sample.Load * 100:0}%, {sample.TickRate:0.0}/{sample.TargetTickRate} tps. Heaviest: {cause}");
            Alert(Loc.GetString("lag-monitor-alert-cpu",
                ("load", (int) (sample.Load * 100)),
                ("tps", sample.TickRate.ToString("0.0")),
                ("target", sample.TargetTickRate),
                ("cause", cause)));
        }

        _wasOverloaded = overloaded;

        BuildFindings(sample);
        PushState();
    }

    private void CountThings(LagSample sample)
    {
        sample.Players = _players.PlayerCount;
        sample.Entities = _ents.EntityCount;
        sample.Grids = _ents.Count<MapGridComponent>();
        sample.Npcs = _ents.Count<HTNComponent>();
        sample.ActiveNpcs = _ents.Count<ActiveNPCComponent>();
        sample.Drones = _ents.Count<TerritoryPicketDroneComponent>();

        if (_sysMan.TryGetEntitySystem<SharedPhysicsSystem>(out var physics))
            sample.AwakeBodies = physics.AwakeBodies.Count;
    }

    private List<LagPing> GetPings()
    {
        return _players.Sessions
            .OrderByDescending(s => s.Channel.Ping)
            .Take(5)
            .Select(s => new LagPing(s.Name, s.Channel.Ping))
            .ToList();
    }

    private void BuildFindings(LagSample sample)
    {
        _findings.Clear();

        if (sample.Load >= 0.9f || sample.TickRate < sample.TargetTickRate * 0.9f)
        {
            _findings.Add(Loc.GetString("lag-monitor-finding-cpu",
                ("load", (int) (sample.Load * 100)),
                ("tps", sample.TickRate.ToString("0.0")),
                ("target", sample.TargetTickRate),
                ("cause", DescribeTop(sample.Systems, 3))));
        }
        else if (sample.Load >= 0.6f)
        {
            _findings.Add(Loc.GetString("lag-monitor-finding-busy",
                ("load", (int) (sample.Load * 100)),
                ("cause", DescribeTop(sample.Systems, 3))));
        }

        var recent = _hitches
            .Where(h => !h.RoundLoading && h.Time >= DateTime.Now - TimeSpan.FromMinutes(5))
            .ToList();

        if (recent.Count > 0)
        {
            var worst = recent.MaxBy(h => h.Ms)!;
            _findings.Add(Loc.GetString("lag-monitor-finding-hitch",
                ("count", recent.Count),
                ("ms", (int) worst.Ms),
                ("cause", DescribeTop(worst.Top, 2))));
        }

        if (sample.GcPauseMs >= 100f)
            _findings.Add(Loc.GetString("lag-monitor-finding-gc", ("ms", (int) sample.GcPauseMs)));

        if (sample.Players > 0 && sample.NetOutKb / sample.Players >= NetPerPlayerWarnKb)
        {
            _findings.Add(Loc.GetString("lag-monitor-finding-net",
                ("kb", (int) (sample.NetOutKb / sample.Players))));
        }

        var highPing = _players.Sessions.Count(s => s.Channel.Ping >= PingWarn);
        if (highPing > 0)
            _findings.Add(Loc.GetString("lag-monitor-finding-ping", ("count", highPing), ("ping", PingWarn)));

        if (_findings.Count == 0)
            _findings.Add(Loc.GetString("lag-monitor-finding-ok"));
    }

    public LagMonitorEuiState BuildState()
    {
        var since = DateTime.Now - TimeSpan.FromMinutes(10);
        return new LagMonitorEuiState
        {
            Enabled = _enabled,
            Current = _samples.Count > 0 ? _samples[^1] : null,
            Peak = _samples.Where(s => s.Time >= since).MaxBy(s => s.Load),
            LoadHistory = _samples.Select(s => s.Load).ToArray(),
            Hitches = _hitches.AsEnumerable().Reverse().ToList(),
            Findings = _findings.ToList(),
            Pings = GetPings(),
        };
    }

    public void ClearHitches()
    {
        _hitches.Clear();
        PushState();
    }

    public void AddEui(LagMonitorEui eui)
    {
        _open.Add(eui);
    }

    public void RemoveEui(LagMonitorEui eui)
    {
        _open.Remove(eui);
    }

    private void PushState()
    {
        foreach (var eui in _open.ToArray())
        {
            if (!_admins.HasAdminFlag(eui.Player, AdminFlags.Admin))
            {
                eui.Close();
                continue;
            }

            eui.StateDirty();
        }
    }

    public string BuildReport()
    {
        var sb = new StringBuilder();
        if (!_enabled)
            sb.AppendLine("lagmonitor.enabled is false, nothing is being measured.");

        if (_samples.Count == 0)
        {
            sb.AppendLine("No samples yet, wait a few seconds.");
            return sb.ToString();
        }

        var s = _samples[^1];
        sb.AppendLine($"Load {s.Load * 100:0}% | {s.TickRate:0.0}/{s.TargetTickRate} tps | {s.TickMs:0.0} ms per tick | players {s.Players} | entities {s.Entities}");
        sb.AppendLine($"NPCs {s.ActiveNpcs}/{s.Npcs} active | grids {s.Grids} | drones {s.Drones} | awake bodies {s.AwakeBodies}");
        sb.AppendLine($"Net out {s.NetOutKb:0} KB/s, in {s.NetInKb:0} KB/s | GC pause {s.GcPauseMs:0} ms ({s.Gen2} gen2) | this monitor {s.MonitorMs:0.000} ms per tick");
        sb.AppendLine();

        foreach (var finding in _findings)
        {
            sb.AppendLine("* " + finding);
        }

        sb.AppendLine();
        sb.AppendLine("Heaviest systems (ms per tick, last 5 s):");
        foreach (var e in s.Systems.Take(10))
        {
            sb.AppendLine($"  {e.Ms,7:0.00}  {e.Name}");
        }

        if (_hitches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Last freezes:");
            foreach (var h in _hitches.AsEnumerable().Reverse().Take(5))
            {
                var loading = h.RoundLoading ? " (round start)" : string.Empty;
                sb.AppendLine($"  {h.Time:HH:mm:ss} {h.Ms:0} ms{loading}: {DescribeTop(h.Top, 3)}");
            }
        }

        return sb.ToString();
    }
}
