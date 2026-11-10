using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.RepairStation;
using Content.Server.Power.Components;
using Content.Server.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Shared._Crescent.RepairStation;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Damage;
using Content.Shared.DeviceLinking;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class DrydockReplacementTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ManualReplacementStaysProtectedAfterRemoval(bool installedDuringRepair)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;
        var transforms = server.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            var console = em.SpawnEntity("ComputerShipRepairStation", map.GridCoords);
            var station = em.GetComponent<ShipRepairStationComponent>(console);
            station.ServiceOwnGrid = true;
            station.RepairUnregistered = true;
            station.Free = true;
            em.RemoveComponent<ApcPowerReceiverComponent>(console);
            var actor = em.SpawnEntity(null, map.GridCoords);
            var original = em.SpawnEntity("ComputerRadar", map.GridCoords);
            server.System<ShipDrydockSnapshotSystem>().Capture(map.Grid);
            var part = em.GetComponent<ShipDrydockSnapshotComponent>(map.Grid).Parts.Single(p => p.Original == original);
            em.DeleteEntity(original);

            if (installedDuringRepair)
            {
                em.EventBus.RaiseLocalEvent(console, new ShipRepairStartMessage { Actor = actor });
                Assert.That(station.Jobs.Any(j => j.Part == part), Is.True);
            }

            var replacement = em.SpawnEntity("ComputerRadar", map.GridCoords);
            if (installedDuringRepair)
            {
                station.NextTickTime = TimeSpan.Zero;
                station.EndTime = TimeSpan.Zero;
                server.System<ShipRepairStationSystem>().Update(0);
                Assert.That(station.Target, Is.Null);
            }

            ShipRepairStationUiState Survey()
            {
                em.EventBus.RaiseLocalEvent(console, new BoundUIOpenedEvent(ShipRepairStationUiKey.Key, console, actor));
                Assert.That(server.System<SharedUserInterfaceSystem>().TryGetUiState<ShipRepairStationUiState>(
                    console, ShipRepairStationUiKey.Key, out var state), Is.True);
                return state!;
            }

            if (!installedDuringRepair)
                Assert.That(Survey().MissingParts, Is.Zero);
            transforms.Unanchor(replacement);
            transforms.SetCoordinates(replacement, new EntityCoordinates(map.MapUid, new Vector2(10, 10)));
            Assert.That(Survey().MissingParts, Is.Zero,
                "A manually fitted replacement must not become duplicable by moving it off the hull.");
            em.DeleteEntity(replacement);
            Assert.That(Survey().MissingParts, Is.EqualTo(1),
                "Destroying the replacement must allow a legitimate repair.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LooseProtectedEquipmentIsNotChargedForUnperformedHealing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var console = em.SpawnEntity("ComputerShipRepairStation", map.GridCoords);
            var station = em.GetComponent<ShipRepairStationComponent>(console);
            station.ServiceOwnGrid = true;
            station.RepairUnregistered = true;
            var radar = em.SpawnEntity("ComputerRadar", map.GridCoords);
            server.System<ShipDrydockSnapshotSystem>().Capture(map.Grid);
            server.System<DamageableSystem>().SetAllDamage(radar, em.GetComponent<DamageableComponent>(radar), FixedPoint2.New(1));
            server.System<SharedTransformSystem>().Unanchor(radar);
            em.EventBus.RaiseLocalEvent(console, new BoundUIOpenedEvent(ShipRepairStationUiKey.Key, console, radar));
            Assert.That(server.System<SharedUserInterfaceSystem>().TryGetUiState<ShipRepairStationUiState>(
                console, ShipRepairStationUiKey.Key, out var state), Is.True);
            Assert.That(state!.DamagedParts, Is.Zero,
                "Loose equipment cannot be healed by the slip and must not appear on its bill.");
            Assert.That(state.MissingParts, Is.Zero);
        });

        await pair.CleanReturnAsync();
    }

    private static (EntityUid Console, ShipRepairStationComponent Station, EntityUid Actor) SpawnSlip(IEntityManager em, EntityCoordinates coords)
    {
        var console = em.SpawnEntity("ComputerShipRepairStation", coords);
        var station = em.GetComponent<ShipRepairStationComponent>(console);
        station.ServiceOwnGrid = true;
        station.RepairUnregistered = true;
        station.Free = true;
        em.RemoveComponent<ApcPowerReceiverComponent>(console);
        return (console, station, em.SpawnEntity(null, coords));
    }

    private static void RunToCompletion(RobustIntegrationTest.ServerIntegrationInstance server, EntityUid console, ShipRepairStationComponent station, EntityUid actor)
    {
        server.EntMan.EventBus.RaiseLocalEvent(console, new ShipRepairStartMessage { Actor = actor });
        station.NextTickTime = TimeSpan.Zero;
        station.EndTime = TimeSpan.Zero;
        server.System<ShipRepairStationSystem>().Update(0);
        Assert.That(station.Target, Is.Null);
    }

    private static List<EntityUid> OnTile(RobustIntegrationTest.ServerIntegrationInstance server, EntityUid grid, Vector2i tile, string proto)
    {
        var em = server.EntMan;
        return server.System<SharedMapSystem>()
            .GetAnchoredEntities(grid, em.GetComponent<MapGridComponent>(grid), tile)
            .Where(e => em.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == proto)
            .ToList();
    }

    [Test]
    public async Task CrewWallOfAnotherKindBlocksRebuild()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            var gridComp = em.GetComponent<MapGridComponent>(map.Grid);
            var tile = new Vector2i(1, 0);
            mapSys.SetTile(map.Grid, gridComp, tile, map.Tile.Tile);
            var coords = new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f));

            var (console, station, actor) = SpawnSlip(em, map.GridCoords);
            var wall = em.SpawnEntity("WallSolid", coords);
            server.System<SharedShipRepairSystem>().GenerateRepairData(map.Grid);

            em.DeleteEntity(wall);
            em.SpawnEntity("WallShuttle", coords);

            RunToCompletion(server, console, station, actor);
            Assert.That(OnTile(server, map.Grid, tile, "WallSolid"), Is.Empty,
                "A wall the crew put up must not get the filed wall stacked on top of it.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BrokenGrilleIsReplacedNotStacked()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;

        var tile = new Vector2i(1, 0);
        await server.WaitAssertion(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            var gridComp = em.GetComponent<MapGridComponent>(map.Grid);
            mapSys.SetTile(map.Grid, gridComp, tile, map.Tile.Tile);
            var coords = new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f));

            var (console, station, actor) = SpawnSlip(em, map.GridCoords);
            var grille = em.SpawnEntity("Grille", coords);
            server.System<ShipDrydockSnapshotSystem>().Capture(map.Grid);

            em.DeleteEntity(grille);
            em.SpawnEntity("GrilleBroken", coords);

            RunToCompletion(server, console, station, actor);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(OnTile(server, map.Grid, tile, "Grille"), Has.Count.EqualTo(1));
            Assert.That(OnTile(server, map.Grid, tile, "GrilleBroken"), Is.Empty);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CornerDirectionalWindowsBothRebuilt(bool bothGone)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;

        var tile = new Vector2i(1, 0);
        await server.WaitAssertion(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            var gridComp = em.GetComponent<MapGridComponent>(map.Grid);
            mapSys.SetTile(map.Grid, gridComp, tile, map.Tile.Tile);
            var coords = new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f));
            var transforms = server.System<SharedTransformSystem>();

            var (console, station, actor) = SpawnSlip(em, map.GridCoords);
            var south = em.SpawnEntity("WindowDirectional", coords);
            var east = em.SpawnEntity("WindowDirectional", coords);
            transforms.SetLocalRotation(east, Angle.FromDegrees(90));
            server.System<ShipDrydockSnapshotSystem>().Capture(map.Grid);

            if (bothGone)
                em.DeleteEntity(south);
            em.DeleteEntity(east);

            RunToCompletion(server, console, station, actor);
            Assert.That(OnTile(server, map.Grid, tile, "WindowDirectional"), Has.Count.EqualTo(2));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RebuiltButtonKeepsItsLinks()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            var gridComp = em.GetComponent<MapGridComponent>(map.Grid);
            mapSys.SetTile(map.Grid, gridComp, new Vector2i(1, 0), map.Tile.Tile);
            mapSys.SetTile(map.Grid, gridComp, new Vector2i(2, 0), map.Tile.Tile);

            var (console, station, actor) = SpawnSlip(em, map.GridCoords);
            var button = em.SpawnEntity("SignalButton", new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f)));
            var light = em.SpawnEntity("Poweredlight", new EntityCoordinates(map.Grid, new Vector2(2.5f, 0.5f)));
            var transforms = server.System<SharedTransformSystem>();
            foreach (var uid in new[] { button, light })
            {
                if (!em.GetComponent<TransformComponent>(uid).Anchored)
                    transforms.AnchorEntity(uid);
            }
            server.System<SharedDeviceLinkSystem>().SaveLinks(null, button, light, new List<(string, string)> { ("Pressed", "Toggle") });
            server.System<ShipDrydockSnapshotSystem>().Capture(map.Grid);

            em.DeleteEntity(button);
            RunToCompletion(server, console, station, actor);

            var rebuilt = OnTile(server, map.Grid, new Vector2i(1, 0), "SignalButton").Single();
            var linked = em.GetComponent<DeviceLinkSourceComponent>(rebuilt).LinkedPorts;
            Assert.That(linked.ContainsKey(light), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpawnerMarkersAreNotFiled()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.EntMan;

        var tile = new Vector2i(1, 0);
        await server.WaitAssertion(() =>
        {
            var gridComp = em.GetComponent<MapGridComponent>(map.Grid);
            server.System<SharedMapSystem>().SetTile(map.Grid, gridComp, tile, map.Tile.Tile);

            // A bought hull is filed on the tick it loads, while the markers still sit in the deletion queue.
            em.SpawnEntity("GrilleSpawner", new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f)));
            server.System<ShipDrydockSnapshotSystem>().Capture(map.Grid);

            var parts = em.GetComponent<ShipDrydockSnapshotComponent>(map.Grid).Parts;
            Assert.That(parts.Any(p => p.Proto.Id == "GrilleSpawner"), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundStartSnapshotWaitsForMapInit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var em = server.EntMan;
        var mapSys = server.System<SharedMapSystem>();

        MapId mapId = default;
        EntityUid grid = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out mapId, runMapInit: false);
            var gridEnt = mapSys.CreateGridEntity(mapId);
            grid = gridEnt.Owner;
            mapSys.SetTile(gridEnt, new Vector2i(0, 0), new Tile(1));
            em.SpawnEntity("GrilleSpawner", new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
            em.EnsureComponent<ShuttleComponent>(grid);
            em.EventBus.RaiseLocalEvent(grid, new ShipBoughtEvent());

            Assert.That(em.HasComponent<ShipRepairDataComponent>(grid), Is.False,
                "The hull must not be filed before its spawners have rolled.");
            mapSys.InitializeMap(mapId);
        });

        await server.WaitRunTicks(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(em.HasComponent<ShipRepairDataComponent>(grid), Is.True);
            var protos = em.GetComponent<ShipDrydockSnapshotComponent>(grid).Parts.Select(p => p.Proto.Id).ToList();
            Assert.That(protos, Does.Not.Contain("GrilleSpawner"));
            Assert.That(protos.Any(p => p is "Grille" or "GrilleBroken"), Is.True,
                "What the spawner rolled is part of the hull and belongs on file.");
            mapSys.DeleteMap(mapId);
        });

        await pair.CleanReturnAsync();
    }
}
