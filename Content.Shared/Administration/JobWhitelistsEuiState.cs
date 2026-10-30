using Content.Shared.Eui;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

[Serializable, NetSerializable]
public sealed class JobWhitelistsEuiState : EuiStateBase
{
    public string PlayerName;
    public HashSet<ProtoId<JobPrototype>> Whitelists;

    public bool? ServerWhitelisted;

    public JobWhitelistsEuiState(string playerName, HashSet<ProtoId<JobPrototype>> whitelists, bool? serverWhitelisted)
    {
        PlayerName = playerName;
        Whitelists = whitelists;
        ServerWhitelisted = serverWhitelisted;
    }
}

/// <summary>
/// </summary>
[Serializable, NetSerializable]
public sealed class SetJobWhitelistsMessage : EuiMessageBase
{
    public List<ProtoId<JobPrototype>> Jobs;
    public bool Whitelisting;

    public SetJobWhitelistsMessage(List<ProtoId<JobPrototype>> jobs, bool whitelisting)
    {
        Jobs = jobs;
        Whitelisting = whitelisting;
    }
}

[Serializable, NetSerializable]
public sealed class SetServerWhitelistedMessage : EuiMessageBase
{
    public bool Whitelisted;

    public SetServerWhitelistedMessage(bool whitelisted)
    {
        Whitelisted = whitelisted;
    }
}
