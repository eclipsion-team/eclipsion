using System.Numerics;
using Content.Shared.Conveyor;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Content.Server.Power.EntitySystems;
using Robust.Server.GameObjects;
using Content.Shared._Crescent.Misc;
using Content.Shared.EntityList;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Misc;

/// <summary>
/// This handles...
/// </summary>
public sealed class PassiveSpawningMachineSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly PowerReceiverSystem _powerReceiver = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PassiveSpawningMachineComponent, BoundUIOpenedEvent>(OnUIOpened);
        SubscribeLocalEvent<PassiveSpawningMachineComponent, AutominerStartMessage>(OnStartMessage);
    }

    private void OnUIOpened(EntityUid uid, PassiveSpawningMachineComponent comp, BoundUIOpenedEvent args)
    {
        UpdateUi(uid, comp);
    }

    private void OnStartMessage(EntityUid uid, PassiveSpawningMachineComponent comp, AutominerStartMessage args)
    {
        if (comp.isActive) return;
        comp.isActive = true;
        comp.cooldownEndTime = _gameTiming.CurTime + TimeSpan.FromSeconds(comp.cycleDuration);
        comp.passedTime = 0;
        UpdateUi(uid, comp);
    }

    private void UpdateUi(EntityUid uid, PassiveSpawningMachineComponent comp)
    {
        _ui.SetUiState(uid, AutominerUiKey.Key,
            new AutominerBoundUserInterfaceState(comp.cooldownEndTime, comp.isActive));
    }

    public override void Update(float delta)
    {
        var query = EntityQueryEnumerator<PassiveSpawningMachineComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.requirePower && !_powerReceiver.IsPowered(uid))
                continue;

            if (comp.manualActivation)
            {
                if (!comp.isActive) continue;

                if (_gameTiming.CurTime >= comp.cooldownEndTime)
                {
                    comp.isActive = false;
                    UpdateUi(uid, comp);
                    continue;
                }
            }

            comp.passedTime += delta;
            if (comp.passedTime < comp.spawnDelay) continue;
            comp.passedTime = 0;

            if (!_proto.TryIndex<EntityListPrototype>(comp.entityListProto, out var entityListProto))
            {
                Log.Error(
                    $"PassiveSpawningMachineSystem: EntityListProto with id {comp.entityListProto} NOT FOUND on entity prototype : {MetaData(uid).EntityPrototype}");
                continue;
            }
            var ent = _random.Pick(entityListProto.EntityIds);
            if (TryGetBeltOutput(uid, out var output))
                Spawn(ent, output);
            else
                SpawnNextToOrDrop(ent, uid);
        }
    }

    /// <summary>
    /// With no belt under the machine, output goes onto an adjacent belt that doesn't run back into it,
    /// preferring one that runs away from it.
    /// </summary>
    private bool TryGetBeltOutput(EntityUid uid, out EntityCoordinates output)
    {
        output = default;
        var xform = Transform(uid);
        if (!xform.Anchored || xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        if (TryGetBeltDirection(gridUid, grid, tile, out _))
            return false;

        var found = false;
        foreach (var dir in Cardinals)
        {
            var offset = dir.ToIntVec();
            if (!TryGetBeltDirection(gridUid, grid, tile + offset, out var beltDir))
                continue;

            var dot = Vector2.Dot(beltDir, new Vector2(offset.X, offset.Y));
            if (dot < -0.5f)
                continue;

            output = _map.GridTileToLocal(gridUid, grid, tile + offset);
            found = true;

            if (dot > 0.5f)
                return true;
        }

        return found;
    }

    private bool TryGetBeltDirection(EntityUid gridUid, MapGridComponent grid, Vector2i tile, out Vector2 direction)
    {
        direction = default;
        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var ent))
        {
            if (!TryComp<ConveyorComponent>(ent, out var conveyor))
                continue;

            var rot = Transform(ent.Value).LocalRotation + conveyor.Angle;
            if (conveyor.State == ConveyorState.Reverse)
                rot += MathF.PI;

            direction = rot.ToWorldVec();
            return true;
        }

        return false;
    }

    private static readonly Direction[] Cardinals = { Direction.South, Direction.East, Direction.North, Direction.West };
}