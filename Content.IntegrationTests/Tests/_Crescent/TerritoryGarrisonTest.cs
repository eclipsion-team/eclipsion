using System.Collections.Generic;
using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Server._Crescent.Territory;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Economy;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Bank.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Maps;
using Content.Shared.NPC.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class TerritoryGarrisonTest
{
    // the round's own path: a saved grid goes onto the paused, not yet initialized default map
    [Test]
    public async Task LevelSurvivesSavedGridOnPausedMap()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var saves = server.System<PersistentPoiSaveSystem>();
        var campaign = $"GarrisonTest{Guid.NewGuid():N}";
        var regionId = $"integration-test-{Guid.NewGuid():N}";
        var key = $"garrison-save-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid? loaded = null;
        MapId pausedMap = default;

        await server.WaitPost(() => server.System<TerritoryProfileSystem>().SetProfile(campaign, null));
        await server.WaitPost(() => grid = SpawnTerritory(server, regionId, out _));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            saves.MarkPersistent(grid, key);
            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "DSM"), Is.True);
            server.System<PersistentPoiProgressionSystem>().SetLevel(regionId, 3);
            Assert.That(saves.TrySave((grid, entMan.GetComponent<PersistentPoiSaveComponent>(grid))), Is.True);
            entMan.DeleteEntity(entMan.GetComponent<TransformComponent>(grid).MapUid);
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            mapSystem.CreateMap(out pausedMap, runMapInit: false);
            Assert.That(saves.TryLoad(pausedMap, key, 1, new ResPath("/Maps/none.yml"), Vector2.Zero, out loaded), Is.True);
            Assert.That(entMan.GetComponent<PersistentPoiGridComponent>(loaded!.Value).Level, Is.EqualTo(3),
                "The saved grid came up below its saved level.");
            mapSystem.InitializeMap(pausedMap);
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var territory = entMan.GetComponent<PersistentPoiGridComponent>(loaded!.Value);
            Assert.That(territory.Level, Is.EqualTo(3), "The level was lost once the map started.");
            Assert.That(territory.Owner, Is.EqualTo("DSM"));
            Assert.That(server.System<PersistentCaptureRegionSystem>().TryGetLiveRegion(regionId, out _), Is.True,
                "The saved standard never registered.");

            mapSystem.DeleteMap(pausedMap);
            saves.Reset(key);
            server.System<TerritoryProfileSystem>().ArchiveProfile(campaign);
            server.System<TerritoryProfileSystem>().SetProfile(TerritoryProfileSystem.DefaultProfile, null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HeldGroundGetsItsGarrisonByLevel()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saves = server.System<PersistentPoiSaveSystem>();
        var campaign = $"GarrisonTest{Guid.NewGuid():N}";
        var regionId = $"integration-test-{Guid.NewGuid():N}";
        var key = $"garrison-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid flag = default;

        await server.WaitPost(() => server.System<TerritoryProfileSystem>().SetProfile(campaign, null));
        await server.WaitPost(() => grid = SpawnTerritory(server, regionId, out flag));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var garrison = server.System<TerritoryGarrisonSystem>();
            var standard = (flag, entMan.GetComponent<TerritoryGarrisonComponent>(flag));

            Assert.That(garrison.SpawnGarrison(standard), Is.EqualTo(0), "Unclaimed ground got a garrison.");

            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "DSM"), Is.True);
            server.System<PersistentPoiProgressionSystem>().SetLevel(regionId, 2);
            Assert.That(garrison.SpawnGarrison(standard), Is.EqualTo(4));
            Assert.That(Soldiers(entMan, grid), Is.Empty, "The garrison was there before its pods came down.");
            Assert.That(garrison.CountSoldiers(regionId, "DSM", TerritoryRecruitKind.Garrison), Is.EqualTo(4),
                "Pods still in the air don't count toward the garrison.");
        });

        await WaitForPods(server);
        await server.WaitAssertion(() =>
        {
            var soldiers = Soldiers(entMan, null);
            Assert.That(soldiers, Has.Count.EqualTo(4));
            foreach (var soldier in soldiers)
            {
                Assert.That(entMan.GetComponent<NpcFactionMemberComponent>(soldier).Factions, Does.Contain("DSM"));
                Assert.That(entMan.HasComponent<NpcSquadRecruitableComponent>(soldier), Is.False);
            }

            // stamped native like everything else on the grid, and still left out of the save
            saves.MarkPersistent(grid, key);
            Assert.That(saves.TrySave((grid, entMan.GetComponent<PersistentPoiSaveComponent>(grid))), Is.True);
            Assert.That(Soldiers(entMan, grid), Is.Empty, "The garrison was saved with the territory.");
        });

        await server.WaitAssertion(() =>
        {
            var pickets = server.System<TerritoryDronePicketSystem>();
            Assert.That(pickets.SpawnPickets((flag, entMan.GetComponent<TerritoryDronePicketsComponent>(flag))),
                Is.EqualTo(2));

            var drones = entMan.AllEntityQueryEnumerator<TerritoryPicketDroneComponent, TransformComponent>();
            var count = 0;
            while (drones.MoveNext(out _, out _, out var xform))
            {
                count++;
                Assert.That(entMan.GetComponent<IFFComponent>(xform.GridUid!.Value).Faction, Is.EqualTo("DSM"));
            }

            Assert.That(count, Is.EqualTo(2));
        });

        await server.WaitAssertion(() =>
        {
            entMan.DeleteEntity(entMan.GetComponent<TransformComponent>(grid).MapUid);
            saves.Reset(key);
            server.System<TerritoryProfileSystem>().ArchiveProfile(campaign);
            server.System<TerritoryProfileSystem>().SetProfile(TerritoryProfileSystem.DefaultProfile, null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ConsoleRefillsGarrisonAndSendsEscorts()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var campaign = $"GarrisonTest{Guid.NewGuid():N}";
        var regionId = $"integration-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid flag = default;
        EntityUid console = default;
        EntityUid holder = default;

        await server.WaitPost(() => server.System<TerritoryProfileSystem>().SetProfile(campaign, null));
        await server.WaitPost(() => grid = SpawnTerritory(server, regionId, out flag));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "DSM"), Is.True);
            server.System<PersistentPoiProgressionSystem>().SetLevel(regionId, 1);

            console = entMan.SpawnEntity("ComputerPersistentPoiRecruitment", new EntityCoordinates(grid, 4.5f, 4.5f));
            entMan.System<PowerReceiverSystem>().SetNeedsPower(console, false);

            holder = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 3.5f, 4.5f));
            entMan.EnsureComponent<HullrotFactionComponent>(holder).Faction = "DSM";
            entMan.EnsureComponent<BankAccountComponent>(holder).Balance = 100_000;
            // the test grid is in vacuum
            entMan.System<SharedGodmodeSystem>().EnableGodmode(holder);

            server.System<TerritoryGarrisonSystem>().SpawnGarrison((flag, entMan.GetComponent<TerritoryGarrisonComponent>(flag)));
        });

        await WaitForPods(server);
        await server.WaitAssertion(() =>
        {
            var recruitment = server.System<TerritoryRecruitmentSystem>();
            var ent = (console, entMan.GetComponent<TerritoryRecruitmentConsoleComponent>(console));
            var protos = server.ResolveDependency<IPrototypeManager>();
            var marksman = protos.Index<TerritorySoldierRolePrototype>("Marksman");
            var gunner = protos.Index<TerritorySoldierRolePrototype>("Machinegunner");

            Assert.That(recruitment.TryRecruit(ent, holder, TerritoryRecruitKind.Garrison, marksman.ID, 5,
                PaymentSource.Personal, out _, out _, out var error), Is.False, "A full garrison took more.");
            Assert.That(error, Is.EqualTo("territory-recruit-full"));

            Assert.That(recruitment.TryRecruit(ent, holder, TerritoryRecruitKind.Garrison, "NoSuchRole", 1,
                PaymentSource.Personal, out _, out _, out error), Is.False);
            Assert.That(error, Is.EqualTo("territory-recruit-unknown-role"));

            entMan.DeleteEntity(Soldiers(entMan, grid).First());
            Assert.That(recruitment.TryRecruit(ent, holder, TerritoryRecruitKind.Garrison, marksman.ID, 5,
                PaymentSource.Personal, out var refilled, out var price, out _), Is.True);
            Assert.That(refilled, Is.EqualTo(1), "The refill went past the level's garrison.");
            Assert.That(price, Is.EqualTo(marksman.Price));

            Assert.That(recruitment.TryRecruit(ent, holder, TerritoryRecruitKind.Escort, gunner.ID, 5,
                PaymentSource.Personal, out var escorts, out price, out _), Is.True);
            Assert.That(escorts, Is.EqualTo(2), "Level 1 sends two along.");
            Assert.That(price, Is.EqualTo(2 * (gunner.Price + ent.Item2.EscortSurcharge)));
            Assert.That(entMan.GetComponent<BankAccountComponent>(holder).Balance,
                Is.EqualTo(100_000 - marksman.Price - price));
            Assert.That(recruitment.TryRecruit(ent, holder, TerritoryRecruitKind.Escort, gunner.ID, 1,
                PaymentSource.Personal, out _, out _, out _), Is.False, "Escorts in the air were not counted.");
        });

        await WaitForPods(server);
        await server.WaitAssertion(() =>
        {
            // anywhere: on a grid this small one can get shoved off the edge as they land
            var soldiers = Soldiers(entMan, null);
            var escorts = soldiers.Where(s => entMan.GetComponent<TerritoryGarrisonSoldierComponent>(s).Escort).ToList();
            Assert.That(soldiers, Has.Count.EqualTo(4), "A pod never let its soldier out.");
            Assert.That(soldiers.Count(s => entMan.GetComponent<MetaDataComponent>(s).EntityPrototype?.ID == "MobSoldierAIDSMMarksman"),
                Is.GreaterThanOrEqualTo(1), "The refill didn't send the kind of soldier picked.");
            Assert.That(escorts, Has.Count.EqualTo(2));
            foreach (var escort in escorts)
            {
                Assert.That(entMan.GetComponent<MetaDataComponent>(escort).EntityPrototype?.ID,
                    Is.EqualTo("MobSoldierAIDSMGunner"), "An escort wasn't the kind picked.");
                Assert.That(entMan.GetComponent<NpcSquadMemberComponent>(escort).Leader, Is.EqualTo(holder),
                    "An escort didn't join the squad of whoever called it.");
            }

            entMan.DeleteEntity(entMan.GetComponent<TransformComponent>(grid).MapUid);
            server.System<TerritoryProfileSystem>().ArchiveProfile(campaign);
            server.System<TerritoryProfileSystem>().SetProfile(TerritoryProfileSystem.DefaultProfile, null);
        });

        await pair.CleanReturnAsync();
    }

    // longest fall plus the spacing between pods of one call
    private static async Task WaitForPods(RobustIntegrationTest.ServerIntegrationInstance server)
    {
        var tickRate = server.ResolveDependency<IGameTiming>().TickRate;
        await server.WaitRunTicks(tickRate * 10);
    }

    private static EntityUid SpawnTerritory(RobustIntegrationTest.ServerIntegrationInstance server, string regionId, out EntityUid flag)
    {
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];

        mapSystem.CreateMap(out var mapId);
        var grid = mapSystem.CreateGridEntity(mapId);
        for (var x = -6; x <= 6; x++)
        {
            for (var y = -6; y <= 6; y++)
            {
                mapSystem.SetTile(grid, new Vector2i(x, y), new Tile(plating.TileId));
            }
        }

        flag = entMan.CreateEntityUninitialized("PersistentCaptureRegionFlag", new EntityCoordinates(grid, 0.5f, 0.5f));
        entMan.GetComponent<PersistentCaptureRegionComponent>(flag).RegionId = regionId;
        entMan.InitializeAndStartEntity(flag);
        return grid.Owner;
    }

    private static List<EntityUid> Soldiers(IEntityManager entMan, EntityUid? grid)
    {
        var soldiers = new List<EntityUid>();
        var query = entMan.AllEntityQueryEnumerator<TerritoryGarrisonSoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (grid == null || xform.GridUid == grid)
                soldiers.Add(uid);
        }

        return soldiers;
    }
}
