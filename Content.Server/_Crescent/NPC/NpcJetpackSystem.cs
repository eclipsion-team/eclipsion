using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Server.Movement.Systems;
using Content.Server.NPC.Systems;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcJetpackComponent"/>
public sealed class NpcJetpackSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly JetpackSystem _jetpack = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<ActiveNPCComponent> _activeQuery;
    private EntityQuery<InputMoverComponent> _moverQuery;
    private EntityQuery<NpcSquadMemberComponent> _squadQuery;

    public override void Initialize()
    {
        base.Initialize();

        _activeQuery = GetEntityQuery<ActiveNPCComponent>();
        _moverQuery = GetEntityQuery<InputMoverComponent>();
        _squadQuery = GetEntityQuery<NpcSquadMemberComponent>();

        // Steering writes the NPC's own input, which a lit jetpack ignores; the jetpack's is written here
        // afterwards, and has to be in before physics moves anything.
        UpdatesAfter.Add(typeof(NPCSteeringSystem));
        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<NpcJetpackComponent, InputMoverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var mover, out var xform))
        {
            // Shut in a locker or a body bag: it isn't going anywhere by itself.
            if (_container.IsEntityInContainer(uid))
                continue;

            if (xform.GridUid is { } grid)
            {
                // Back on a grid: steering takes over again, and it only steers the NPC's own legs. Not while
                // it sleeps - that is also a player having taken the body over, and flying it is up to them.
                if (_activeQuery.HasComp(uid) &&
                    TryComp<JetpackUserComponent>(uid, out var user) &&
                    TryComp<JetpackComponent>(user.Jetpack, out var worn))
                    _jetpack.SetEnabled(user.Jetpack, worn, false, uid);

                if (now < comp.NextCheck)
                    continue;

                comp.NextCheck = now + comp.CheckInterval;
                comp.LastGrid = grid;
                comp.LastGridPosition = Vector2.Transform(_transform.GetWorldPosition(xform), _transform.GetInvWorldMatrix(grid));
                continue;
            }

            // Where it stands is noted even while it sleeps, but only an awake NPC flies.
            if (_activeQuery.HasComp(uid))
                FlyBack((uid, comp, mover, xform), now);
        }
    }

    private void FlyBack(Entity<NpcJetpackComponent, InputMoverComponent, TransformComponent> ent, TimeSpan now)
    {
        var (uid, comp, mover, xform) = ent;

        if (_mobState.IsIncapacitated(uid) || GetDestination(ent) is not { } destination)
            return;

        if (!TryComp<JetpackUserComponent>(uid, out var user))
        {
            // Lighting it isn't free - it walks the inventory - so it only tries every so often. It fails
            // outright with the jetpack out of gas, and then it simply drifts.
            if (now < comp.NextCheck)
                return;

            comp.NextCheck = now + comp.CheckInterval;

            if (FindWornJetpack(uid) is not { } found)
                return;

            _jetpack.SetEnabled(found, found.Comp, true, uid);

            if (!TryComp(uid, out user))
                return;
        }

        if (!_jetpack.IsProvidingThrust(user.Jetpack) || !_moverQuery.TryComp(user.Jetpack, out var jetMover))
            return;

        var offset = destination - _transform.GetWorldPosition(xform);
        var input = offset.Length() > comp.ArriveDistance
            // Input is taken relative to the mover's frame, the same way steering hands it over.
            ? (-_mover.GetParentGridAngle(mover)).RotateVec(Vector2.Normalize(offset))
            : Vector2.Zero;

        // Whatever steering wanted is dropped: the mover still applies the NPC's own input before the
        // jetpack's, and the two would fight.
        mover.CurTickWalkMovement = Vector2.Zero;
        mover.CurTickSprintMovement = Vector2.Zero;

        // As walking: in space there is no traction to sprint on anyway, and the mover's sprint fallback trips
        // over a jetpack's relayed transform.
        jetMover.CurTickWalkMovement = input;
        jetMover.CurTickSprintMovement = Vector2.Zero;
        jetMover.LastInputTick = _timing.CurTick;
        jetMover.LastInputSubTick = ushort.MaxValue;
    }

    /// <summary>
    /// Where to fly to, in world coordinates: its squad leader if it follows one who is standing on a grid,
    /// otherwise the spot it last stood on. Null if neither is on its map any more - a ship gone to FTL.
    /// </summary>
    private Vector2? GetDestination(Entity<NpcJetpackComponent, InputMoverComponent, TransformComponent> ent)
    {
        var (uid, comp, _, xform) = ent;

        if (_squadQuery.TryComp(uid, out var member) &&
            TryComp<TransformComponent>(member.Leader, out var leaderXform) &&
            leaderXform.GridUid != null &&
            leaderXform.MapUid == xform.MapUid)
        {
            return _transform.GetWorldPosition(leaderXform);
        }

        if (comp.LastGrid is not { } grid ||
            TerminatingOrDeleted(grid) ||
            Transform(grid).MapUid != xform.MapUid)
        {
            return null;
        }

        return Vector2.Transform(comp.LastGridPosition, _transform.GetWorldMatrix(grid));
    }

    /// <summary>
    /// A jetpack worn directly on it. One in a bag would push the bag, not the NPC.
    /// </summary>
    private Entity<JetpackComponent>? FindWornJetpack(EntityUid uid)
    {
        var slots = _inventory.GetSlotEnumerator(uid);
        while (slots.NextItem(out var item))
        {
            if (TryComp<JetpackComponent>(item, out var jetpack))
                return (item, jetpack);
        }

        return null;
    }
}
