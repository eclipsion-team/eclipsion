using System.Threading;
using Content.Shared.PointCannons;
using Timer = Robust.Shared.Timing.Timer;
using JetBrains.Annotations;
using System.Numerics;
using Content.Client._Crescent.PointCannons;
using Robust.Client.GameObjects;
using Content.Shared.Weapons.Ranged.Events;
using Content.Client.Weapons.Ranged.Systems;
using Robust.Client.Input;
using Robust.Shared.Input;

namespace Content.Client._Crescent.PointCannons;

[UsedImplicitly]
public sealed class TargetingConsoleBoundUserInterface : BoundUserInterface
{
    private IEntityManager _entMan;
    private TransformSystem _formSys;
    private IInputManager _inputMan;

    private TargetingConsoleWindow? _window;
    private bool _isFiring;
    private Vector2 _coords;
    private CancellationTokenSource _updTimerTok = new();
    private List<NetEntity>? _controlled;

    public TargetingConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _formSys = _entMan.System<TransformSystem>();
        _inputMan = IoCManager.Resolve<IInputManager>();
        Timer.SpawnRepeating(100, Update, _updTimerTok.Token);
    }

    /// <summary>
    /// Whether the fire button is physically held down right now.
    /// </summary>
    /// <remarks>
    /// Read off the keybind itself rather than InputSystem.CmdStates: a click that lands on a UI control is
    /// consumed by the UI and never reaches the simulation, so CmdStates reports UIClick as Up for the whole
    /// drag. The binding's own state is written in InputManager.SetBindState before dispatch, so it is Down
    /// either way - and ReleaseAllKeys drives it Up when the window loses focus.
    /// </remarks>
    private bool IsFireHeld()
    {
        // If UIClick somehow isn't bound, fall back to the radar's own release event.
        if (!_inputMan.TryGetKeyBinding(EngineKeyFunctions.UIClick, out var binding))
            return true;

        return binding.State == BoundKeyState.Down;
    }

    private void Update()
    {
        // The radar announces the release itself on mouse-up and on the cursor leaving it, and that is the
        // primary path. This is the backstop for the releases the radar never gets to see: the console closing
        // under a held button, and the window losing focus mid-drag - alt-tab drops the button without the UI
        // ever raising a KeyBindUp, which is how the guns ended up firing on their own with nobody at the
        // console. Both halves are needed; each on its own leaves one of the two holes open.
        if (_isFiring && (!IsOpened || !IsFireHeld()))
            StopFiring();

        if (_isFiring)
        {
            // Re-resolve the target every tick rather than firing at the map point the cursor was over when
            // it last moved. That point is fixed in the world while the ship is not, so a held crosshair
            // walks off the target and eventually ends up somewhere behind the hull - which is how the guns
            // came to swing round and shoot back through their own ship.
            if (_window != null && _window.Radar.TryGetHoveredCoordinates(out var hovered))
                _coords = _formSys.ToMapCoordinates(hovered).Position;

            SendMessage(new TargetingConsoleFireMessage(_coords));
        }

        if (_controlled == null || _window == null)
            return;

        // Walk _controlled, not the entity query, or the bars end up in a different order than the server's list.
        var ammoValues = new List<(int, int)>(_controlled.Count);
        foreach (var netEntity in _controlled)
        {
            // Still push a placeholder so the remaining bars don't shift.
            if (!_entMan.TryGetEntity(netEntity, out var uid) ||
                !_entMan.HasComponent<PointCannonComponent>(uid.Value))
            {
                ammoValues.Add((0, 1));
                continue;
            }

            GetAmmoCountEvent ammoEv = new();
            _entMan.EventBus.RaiseLocalEvent(uid.Value, ref ammoEv);
            ammoValues.Add((ammoEv.Count, ammoEv.Capacity));
        }

        _window.UpdateAmmoStatus(ammoValues);
    }

    protected override void Open()
    {
        base.Open();
        StopFiring();

        _window = new TargetingConsoleWindow();
        _window.OpenCentered();
        _window.OnClose += OnWindowClosed;

        _window.OnServerRefresh += OnRefreshServer;
        _window.OnTargetingModeChange += mode => SendMessage(new ShipWeaponTargetingModeMessage(mode));

        _window.Radar.OnRadarClick += (coords) =>
        {
            _coords = _formSys.ToMapCoordinates(coords).Position;
            SendMessage(new TargetingConsoleFireMessage(_coords));
            _isFiring = true;
        };

        _window.Radar.OnRadarRelease += () =>
        {
            StopFiring();
        };

        _window.Radar.OnRadarMouseMove += (coords) =>
        {
            _coords = _formSys.ToMapCoordinates(coords).Position;
        };

        _window.OnCannonGroupChange += (groupName) =>
        {
            SendMessage(new TargetingConsoleGroupChangedMessage(groupName));
        };
    }

    protected override void Dispose(bool disposing)
    {
        StopFiring();
        base.Dispose(disposing);

        if (disposing)
        {
            _updTimerTok.Cancel();
            _window?.Dispose();
        }
    }

    private void StopFiring()
    {
        if (!_isFiring)
            return;

        _isFiring = false;

        // Tells the server to drop the order now instead of waiting for it to lapse, so a tap does not carry on
        // shooting for the length of the expiry window. Not needed for correctness - the order times out on its
        // own - so it is fine that a console already on its way out cannot send it.
        if (IsOpened)
            SendMessage(new TargetingConsoleStopFireMessage());
    }

    private void OnWindowClosed()
    {
        StopFiring();
        Close();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not TargetingConsoleBoundUserInterfaceState consoleState)
            return;

        _controlled = consoleState.ControlledCannons;
        _window?.UpdateState(consoleState);
        if (_window != null) // Rat
            _window.Radar.ActiveCannons = _controlled; // Rat
    }

    private void OnRefreshServer()
    {
        SendMessage(new FireControlConsoleRefreshServerMessage());
    }
}
