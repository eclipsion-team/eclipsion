using Content.Server.EUI;
using Content.Shared._Crescent.Religion;
using Content.Shared.Eui;

namespace Content.Server._Crescent.Religion;

/// <summary>
///     The Prophet's view of the Host of the Mother.
/// </summary>
public sealed class VeiledHostEui : BaseEui
{
    private readonly VeiledProphetSystem _system;

    internal EntityUid Prophet { get; }

    public VeiledHostEui(EntityUid prophet, VeiledProphetSystem system)
    {
        Prophet = prophet;
        _system = system;
    }

    public override void Opened()
    {
        StateDirty();
    }

    public override void Closed()
    {
        base.Closed();
        _system.OnEuiClosed(this);
    }

    public override EuiStateBase GetNewState()
    {
        return _system.BuildHostState(Prophet);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (IsShutDown)
            return;

        // The window belongs to the body that opened it, not to whoever the player becomes later.
        if (Player.AttachedEntity != Prophet)
        {
            Close();
            return;
        }

        switch (msg)
        {
            case VeiledHostChastiseMessage chastise:
                _system.TryChastise(Prophet, chastise.Member);
                StateDirty();
                break;
            case VeiledHostRefreshMessage:
                StateDirty();
                break;
        }
    }
}
