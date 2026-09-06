using Content.Shared._Crescent.RepairStation;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.RepairStation;

[UsedImplicitly]
public sealed class ShipRepairStationBoundUserInterface : BoundUserInterface
{
    private ShipRepairStationWindow? _window;

    public ShipRepairStationBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey) { }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<ShipRepairStationWindow>();
        _window.OnShipSelected += grid => SendMessage(new ShipRepairSelectMessage(grid));
        _window.OnStartPressed += () => SendMessage(new ShipRepairStartMessage());
        _window.OnCancelPressed += () => SendMessage(new ShipRepairCancelMessage());
        _window.OnSnapshotPressed += () => SendMessage(new ShipRepairSnapshotMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is ShipRepairStationUiState cast)
            _window?.UpdateState(cast);
    }
}
