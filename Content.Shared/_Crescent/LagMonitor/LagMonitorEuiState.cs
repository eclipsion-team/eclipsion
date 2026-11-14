using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.LagMonitor;

[Serializable, NetSerializable]
public sealed class LagMonitorEuiState : EuiStateBase
{
    public bool Enabled;
    public LagSample? Current;
    // highest load sample of the last 10 minutes
    public LagSample? Peak;
    public float[] LoadHistory = Array.Empty<float>();
    public List<LagHitch> Hitches = new();
    public List<string> Findings = new();
    public List<LagPing> Pings = new();
}

[Serializable, NetSerializable]
public sealed class LagSample
{
    public DateTime Time;
    public float Seconds;
    public float Load;
    public float TickRate;
    public float TargetTickRate;
    public float TickMs;
    public float NetOutKb;
    public float NetInKb;
    public float GcPauseMs;
    public int Gen2;
    public int Players;
    public int Entities;
    public int Grids;
    public int Npcs;
    public int ActiveNpcs;
    public int AwakeBodies;
    public int Drones;
    public int Hitches;
    public float WorstHitchMs;
    // what the lag monitor itself took, ms per tick
    public float MonitorMs;
    // ms per tick
    public List<LagEntry> Systems = new();
    public List<LagEntry> Parts = new();
}

[Serializable, NetSerializable]
public sealed class LagHitch
{
    public DateTime Time;
    public float Ms;
    public float GcMs;
    public bool RoundLoading;
    // total ms inside that one freeze
    public List<LagEntry> Top = new();
}

[Serializable, NetSerializable]
public record struct LagEntry(string Name, float Ms);

[Serializable, NetSerializable]
public record struct LagPing(string Name, int Ping);

public static class LagMonitorEuiMsg
{
    [Serializable, NetSerializable]
    public sealed class ClearHitches : EuiMessageBase
    {
    }
}
