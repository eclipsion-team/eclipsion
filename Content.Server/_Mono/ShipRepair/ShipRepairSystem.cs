using Content.Server.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Events;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;

namespace Content.Server._Mono.ShipRepair;

public sealed partial class ShipRepairSystem : SharedShipRepairSystem
{
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

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

            GenerateRepairData(grid);
        }
    }

    private void OnShipBought(Entity<ShuttleComponent> ent, ref ShipBoughtEvent ev)
    {
        GenerateRepairData(ent);
    }

    private void OnInitSnapshot(Entity<InitRepairSnapshotComponent> ent, ref MapInitEvent ev)
    {
        GenerateRepairData(ent);
    }
}
