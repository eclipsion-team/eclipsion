using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._Crescent.LagMonitor;
using Content.Shared.Administration;
using Content.Shared.Eui;

namespace Content.Server._Crescent.LagMonitor;

public sealed partial class LagMonitorEui : BaseEui
{
    [Dependency] private readonly LagMonitorManager _monitor = default!;
    [Dependency] private readonly IAdminManager _admins = default!;

    public LagMonitorEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        _monitor.AddEui(this);
        StateDirty();
    }

    public override void Closed()
    {
        base.Closed();
        _monitor.RemoveEui(this);
    }

    public override EuiStateBase GetNewState()
    {
        return _monitor.BuildState();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is LagMonitorEuiMsg.ClearHitches && _admins.HasAdminFlag(Player, AdminFlags.Admin))
            _monitor.ClearHitches();
    }
}
