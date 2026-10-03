using System.Numerics;
using Content.Server.Mech.Systems;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.Factions;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.Emag.Components;
using Content.Shared.Inventory;
using Content.Shared.Mech.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(NPCUtilitySystem))]
public sealed class AutoPDTurretTargetingTest
{
    [TestCase(MobState.Critical)]
    [TestCase(MobState.SoftCritical)]
    [TestCase(MobState.Dead)]
    public async Task IncapacitatedPilotIsNotTargeted(MobState state)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var mech = entMan.SpawnEntity("MechRipley", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var pilot = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));

            Assert.That(server.System<MechSystem>().TryInsert(mech, pilot, entMan.GetComponent<MechComponent>(mech)), Is.True);
            server.System<MobStateSystem>().ChangeMobState(pilot, state);

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");

            Assert.That(result.GetHighest(), Is.EqualTo(EntityUid.Invalid),
                $"The anti-boarder turret targeted a pilot in the {state} state.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WornAlliedFactionIdPreventsTargeting()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var mech = entMan.SpawnEntity("MechRipley", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var ally = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var id = entMan.SpawnEntity("SHIIDCardEmployee", MapCoordinates.Nullspace);

            Assert.That(server.System<InventorySystem>().TryEquip(ally, id, "id", silent: true, force: true), Is.True);
            Assert.That(server.System<MechSystem>().TryInsert(mech, ally, entMan.GetComponent<MechComponent>(mech)), Is.True);

            var factionId = entMan.GetComponent<FactionIdCardComponent>(id);
            Assert.That(factionId.Faction, Is.EqualTo("SHI"),
                "The preset SHI ID did not learn its faction from its job.");

            var diplomacy = server.System<RatDiplomacySystem>();
            var previousRelation = diplomacy.GetRelation("DSM", "SHI");
            diplomacy.SetRelation("DSM", "SHI", FactionRelation.Alliance, persist: false);

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");
            diplomacy.SetRelation("DSM", "SHI", previousRelation, persist: false);

            Assert.That(result.GetHighest(), Is.EqualTo(EntityUid.Invalid),
                "The anti-boarder turret targeted someone wearing an allied faction ID.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WornHostileFactionIdTargetsHardsuitlessWearer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var boarder = entMan.SpawnEntity("MobHuman",
                new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var id = entMan.SpawnEntity("NCWLIDCardWorker", MapCoordinates.Nullspace);

            Assert.That(server.System<InventorySystem>().TryEquip(boarder, id, "id", silent: true, force: true),
                Is.True);
            Assert.That(entMan.GetComponent<FactionIdCardComponent>(id).Faction, Is.EqualTo("NCWL"));
            Assert.That(server.System<RatDiplomacySystem>().GetRelation("DSM", "NCWL"),
                Is.EqualTo(FactionRelation.War));

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");

            Assert.That(result.GetHighest(), Is.EqualTo(boarder),
                "The anti-boarder turret ignored a hardsuitless wearer of a hostile faction ID.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A spacer's card is no safe-conduct: IND has no seat at the diplomacy table, so every gun but Gliess's,
    /// which counts IND as its own, shoots its wearer even out of a suit.
    /// </summary>
    [TestCase("WeaponTurretAutoPDDSM", true)]
    [TestCase("WeaponTurretAutoPDCMM", false)]
    public async Task SpacerIdIsOnlyTrustedAtGliess(string turretProto, bool targeted)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity(turretProto, map.GridCoords);
            var spacer = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var id = entMan.SpawnEntity("SpacerIDCard", MapCoordinates.Nullspace);

            Assert.That(server.System<InventorySystem>().TryEquip(spacer, id, "id", silent: true, force: true),
                Is.True);
            Assert.That(entMan.GetComponent<FactionIdCardComponent>(id).Faction, Is.EqualTo("IND"));

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");

            Assert.That(result.GetHighest(), Is.EqualTo(targeted ? spacer : EntityUid.Invalid),
                targeted
                    ? "The anti-boarder turret let a hardsuitless spacer walk past."
                    : "Gliess's anti-boarder turret shot a spacer.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Taking the ID off no longer slips anyone past the guns - except the ship's own crew, who are still known
    /// by their allegiance.
    /// </summary>
    [Test]
    public async Task MissingIdTargetsOutsidersButNotOwnCrew()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var crew = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            entMan.EnsureComponent<HullrotFactionComponent>(crew).Faction = "DSM";
            var outsider = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(-1f, 0f)));

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var targets = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets").Entities.Keys;

            Assert.Multiple(() =>
            {
                Assert.That(targets, Does.Contain(outsider),
                    "The anti-boarder turret let a hardsuitless outsider with no ID walk past.");
                Assert.That(targets, Does.Not.Contain(crew),
                    "The anti-boarder turret shot its own crew for not wearing an ID.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrdinaryMechTargetsPilot()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var mech = entMan.SpawnEntity("MechRipley", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var pilot = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var mechComp = entMan.GetComponent<MechComponent>(mech);

            Assert.That(server.System<MechSystem>().TryInsert(mech, pilot, mechComp), Is.True);

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");

            Assert.That(result.GetHighest(), Is.EqualTo(pilot),
                "The anti-boarder turret targeted an ordinary mech instead of its pilot.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FactionMechTargetsMech()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var mech = entMan.SpawnEntity("MechNCWLBogatyr", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var pilot = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            var mechComp = entMan.GetComponent<MechComponent>(mech);

            Assert.That(server.System<MechSystem>().TryInsert(mech, pilot, mechComp), Is.True);

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");

            Assert.That(result.GetHighest(), Is.EqualTo(mech),
                "The anti-boarder turret targeted a faction mech's pilot instead of the mech.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DismantlingDoesNotTargetSameHullrotFaction()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var dsmCrew = entMan.SpawnEntity(null,
                new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            entMan.AddComponent<MobStateComponent>(dsmCrew);
            entMan.AddComponent(dsmCrew, new HullrotFactionComponent { Faction = "DSM" });

            var faction = server.System<NpcFactionSystem>();
            // Model a missing/stale NPC-faction mirror. HullrotFaction remains the authoritative allegiance
            // used by jobs and recruitment, so the anti-boarder turret must still recognize its own crew.
            faction.RemoveFaction(dsmCrew, "DSM");

            var xform = entMan.GetComponent<TransformComponent>(turret);
            server.System<SharedTransformSystem>().Unanchor(turret, xform);

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var result = server.System<NPCUtilitySystem>().GetEntities(htn.Blackboard, "NearbyPDTTargets");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(faction.IsMember(turret, "DSM"), Is.True,
                    "The DSM anti-boarder turret lost its faction.");
                Assert.That(result.GetHighest(), Is.EqualTo(EntityUid.Invalid),
                    "Unanchoring the turret made DSM crew a valid target.");
            }
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("MobCleanBot")]
    [TestCase("MobMedibot")]
    public async Task ServiceBotIsNotTargeted(string bot)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var turret = entMan.SpawnEntity("WeaponTurretAutoPDDSM", map.GridCoords);
            var target = entMan.SpawnEntity(bot, new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));

            var htn = entMan.GetComponent<HTNComponent>(turret);
            var utility = server.System<NPCUtilitySystem>();

            Assert.That(utility.GetEntities(htn.Blackboard, "NearbyPDTTargets").GetHighest(), Is.EqualTo(EntityUid.Invalid),
                $"The anti-boarder turret targeted a {bot}.");

            entMan.AddComponent<EmaggedComponent>(target);
            Assert.That(utility.GetEntities(htn.Blackboard, "NearbyPDTTargets").GetHighest(), Is.EqualTo(target),
                $"The anti-boarder turret ignored an emagged {bot}.");
        });

        await pair.CleanReturnAsync();
    }
}
