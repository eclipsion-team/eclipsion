using Content.Shared.Eui;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

[Serializable, NetSerializable]
public sealed class PlaytimeEuiState : EuiStateBase
{
    public string? PlayerName;

    public bool Online;

    public Dictionary<string, TimeSpan>? Trackers;

    public string? Status;

    public PlaytimeEuiState(string? playerName, bool online, Dictionary<string, TimeSpan>? trackers, string? status)
    {
        PlayerName = playerName;
        Online = online;
        Trackers = trackers;
        Status = status;
    }
}

[Serializable, NetSerializable]
public sealed class SelectPlaytimePlayerMessage : EuiMessageBase
{
    public string Player;

    public SelectPlaytimePlayerMessage(string player)
    {
        Player = player;
    }
}

[Serializable, NetSerializable]
public sealed class ChangePlaytimeMessage : EuiMessageBase
{
    public List<ProtoId<PlayTimeTrackerPrototype>> Trackers;
    public TimeSpan Time;
    public bool Overwrite;

    public ChangePlaytimeMessage(List<ProtoId<PlayTimeTrackerPrototype>> trackers, TimeSpan time, bool overwrite)
    {
        Trackers = trackers;
        Time = time;
        Overwrite = overwrite;
    }
}

[Serializable, NetSerializable]
public sealed class MeetPlaytimeRequirementsMessage : EuiMessageBase
{
    public List<ProtoId<JobPrototype>> Jobs;

    public MeetPlaytimeRequirementsMessage(List<ProtoId<JobPrototype>> jobs)
    {
        Jobs = jobs;
    }
}
