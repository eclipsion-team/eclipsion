using Content.Server.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Events;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Shared.Timing;

namespace Content.Server._Mono.ShipRepair;

public sealed partial class ShipRepairSystem : SharedShipRepairSystem
{
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleComponent, ShipBoughtEvent>(OnShipBought);
        SubscribeLocalEvent<InitRepairSnapshotComponent, MapInitEvent>(OnInitSnapshot);
        // Eclipsion: broadcast, so every station is covered without a component on its prototype.
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);

        InitCommands();
        InitGhosts();
    }

    /// <summary>
    /// Eclipsion: files a blueprint of every station grid as it comes up, while it is still whole.
    /// Only bought ships were ever filed, so a mapped-in station had nothing to be restored to and
    /// the override slip could only snapshot it after the fact - which files the damage as well.
    /// </summary>
    private void OnStationPostInit(ref StationPostInitEvent ev)
    {
        foreach (var grid in ev.Station.Comp.Grids)
        {
            if (HasComp<ShipRepairDataComponent>(grid))
                continue;

            QueueSnapshot(grid);
        }
    }

    private void OnShipBought(Entity<ShuttleComponent> ent, ref ShipBoughtEvent ev)
    {
        QueueSnapshot(ent);
    }

    private void OnInitSnapshot(Entity<InitRepairSnapshotComponent> ent, ref MapInitEvent ev)
    {
        QueueSnapshot(ent);
    }

    // Eclipsion: snapshots wait until the grid is map-initialised and a tick has passed. Before that
    // the hull still holds its spawner markers (round-start stations, or the purchase tick while they
    // sit in the deletion queue), and the slip would file them and respawn their loot over the
    // original - wall randomizers, vending, potted plants.
    private readonly Dictionary<EntityUid, GameTick> _pendingSnapshots = new();
    private readonly List<EntityUid> _snapshotsDue = new();

    private void QueueSnapshot(EntityUid grid)
    {
        _pendingSnapshots.TryAdd(grid, _timing.CurTick);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pendingSnapshots.Count == 0)
            return;

        _snapshotsDue.Clear();
        foreach (var (grid, queued) in _pendingSnapshots)
        {
            if (TerminatingOrDeleted(grid)
                || queued != _timing.CurTick && LifeStage(grid) >= EntityLifeStage.MapInitialized)
            {
                _snapshotsDue.Add(grid);
            }
        }

        foreach (var grid in _snapshotsDue)
        {
            _pendingSnapshots.Remove(grid);
            if (!TerminatingOrDeleted(grid))
                GenerateRepairData(grid);
        }
    }
}
