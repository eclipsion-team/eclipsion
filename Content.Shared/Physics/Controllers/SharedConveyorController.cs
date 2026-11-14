using System.Numerics;
using Content.Shared.Conveyor;
using Content.Shared.Gravity;
using Content.Shared.Item;
using Content.Shared.Movement.Systems;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared.Physics.Controllers;

public abstract class SharedConveyorController : VirtualController
{
    [Dependency] protected readonly SharedMapSystem MapManager = default!;
    [Dependency] protected readonly EntityLookupSystem Lookup = default!;
    [Dependency] protected readonly SharedPhysicsSystem Physics = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] private readonly INetManager _net = default!;

    protected const string ConveyorFixture = "conveyor";

    private static readonly Vector2 _expansion = new Vector2(0.1f, 0.1f);

    // a belt moves things 2 tiles/s, so anything that went less than this in JamCheckTime is stuck
    private const float JamDistance = 0.1f;
    private static readonly TimeSpan JamCheckTime = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan JamRestTime = TimeSpan.FromSeconds(1.5);

    private readonly record struct JamCheck(Vector2 Checkpoint, TimeSpan CheckAt, TimeSpan RestUntil, TimeSpan LastSeen);

    private readonly HashSet<EntityUid> _conveyed = new();
    private readonly Dictionary<EntityUid, JamCheck> _jams = new();
    private readonly List<EntityUid> _jamsRemove = new();
    private TimeSpan _nextJamCleanup;
    private EntityQuery<ItemComponent> _itemQuery;

    public override void Initialize()
    {
        _itemQuery = GetEntityQuery<ItemComponent>();

        UpdatesAfter.Add(typeof(SharedMoverController));

        SubscribeLocalEvent<ConveyorComponent, StartCollideEvent>(OnConveyorStartCollide);
        SubscribeLocalEvent<ConveyorComponent, EndCollideEvent>(OnConveyorEndCollide);

        base.Initialize();
    }

    private void OnConveyorStartCollide(EntityUid uid, ConveyorComponent component, ref StartCollideEvent args)
    {
        var otherUid = args.OtherEntity;

        if (args.OtherBody.BodyType == BodyType.Static || component.State == ConveyorState.Off)
            return;

        component.Intersecting.Add(otherUid);
        EnsureComp<ActiveConveyorComponent>(uid);
    }

    private void OnConveyorEndCollide(EntityUid uid, ConveyorComponent component, ref EndCollideEvent args)
    {
        component.Intersecting.Remove(args.OtherEntity);

        if (component.Intersecting.Count == 0)
            RemComp<ActiveConveyorComponent>(uid);
    }

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        _conveyed.Clear();
        var xformQuery = GetEntityQuery<TransformComponent>();
        var bodyQuery = GetEntityQuery<PhysicsComponent>();
        var query = EntityQueryEnumerator<ActiveConveyorComponent, ConveyorComponent>();

        while (query.MoveNext(out var uid, out var _, out var comp))
        {
            Convey(uid, comp, xformQuery, bodyQuery, frameTime, prediction);
        }

        if (!prediction && _net.IsServer && Timing.CurTime >= _nextJamCleanup)
        {
            _nextJamCleanup = Timing.CurTime + TimeSpan.FromSeconds(5);
            CleanupJams();
        }
    }

    private void Convey(EntityUid uid, ConveyorComponent comp, EntityQuery<TransformComponent> xformQuery, EntityQuery<PhysicsComponent> bodyQuery, float frameTime, bool prediction)
    {
        if (!CanRun(comp))
            return;

        var speed = comp.Speed;

        if (speed <= 0f || !xformQuery.TryGetComponent(uid, out var xform) || xform.GridUid is not { } gridUid)
            return;

        var conveyorPos = xform.LocalPosition;
        var conveyorRot = xform.LocalRotation;

        conveyorRot += comp.Angle;

        if (comp.State == ConveyorState.Reverse)
            conveyorRot += MathF.PI;

        var direction = conveyorRot.ToWorldVec();

        // the belt's tile, belts are always anchored in the middle of one
        var tile = new Vector2((float) Math.Floor(conveyorPos.X), (float) Math.Floor(conveyorPos.Y));
        var conveyorBounds = new Box2(tile, tile + Vector2.One);
        var checkJams = !prediction && _net.IsServer;

        foreach (var entity in comp.Intersecting)
        {
            if (!xformQuery.TryGetComponent(entity, out var transform) || transform.ParentUid != gridUid)
                continue;

            if (!bodyQuery.TryGetComponent(entity, out var body) || body.BodyType == BodyType.Static || body.BodyStatus == BodyStatus.InAir)
                continue;

            if (prediction && !body.Predict)
                continue;

            var localPos = transform.LocalPosition;

            // Check if the thing's centre overlaps the belt tile.
            var itemBox = new Box2(localPos - _expansion, localPos + _expansion);
            if (!conveyorBounds.Intersects(itemBox))
                continue;

            if (!_conveyed.Add(entity))
                continue;

            if (checkJams && IsJammed(entity, localPos))
                continue;

            if (_gravity.IsWeightless(entity, body, transform))
                continue;

            var itemRelative = conveyorPos - localPos;

            localPos += Convey(direction, speed, frameTime, itemRelative);
            transform.LocalPosition = localPos;

            // Force it awake for collisionwake reasons.
            Physics.SetAwake((entity, body), true);
            Physics.SetSleepTime(body, 0f);
        }

        // no Dirty here, nothing on the belt changed
    }

    // stuck against a wall or a pile, leave it alone for a bit
    private bool IsJammed(EntityUid entity, Vector2 localPos)
    {
        if (!_itemQuery.HasComp(entity))
            return false;

        var now = Timing.CurTime;

        // new, or it was off the belt for a while (asleep after resting): give it a fresh try
        if (!_jams.TryGetValue(entity, out var jam) || jam.RestUntil < now && now - jam.LastSeen > JamCheckTime)
        {
            _jams[entity] = new JamCheck(localPos, now + JamCheckTime, TimeSpan.Zero, now);
            return false;
        }

        if (now < jam.RestUntil)
        {
            _jams[entity] = jam with { LastSeen = now };
            return true;
        }

        if (now < jam.CheckAt)
        {
            _jams[entity] = jam with { LastSeen = now };
            return false;
        }

        if ((localPos - jam.Checkpoint).LengthSquared() < JamDistance * JamDistance)
        {
            var rest = now + JamRestTime;
            _jams[entity] = new JamCheck(localPos, rest + JamCheckTime, rest, now);
            return true;
        }

        _jams[entity] = new JamCheck(localPos, now + JamCheckTime, TimeSpan.Zero, now);
        return false;
    }

    public bool IsResting(EntityUid entity)
    {
        return _jams.TryGetValue(entity, out var jam) && Timing.CurTime < jam.RestUntil;
    }

    private void CleanupJams()
    {
        var old = Timing.CurTime - TimeSpan.FromSeconds(5);
        foreach (var (entity, jam) in _jams)
        {
            if (jam.LastSeen < old && jam.RestUntil < old)
                _jamsRemove.Add(entity);
        }

        foreach (var entity in _jamsRemove)
        {
            _jams.Remove(entity);
        }

        _jamsRemove.Clear();
    }

    private static Vector2 Convey(Vector2 direction, float speed, float frameTime, Vector2 itemRelative)
    {
        if (speed == 0 || direction.Length() == 0)
            return Vector2.Zero;

        /*
         * Basic idea: if the item is not in the middle of the conveyor in the direction that the conveyor is running,
         * move the item towards the middle. Otherwise, move the item along the direction. This lets conveyors pick up
         * items that are not perfectly aligned in the middle, and also makes corner cuts work.
         *
         * We do this by computing the projection of 'itemRelative' on 'direction', yielding a vector 'p' in the direction
         * of 'direction'. We also compute the rejection 'r'. If the magnitude of 'r' is not (near) zero, then the item
         * is not on the centerline.
         */

        var p = direction * (Vector2.Dot(itemRelative, direction) / Vector2.Dot(direction, direction));
        var r = itemRelative - p;

        if (r.Length() < 0.1)
        {
            var velocity = direction * speed;
            return velocity * frameTime;
        }
        else
        {
            // Give a slight nudge in the direction of the conveyor to prevent
            // to collidable objects (e.g. crates) on the locker from getting stuck
            // pushing each other when rounding a corner.
            var velocity = (r + direction*0.2f).Normalized() * speed;
            return velocity * frameTime;
        }
    }

    public bool CanRun(ConveyorComponent component)
    {
        return component.State != ConveyorState.Off && component.Powered;
    }
}
