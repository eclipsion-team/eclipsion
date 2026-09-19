using System.Numerics;
using Content.Server.UserInterface;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.PowerCell;
using Content.Shared.Movement.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server.Shuttles.Systems;

public sealed partial class RadarConsoleSystem : SharedRadarConsoleSystem
{
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RadarConsoleComponent, ComponentStartup>(OnRadarStartup);
        SubscribeLocalEvent<RadarConsoleComponent, BoundUIOpenedEvent>(OnUIOpened);
        InitializeCrescent(); // Crescent radar update rate.
    }

    private void OnRadarStartup(EntityUid uid, RadarConsoleComponent component, ComponentStartup args)
    {
        UpdateState(uid, component);
    }

    private void OnUIOpened(EntityUid uid, RadarConsoleComponent component, ref BoundUIOpenedEvent args)
    {
        UpdateState(uid, component);
    }

    protected override void UpdateState(EntityUid uid, RadarConsoleComponent component)
    {
        var xform = Transform(uid);
        var onGrid = xform.ParentUid == xform.GridUid;
        EntityCoordinates? coordinates = onGrid ? xform.Coordinates : null;
        // Eclipsion: the radar is always drawn grid-up, however the console itself is turned.
        Angle? angle = onGrid ? Angle.Zero : null;

        if (component.FollowEntity)
        {
            coordinates = new EntityCoordinates(uid, Vector2.Zero);
            angle = Angle.Zero;
        }

        if (_uiSystem.HasUi(uid, RadarConsoleUiKey.Key))
        {
            NavInterfaceState state;
            var docks = _console.GetAllDocks();

            if (coordinates != null && angle != null)
                state = _console.GetNavState(uid, docks, coordinates.Value, angle.Value);
            else
                state = _console.GetNavState(uid, docks);

            component.LastUpdatedState = new NavBoundUserInterfaceState(state);
            component.LastUpdatedState.IFFState = _console.GetIFFState(uid, null);
            _uiSystem.SetUiState(uid, RadarConsoleUiKey.Key, component.LastUpdatedState);
        }
    }

    public void SetTarget(Entity<RadarConsoleComponent> ent, NetEntity targetEntity, Vector2 target)
    {
        if (EntityManager.TryGetEntity(targetEntity, out var targetUid)
            && HasComp<ShuttleComponent>(targetUid)
            && (!TryComp(targetUid, out IFFComponent? iff) || (iff.Flags & (IFFFlags.Hide | IFFFlags.HideLabel)) == 0)
            && TryComp(targetUid, out TransformComponent? xform))
        {
            ent.Comp.TargetEntity = targetUid.Value;
            ent.Comp.Target = _transform.GetMapCoordinates(xform).Position;
        }
        else
        {
            ent.Comp.Target = target;
            ent.Comp.TargetEntity = null;
        }
        Dirty(ent);
    }

    public void SetHideTarget(Entity<RadarConsoleComponent> ent, bool hideTarget)
    {
        ent.Comp.HideTarget = hideTarget;
        Dirty(ent);
    }
}
