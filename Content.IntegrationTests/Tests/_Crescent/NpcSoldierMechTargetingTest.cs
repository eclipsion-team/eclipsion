using System.Numerics;
using Content.Server._Crescent.Factions;
using Content.Server._Crescent.NPC;
using Content.Server._Crescent.NpcSquad;
using Content.Server.Mech.Systems;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Inventory;
using Content.Shared.Mech.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(NpcMechTargetingSystem))]
public sealed class NpcSoldierMechTargetingTest
{
    private const string DsmSoldier = "MobSoldierAIDSMRifleman";
    private const string GunTargets = "SoldierGunTargets";

    /// <summary>
    /// A soldier scans the cockpit: a mech whose pilot wears the ID of a faction at war with it is a target, one
    /// crewed by its own side, an empty one and one driven by an unmarked civilian are not - the civilian only
    /// becomes one under kill-all.
    /// </summary>
    [Test]
    public async Task SoldierShootsMechsWithHostilePilots()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            var squad = server.System<NpcSquadSystem>();
            squad.SetKillAll(soldier, false);

            // An unaligned pilot whose only tell is the NCWL card in their ID slot - DSM and NCWL are at war.
            var hostileMech = SpawnMech(entMan, server, map, 4f, out var hostilePilot);
            var id = entMan.SpawnEntity("NCWLIDCardWorker", MapCoordinates.Nullspace);
            Assert.That(server.System<InventorySystem>().TryEquip(hostilePilot, id, "id", silent: true, force: true),
                Is.True);

            var friendlyMech = SpawnMech(entMan, server, map, -4f, out var friendlyPilot);
            entMan.EnsureComponent<HullrotFactionComponent>(friendlyPilot).Faction = "DSM";
            server.System<HullrotNpcFactionSyncSystem>().Sync(friendlyPilot);

            var civilianMech = SpawnMech(entMan, server, map, 6f, out _);
            var emptyMech = entMan.SpawnEntity("MechRipley", new EntityCoordinates(map.Grid, new Vector2(0f, 4f)));

            var utility = server.System<NPCUtilitySystem>();
            var htn = entMan.GetComponent<HTNComponent>(soldier);
            var result = utility.GetEntities(htn.Blackboard, GunTargets);

            Assert.Multiple(() =>
            {
                Assert.That(result.Entities.Keys, Does.Contain(hostileMech),
                    "The soldier ignored a mech piloted by someone wearing an enemy faction's ID.");
                Assert.That(result.Entities.Keys, Has.Count.EqualTo(1),
                    "The soldier wanted to shoot a mech with no enemy in it.");
                Assert.That(result.Entities.Keys, Does.Not.Contain(emptyMech));
                Assert.That(server.System<NpcIffSystem>().ShouldPassThrough(soldier, friendlyMech), Is.True,
                    "The soldier's rounds hit a mech crewed by its own side.");
                Assert.That(server.System<NpcIffSystem>().ShouldPassThrough(soldier, hostileMech), Is.False);
            });

            squad.SetKillAll(soldier, true);
            var killAll = utility.GetEntities(htn.Blackboard, GunTargets).Entities.Keys;

            Assert.Multiple(() =>
            {
                Assert.That(killAll, Does.Contain(civilianMech),
                    "Under kill-all the soldier ignored a mech driven by an unmarked civilian.");
                Assert.That(killAll, Does.Not.Contain(friendlyMech));
                Assert.That(killAll, Does.Not.Contain(emptyMech));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Once the pilot is down the mech is no longer worth shooting.
    /// </summary>
    [Test]
    public async Task MechWithDownedPilotIsNotTargeted()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            var mech = SpawnMech(entMan, server, map, 4f, out var pilot);
            entMan.EnsureComponent<HullrotFactionComponent>(pilot).Faction = "NCWL";
            server.System<HullrotNpcFactionSyncSystem>().Sync(pilot);

            var utility = server.System<NPCUtilitySystem>();
            var htn = entMan.GetComponent<HTNComponent>(soldier);

            Assert.That(utility.GetEntities(htn.Blackboard, GunTargets).Entities.Keys, Does.Contain(mech),
                "The soldier ignored a mech piloted by a member of an enemy faction.");

            server.System<MobStateSystem>().ChangeMobState(pilot, MobState.Critical);

            Assert.That(utility.GetEntities(htn.Blackboard, GunTargets).Entities.Keys, Does.Not.Contain(mech),
                "The soldier kept shooting a mech whose pilot was already down.");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnMech(IEntityManager entMan, Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        Pair.TestMapData map, float x, out EntityUid pilot)
    {
        var coords = new EntityCoordinates(map.Grid, new Vector2(x, 0f));
        var mech = entMan.SpawnEntity("MechRipley", coords);
        pilot = entMan.SpawnEntity("MobHuman", coords);

        Assert.That(server.System<MechSystem>().TryInsert(mech, pilot, entMan.GetComponent<MechComponent>(mech)),
            Is.True);
        return mech;
    }
}
