using System;
using Content.Shared._Crescent.IntruderTeleporter;
using Robust.Client.UserInterface;
using Robust.Shared.Map;

namespace Content.Client._Crescent.IntruderTeleporter;

public sealed class IntruderTeleporterBoundUserInterface : BoundUserInterface
{
    private IntruderTeleporterWindow? _window;

    public IntruderTeleporterBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<IntruderTeleporterWindow>();
        _window.SetConsole(Owner);

        _window.OnJoinPressed += () => SendMessage(new IntruderTeleporterToggleJoinMessage());
        _window.OnLaunchPressed += () => SendMessage(new IntruderTeleporterLaunchMessage());
        _window.OnAbortPressed += () => SendMessage(new IntruderTeleporterAbortMessage());
        _window.OnTargetPicked += OnTargetPicked;
    }

    private void OnTargetPicked(EntityCoordinates coordinates)
    {
        SendMessage(new IntruderTeleporterSelectTargetMessage(EntMan.GetNetCoordinates(coordinates)));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is IntruderTeleporterBuiState buiState)
            _window?.UpdateState(buiState);
    }
}
