using Content.Server.DeviceLinking.Events;
using Content.Server.DeviceLinking.Systems;
using Content.Server.Materials;
using Content.Server.Power.Components;
using Content.Shared.Conveyor;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Physics.Controllers;
using Content.Shared.Power;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server.Physics.Controllers;

public sealed class ConveyorController : SharedConveyorController
{
    [Dependency] private readonly FixtureSystem _fixtures = default!;
    [Dependency] private readonly DeviceLinkSystem _signalSystem = default!;
    [Dependency] private readonly MaterialReclaimerSystem _materialReclaimer = default!;
    [Dependency] private readonly SharedBroadphaseSystem _broadphase = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // Freshly spawned items sleep with collision off (CollisionWake), so belts never touch them.
    private static readonly TimeSpan WakeScanInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _nextWakeScan;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextWakeScan)
            return;

        _nextWakeScan = _timing.CurTime + WakeScanInterval;

        var query = EntityQueryEnumerator<ConveyorComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (CanRun(comp))
                AwakenEntities(uid, comp, sleepingOnly: true);
        }
    }

    public override void Initialize()
    {
        UpdatesAfter.Add(typeof(MoverController));
        SubscribeLocalEvent<ConveyorComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<ConveyorComponent, ComponentShutdown>(OnConveyorShutdown);


        SubscribeLocalEvent<ConveyorComponent, SignalReceivedEvent>(OnSignalReceived);
        SubscribeLocalEvent<ConveyorComponent, PowerChangedEvent>(OnPowerChanged);

        base.Initialize();
    }

    private void OnInit(EntityUid uid, ConveyorComponent component, ComponentInit args)
    {
        _signalSystem.EnsureSinkPorts(uid, component.ReversePort, component.ForwardPort, component.OffPort);

        if (TryComp<PhysicsComponent>(uid, out var physics))
        {
            var shape = new PolygonShape();
            shape.SetAsBox(0.55f, 0.55f);

            _fixtures.TryCreateFixture(uid, shape, ConveyorFixture,
                collisionLayer: (int) (CollisionGroup.LowImpassable | CollisionGroup.MidImpassable |
                                       CollisionGroup.Impassable), hard: false, body: physics);

        }
    }

    private void OnConveyorShutdown(EntityUid uid, ConveyorComponent component, ComponentShutdown args)
    {
        if (MetaData(uid).EntityLifeStage >= EntityLifeStage.Terminating)
            return;

        RemComp<ActiveConveyorComponent>(uid);

        if (!TryComp<PhysicsComponent>(uid, out var physics))
            return;

        _fixtures.DestroyFixture(uid, ConveyorFixture, body: physics);
    }

    private void OnPowerChanged(EntityUid uid, ConveyorComponent component, ref PowerChangedEvent args)
    {
        component.Powered = args.Powered;
        if (args.Powered)
            AwakenEntities(uid, component);
        UpdateAppearance(uid, component);
        Dirty(uid, component);
    }

    private void UpdateAppearance(EntityUid uid, ConveyorComponent component)
    {
        _appearance.SetData(uid, ConveyorVisuals.State, component.Powered ? component.State : ConveyorState.Off);
    }

    private void OnSignalReceived(EntityUid uid, ConveyorComponent component, ref SignalReceivedEvent args)
    {
        if (args.Port == component.OffPort)
            SetState(uid, ConveyorState.Off, component);

        else if (args.Port == component.ForwardPort)
        {
            AwakenEntities(uid, component);
            SetState(uid, ConveyorState.Forward, component);
        }

        else if (args.Port == component.ReversePort)
        {
            AwakenEntities(uid, component);
            SetState(uid, ConveyorState.Reverse, component);
        }
    }

    private void SetState(EntityUid uid, ConveyorState state, ConveyorComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        component.State = state;

        if (TryComp<PhysicsComponent>(uid, out var physics))
            _broadphase.RegenerateContacts(uid, physics);

        _materialReclaimer.SetReclaimerEnabled(uid, component.State != ConveyorState.Off);

        UpdateAppearance(uid, component);
        Dirty(uid, component);
    }

    /// <summary>
    /// Awakens sleeping entities on the conveyor belt's tile when it's turned on.
    /// Fixes an issue where non-hard/sleeping entities refuse to wake up + collide if a belt is turned off and on again.
    /// </summary>
    private void AwakenEntities(EntityUid uid, ConveyorComponent component, bool sleepingOnly = false)
    {
        var xformQuery = GetEntityQuery<TransformComponent>();
        var bodyQuery = GetEntityQuery<PhysicsComponent>();
        var wakeQuery = GetEntityQuery<CollisionWakeComponent>();

        if (!xformQuery.TryGetComponent(uid, out var xform) || xform.GridUid is not { } grid)
            return;

        var beltTileRef = xform.Coordinates.GetTileRef(EntityManager, MapManager);

        if (beltTileRef != null)
        {
            var intersecting = Lookup.GetLocalEntitiesIntersecting(beltTileRef.Value, 0f,
                LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Approximate);

            foreach (var entity in intersecting)
            {
                if (!bodyQuery.TryGetComponent(entity, out var physics) || physics.BodyType == BodyType.Static)
                    continue;

                // Only CollisionWake turns collision off for sleeping; anything else is non-colliding on purpose.
                if (!physics.CanCollide && !wakeQuery.HasComp(entity))
                    continue;

                if (sleepingOnly)
                {
                    if (physics.Awake && physics.CanCollide || component.Intersecting.Contains(entity))
                        continue;

                    if (!xformQuery.TryGetComponent(entity, out var entXform) || entXform.ParentUid != grid)
                        continue;
                }

                Physics.WakeBody(entity, body: physics);
            }
        }
    }
}
