using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server._Crescent.NPC;
using Content.Server._Crescent.NpcSquad;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Server.Atmos.Components;
using Content.Server.Damage.Systems;
using Content.Server.Gravity;
using Content.Server.Power.Components;
using Content.Shared._Crescent.Barricades;
using Content.Shared._Crescent.HardsuitInjection;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Access.Components;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Doors.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Gravity;
using Content.Shared.Inventory;
using Content.Shared.Language.Components;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Strip.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(NpcTacticalSystem))]
public sealed class NpcSoldierTacticsTest
{
    private const string DsmSoldier = "MobSoldierAIDSMRifleman";
    private const string NcwlSoldier = "MobSoldierAINCWLRifleman";

    [Test]
    public async Task SoldierSpawnsWithSparesAndDressings()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default;
        await server.WaitPost(() => soldier = server.EntMan.SpawnEntity(DsmSoldier, map.GridCoords));
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<GunSystem>().TryGetGun(soldier, out var gun, out _), Is.True,
                "The soldier spawned without a gun in hand.");
            Assert.That(server.System<NpcGunHandlingSystem>().CountSpareMagazines(soldier, gun), Is.EqualTo(4),
                "The soldier's spare magazines didn't all make it onto them.");

            var tactical = server.System<NpcTacticalSystem>();
            var hasDressing = false;
            foreach (var carried in tactical.EnumerateCarried(soldier))
            {
                if (server.EntMan.GetComponent<MetaDataComponent>(carried).EntityPrototype?.ID == "SoldierAIGauze")
                    hasDressing = true;
            }

            Assert.That(hasDressing, Is.True, "The soldier's chest rig came without field dressings.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundsPassThroughOwnSideOnly()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            var enemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(30f, 0f)));
            var dsmPlayer = SpawnPlayer(entMan, map.GridCoords, "DSM");
            var ncwlPlayer = SpawnPlayer(entMan, map.GridCoords, "NCWL");
            var civilian = entMan.SpawnEntity("MobHuman", map.GridCoords);

            var iff = server.System<NpcIffSystem>();

            Assert.Multiple(() =>
            {
                Assert.That(iff.ShouldPassThrough(soldier, dsmPlayer), Is.True, "A DSM soldier shot a DSM player.");
                Assert.That(iff.ShouldPassThrough(soldier, soldier), Is.False);
                Assert.That(iff.ShouldPassThrough(soldier, ncwlPlayer), Is.False);
                Assert.That(iff.ShouldPassThrough(soldier, enemy), Is.False);
                Assert.That(iff.ShouldPassThrough(soldier, civilian), Is.False);
                Assert.That(iff.ShouldPassThrough(civilian, dsmPlayer), Is.False,
                    "Rounds from someone without IFF passed through a mob.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A friendly in the way shows up in the raw line check, but a soldier with IFF keeps shooting through
    /// them anyway: its rounds pass through its own side.
    /// </summary>
    [Test]
    public async Task KeepsFiringPastFriendlyInTheWay()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var mapId = map.MapId;

        EntityUid soldier = default, friendly = default, target = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            soldier = entMan.SpawnEntity(DsmSoldier, new MapCoordinates(new Vector2(0f, 10f), mapId));
            friendly = SpawnPlayer(entMan, new MapCoordinates(new Vector2(3f, 10f), mapId), "DSM");
            target = entMan.SpawnEntity(NcwlSoldier, new MapCoordinates(new Vector2(6f, 10f), mapId));
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var tactical = server.System<NpcTacticalSystem>();
            var from = new MapCoordinates(new Vector2(0f, 10f), mapId);
            var to = new MapCoordinates(new Vector2(6f, 10f), mapId);

            Assert.That(tactical.HasClearShot(soldier, from, target, to), Is.False,
                "The line check didn't notice the friendly in the way.");
            Assert.That(tactical.UpdateLineOfFire(soldier, target), Is.False,
                "The soldier held fire for a friendly its rounds would have passed straight through.");

            server.EntMan.DeleteEntity(friendly);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var tactical = server.System<NpcTacticalSystem>();
            var from = new MapCoordinates(new Vector2(0f, 10f), mapId);
            var to = new MapCoordinates(new Vector2(6f, 10f), mapId);

            Assert.That(tactical.HasClearShot(soldier, from, target, to), Is.True,
                "The soldier held fire with nobody in the way.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShootsOverOwnBarricadeButNotThroughItsFront()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default, target = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSystem = server.System<SharedMapSystem>();

            for (var x = -2; x <= 7; x++)
            {
                mapSystem.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, 0), map.Tile.Tile);
            }

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            target = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(5.5f, 0.5f)));

            // Facing the target, the way TryPickBarricadeSpot puts one up in front of the soldier.
            var barricade = entMan.SpawnEntity("CrescentBarricadeMetal", new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f)));
            server.System<SharedTransformSystem>().SetLocalRotation(barricade, new Vector2(1f, 0f).ToWorldAngle());

            var npc = server.System<NPCSystem>();
            npc.SleepNPC(soldier);
            npc.SleepNPC(target);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var tactical = server.System<NpcTacticalSystem>();
            var xform = server.System<SharedTransformSystem>();
            var soldierPos = xform.GetMapCoordinates(soldier);
            var targetPos = xform.GetMapCoordinates(target);

            Assert.That(tactical.HasClearShot(soldier, soldierPos, target, targetPos), Is.True,
                "The soldier wouldn't fire over its own barricade, which lets its rounds through.");
            Assert.That(tactical.HasClearShot(target, targetPos, soldier, soldierPos), Is.False,
                "The enemy had a clear shot through the front of the barricade.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecruitOrderAndDismiss()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var squad = server.System<NpcSquadSystem>();

            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            var leader = SpawnPlayer(entMan, map.GridCoords, "DSM");
            var outsider = SpawnPlayer(entMan, map.GridCoords, "NCWL");
            var nearEnemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(5f, 0f)));
            var farEnemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(40f, 0f)));

            Assert.That(squad.CanRecruit(outsider, soldier, out _), Is.False,
                "An NCWL player could recruit a DSM soldier.");
            Assert.That(squad.TryRecruit(leader, soldier), Is.True);

            Assert.That(entMan.TryGetComponent<NpcSquadMemberComponent>(soldier, out var member), Is.True);
            Assert.That(entMan.GetComponent<NpcSquadLeaderComponent>(leader).Members, Does.Contain(soldier));
            Assert.That(entMan.GetComponent<NpcIffComponent>(soldier).SquadLeader, Is.EqualTo(leader));

            var blackboard = entMan.GetComponent<HTNComponent>(soldier).Blackboard;
            Assert.That(blackboard.GetValueOrDefault<Enum>(NPCBlackboard.CurrentOrders, entMan),
                Is.EqualTo(NpcSquadOrder.Follow));

            Assert.Multiple(() =>
            {
                Assert.That(squad.IsTargetAllowed(soldier, nearEnemy), Is.True);
                Assert.That(squad.IsTargetAllowed(soldier, farEnemy), Is.False,
                    "A following soldier would chase an enemy far from its leader.");
                Assert.That(squad.IsTargetAllowed(soldier, leader), Is.False);
            });

            squad.SetOrder(soldier, member!, NpcSquadOrder.Attack);
            Assert.That(squad.IsTargetAllowed(soldier, farEnemy), Is.True, "An attacking soldier was leashed.");

            squad.SetOrder(soldier, member!, NpcSquadOrder.HoldFire);
            Assert.That(squad.IsTargetAllowed(soldier, nearEnemy), Is.False, "A soldier ignored a hold-fire order.");

            squad.Dismiss(soldier);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<NpcSquadMemberComponent>(soldier), Is.False);
                Assert.That(entMan.HasComponent<NpcSquadLeaderComponent>(leader), Is.False,
                    "The leader kept their squad after its last member left.");
                Assert.That(entMan.GetComponent<NpcIffComponent>(soldier).SquadLeader, Is.Null);
                Assert.That(blackboard.GetValueOrDefault<Enum>(NPCBlackboard.CurrentOrders, entMan), Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SquadGoesBackToItsOrdersWhenLeaderRecovers()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var squad = server.System<NpcSquadSystem>();
            var mobState = server.System<MobStateSystem>();

            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            var leader = SpawnPlayer(entMan, map.GridCoords, "DSM");
            server.System<NPCSystem>().SleepNPC(soldier);

            Assert.That(squad.TryRecruit(leader, soldier), Is.True);
            var member = entMan.GetComponent<NpcSquadMemberComponent>(soldier);
            squad.SetOrder(soldier, member, NpcSquadOrder.Attack);

            // Straight to the state rather than through damage, which would start taking limbs off.
            mobState.ChangeMobState(leader, MobState.Critical);
            Assert.That(mobState.IsCritical(leader), Is.True);
            Assert.That(member.Order, Is.EqualTo(NpcSquadOrder.Defend), "The squad didn't close in around its downed leader.");

            mobState.ChangeMobState(leader, MobState.Alive);
            Assert.That(mobState.IsAlive(leader), Is.True);
            Assert.That(member.Order, Is.EqualTo(NpcSquadOrder.Attack),
                "The squad kept holding after its leader got back up.");
            Assert.That(member.OrderBeforeLeaderDown, Is.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SoldierCarriesMedkitSteelAndSuitPens()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default, shotgunner = default;
        await server.WaitPost(() =>
        {
            soldier = server.EntMan.SpawnEntity(DsmSoldier, map.GridCoords);
            shotgunner = server.EntMan.SpawnEntity("MobSoldierAIDSMShotgunner", map.GridCoords);
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var tactical = server.System<NpcTacticalSystem>();

            var hasMedkit = false;
            foreach (var carried in tactical.EnumerateCarried(soldier))
            {
                if (entMan.GetComponent<MetaDataComponent>(carried).EntityPrototype?.ID == "MedkitCombatFilled")
                    hasMedkit = true;
            }

            Assert.That(hasMedkit, Is.True, "The soldier spawned without its combat medkit.");
            Assert.That(tactical.CountMaterial(soldier, "Steel"), Is.EqualTo(10));

            Assert.That(server.System<InventorySystem>().TryGetSlotEntity(soldier, "outerClothing", out var suit), Is.True);
            Assert.That(server.System<ItemSlotsSystem>().TryGetSlot(suit!.Value, HardsuitInjectorComponent.SlotOneId, out var slot), Is.True);
            Assert.That(slot!.Item, Is.Not.Null, "The soldier's hardsuit injector came unloaded.");

            Assert.That(server.System<GunSystem>().TryGetGun(shotgunner, out var shotgun, out _), Is.True);
            Assert.That(server.System<NpcGunHandlingSystem>().CountSpareMagazines(shotgunner, shotgun), Is.GreaterThan(0),
                "The shotgunner spawned without any spare shells.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SquadSoldierBuildsBarricadeAndTendsLeader()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default, leader = default, enemy = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSystem = server.System<SharedMapSystem>();

            // Room to build in: the test grid starts out as a single tile.
            for (var x = -6; x <= 6; x++)
            {
                for (var y = -6; y <= 6; y++)
                {
                    mapSystem.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            leader = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(-0.5f, 0.5f)), "DSM");
            // The test map is vacuum, and low pressure hurts as blunt damage, which would drown out the treatment.
            entMan.RemoveComponent<BarotraumaComponent>(leader);
            enemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(5.5f, 0.5f)));

            // The test drives the soldier by hand. Left running, its own HTN starts the same barricade and the
            // duplicate do-after cancels this one, or the two of them walk off to fight each other.
            var npc = server.System<NPCSystem>();
            npc.SleepNPC(soldier);
            npc.SleepNPC(enemy);
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var squad = server.System<NpcSquadSystem>();
            var tactical = server.System<NpcTacticalSystem>();

            Assert.That(squad.TryRecruit(leader, soldier), Is.True);
            squad.SetOrder(soldier, server.EntMan.GetComponent<NpcSquadMemberComponent>(soldier), NpcSquadOrder.Defend);

            Assert.That(tactical.WantsToFortify(soldier, 14f), Is.True,
                "A defending soldier with steel and an enemy in sight didn't want to dig in.");
            Assert.That(tactical.TryPickBarricadeSpot(soldier, 14f, out var spot, out var rotation), Is.True);
            Assert.That(tactical.TryStartBarricade(soldier, spot!.Value, rotation), Is.True);
        });

        // Build time plus a little.
        await server.WaitRunTicks(server.Timing.TickRate * 6);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var tactical = server.System<NpcTacticalSystem>();

            Assert.That(tactical.IsBarricadeFinished(soldier), Is.True, "The barricade never went up.");
            Assert.That(tactical.CountMaterial(soldier, "Steel"), Is.EqualTo(5), "Building didn't use the steel.");

            var barricades = 0;
            var query = entMan.EntityQueryEnumerator<DirectionalBarricadeComponent>();
            while (query.MoveNext(out _, out _))
            {
                barricades++;
            }

            Assert.That(barricades, Is.EqualTo(1));

            // Now hurt the leader and have the soldier patch them up.
            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Blunt"), 60);
            server.System<DamageableSystem>().TryChangeDamage(leader, damage, true);

            Assert.That(tactical.ShouldTendLeader(soldier), Is.True, "Nobody went to help the wounded leader.");
            Assert.That(tactical.TryTendLeader(soldier, leader, entMan.GetComponent<NpcTacticalComponent>(soldier)), Is.True);
        });

        // The test map has no air, so only brute damage says anything about the treatment.
        FixedPoint2 before = default;
        await server.WaitPost(() => before = server.EntMan.GetComponent<DamageableComponent>(leader).Damage.DamageDict["Blunt"]);
        await server.WaitRunTicks(server.Timing.TickRate * 8);

        await server.WaitAssertion(() =>
        {
            var after = server.EntMan.GetComponent<DamageableComponent>(leader).Damage.DamageDict["Blunt"];
            Assert.That(after, Is.LessThan(before), "The soldier's treatment didn't heal the leader.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Two hostile soldiers on a floor in plain sight of each other actually open fire.
    /// </summary>
    [Test]
    public async Task HostileSoldiersOpenFire()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        // No player is anywhere near, and NPCs out of every player's range are put to sleep.
        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid dsm = default, ncwl = default;

        await server.WaitPost(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            var plating = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);

            for (var x = -4; x <= 16; x++)
            {
                for (var y = -4; y <= 4; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), plating);
                }
            }
        });
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            dsm = server.EntMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            ncwl = server.EntMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(10.5f, 0.5f)));
        });

        // Both of them should have opened up within a few seconds. Track the most each has taken, since the
        // one still standing patches itself up afterwards.
        var piercing = new[] { FixedPoint2.Zero, FixedPoint2.Zero };

        for (var second = 0; second < 5; second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                var soldiers = new[] { dsm, ncwl };

                for (var i = 0; i < soldiers.Length; i++)
                {
                    if (server.EntMan.TryGetComponent<DamageableComponent>(soldiers[i], out var damage) &&
                        damage.Damage.DamageDict.TryGetValue("Piercing", out var taken))
                    {
                        piercing[i] = FixedPoint2.Max(piercing[i], taken);
                    }
                }
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(piercing[1], Is.GreaterThan(FixedPoint2.Zero), "The DSM soldier never hit the NCWL soldier.");
            Assert.That(piercing[0], Is.GreaterThan(FixedPoint2.Zero), "The NCWL soldier never hit the DSM soldier.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Soldiers talk in their side's language, not the sign language MobHuman comes with - including the role
    /// variants, which take everything shared from their role archetype first.
    /// </summary>
    [TestCase("MobSoldierAIDSMRifleman", "LowImperial")]
    [TestCase("MobSoldierAIDSMGunner", "LowImperial")]
    [TestCase("MobSoldierAINCWLShock", "Dockta")]
    [TestCase("MobSoldierAITFSCMarksman", "Freespeak")]
    [TestCase("MobSoldierAISHIShotgunner", "Kaishago")]
    [TestCase("MobSoldierAIINDRifleman", "Tradeband")]
    public async Task SoldierSpeaksItsFactionsLanguage(string proto, string language)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default;
        await server.WaitPost(() => soldier = server.EntMan.SpawnEntity(proto, map.GridCoords));
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var speaker = server.EntMan.GetComponent<LanguageSpeakerComponent>(soldier);
            Assert.That(speaker.CurrentLanguage, Is.EqualTo(language));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Every soldier wears its side's ID card, made out in its own name.
    /// </summary>
    [TestCase("MobSoldierAIDSMRifleman", "DSMIDCardLevyman")]
    [TestCase("MobSoldierAINCWLShock", "NCWLIDCardHomeguardSoldat")]
    [TestCase("MobSoldierAITFSCMarksman", "TFSCIDCardInfanteer")]
    [TestCase("MobSoldierAISHIShotgunner", "SHIIDCardCorpSec")]
    [TestCase("MobSoldierAIINDGunner", "SpacerIDCard")]
    public async Task SoldierWearsNamedFactionId(string proto, string card)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default;
        await server.WaitPost(() => soldier = server.EntMan.SpawnEntity(proto, map.GridCoords));
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;

            Assert.That(server.System<InventorySystem>().TryGetSlotEntity(soldier, "id", out var id), Is.True,
                "The soldier spawned without an ID.");
            Assert.That(entMan.GetComponent<MetaDataComponent>(id!.Value).EntityPrototype?.ID, Is.EqualTo(card));

            var idCard = entMan.GetComponent<IdCardComponent>(id.Value);
            Assert.That(idCard.FullName, Is.EqualTo(entMan.GetComponent<MetaDataComponent>(soldier).EntityName),
                "The soldier's ID isn't made out in its name.");
            Assert.That(idCard.LocalizedJobTitle, Is.Not.Null.And.Not.Empty, "The soldier's ID has no job on it.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Nothing can be stripped off a soldier, and when it dies it and everything it carried are gone, with
    /// only ash left behind.
    /// </summary>
    [Test]
    public async Task SoldierCantBeLootedAndDustsOnDeath()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default;
        var carried = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            soldier = server.EntMan.SpawnEntity(DsmSoldier, map.GridCoords);
            server.System<NPCSystem>().SleepNPC(soldier);
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.HasComponent<StrippableComponent>(soldier), Is.False,
                "The soldier can still be stripped.");

            carried.AddRange(server.System<NpcTacticalSystem>().EnumerateCarried(soldier));
            Assert.That(carried, Is.Not.Empty);

            server.System<MobStateSystem>().ChangeMobState(soldier, MobState.Dead);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;

            Assert.That(entMan.Deleted(soldier), Is.True, "The dead soldier didn't turn to dust.");

            foreach (var item in carried)
            {
                Assert.That(entMan.Deleted(item), Is.True,
                    $"{entMan.ToPrettyString(item)} was left behind by the dead soldier.");
            }

            var ash = 0;
            var query = entMan.EntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var meta))
            {
                if (meta.EntityPrototype?.ID == "Ash")
                    ash++;
            }

            Assert.That(ash, Is.EqualTo(1), "The soldier left no ash behind.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// While one squadmate patches the leader up, the rest stand watch around them rather than wandering off.
    /// </summary>
    [Test]
    public async Task SquadGuardsWhileLeaderIsTended()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid medic = default, guard = default, leader = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSystem = server.System<SharedMapSystem>();

            for (var x = -6; x <= 6; x++)
            {
                for (var y = -6; y <= 6; y++)
                {
                    mapSystem.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            leader = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)), "DSM");
            entMan.RemoveComponent<BarotraumaComponent>(leader);
            medic = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f)));
            guard = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(-2.5f, 0.5f)));

            var npc = server.System<NPCSystem>();
            npc.SleepNPC(medic);
            npc.SleepNPC(guard);
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var squad = server.System<NpcSquadSystem>();
            var tactical = server.System<NpcTacticalSystem>();
            var xform = server.System<SharedTransformSystem>();

            Assert.That(squad.TryRecruit(leader, medic), Is.True);
            Assert.That(squad.TryRecruit(leader, guard), Is.True);

            Assert.That(tactical.ShouldGuardLeader(guard), Is.False, "A soldier stood guard with nobody hurt.");

            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Blunt"), 60);
            server.System<DamageableSystem>().TryChangeDamage(leader, damage, true);

            Assert.That(tactical.ShouldTendLeader(medic), Is.True, "Nobody went to help the wounded leader.");
            Assert.That(tactical.ShouldTendLeader(guard), Is.False, "Two soldiers dropped everything for the leader.");

            Assert.Multiple(() =>
            {
                Assert.That(tactical.ShouldGuardLeader(guard), Is.True,
                    "The other soldier didn't stand watch while the leader was being patched up.");
                Assert.That(tactical.ShouldGuardLeader(medic), Is.False, "The medic stood guard over its own patient.");
            });

            Assert.That(tactical.TryPickGuardSpot(guard, out var spot), Is.True, "The guard found nowhere to stand.");

            var spotPos = xform.ToMapCoordinates(spot!.Value).Position;
            var leaderPos = xform.GetMapCoordinates(leader).Position;
            var medicPos = xform.GetMapCoordinates(medic).Position;
            var fromLeader = (spotPos - leaderPos).Length();

            Assert.Multiple(() =>
            {
                Assert.That(fromLeader, Is.InRange(0.9f, 3.5f), "The guard spot isn't close around the leader.");
                Assert.That((spotPos - medicPos).Length(), Is.GreaterThan(fromLeader),
                    "The guard stands on the medic's side instead of watching the other way.");
            });

            Assert.That(squad.TryGetLeash(guard, out _, out var range), Is.True);
            Assert.That(range, Is.LessThanOrEqualTo(4f), "A guard may still wander off to fight.");

            Assert.That(tactical.UpdateGuard(guard, 0.1f), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A soldier shot at from behind a wall turns round and goes to look, and tells the friend next to it -
    /// instead of standing there taking it because the shooter isn't in sight.
    /// </summary>
    [Test]
    public async Task SoldierFollowsUpWhereItWasShotFrom()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default, friend = default, shooter = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            GiveGravity(server.EntMan, map.Grid);

            for (var x = -2; x <= 14; x++)
            {
                for (var y = -4; y <= 4; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            // A wall between them with a way round it at the top.
            for (var y = -4; y <= 2; y++)
            {
                entMan.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, new Vector2(6.5f, y + 0.5f)));
            }

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            friend = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, -2.5f)));
            shooter = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(10.5f, 0.5f)));

            // Only there to be found.
            server.System<NPCSystem>().SleepNPC(shooter);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var tactical = server.System<NpcTacticalSystem>();

            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Piercing"), 5);
            server.System<DamageableSystem>().TryChangeDamage(soldier, damage, true, origin: shooter);

            Assert.That(entMan.GetComponent<NpcTacticalComponent>(soldier).Lead, Is.Not.Null,
                "Getting shot didn't tell the soldier where from.");
            Assert.That(tactical.HasLeadToSearch(soldier), Is.True);
            Assert.That(entMan.GetComponent<NpcTacticalComponent>(friend).Lead, Is.Not.Null,
                "The soldier's friend next to it wasn't told.");
        });

        // It should go round the wall and find the shooter, or at least get well on the way.
        var xformSys = server.System<SharedTransformSystem>();
        var engaged = false;
        var closest = float.MaxValue;

        for (var second = 0; second < 10 && !engaged; second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                engaged = server.EntMan.HasComponent<NPCRangedCombatComponent>(soldier);
                var distance = (xformSys.GetWorldPosition(soldier) - xformSys.GetWorldPosition(shooter)).Length();
                closest = MathF.Min(closest, distance);
            });
        }

        Assert.That(engaged || closest < 6f, Is.True,
            $"The soldier never went after whoever shot it (got within {closest:F1} of them).");

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// From cover, a soldier steps out to one side to shoot rather than standing on the same tile all fight.
    /// </summary>
    [Test]
    public async Task SoldierPeeksSidewaysFromCover()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid soldier = default, target = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            GiveGravity(server.EntMan, map.Grid);

            for (var x = -3; x <= 10; x++)
            {
                for (var y = -3; y <= 3; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            target = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(8.5f, 0.5f)));

            var npc = server.System<NPCSystem>();
            npc.SleepNPC(soldier);
            npc.SleepNPC(target);
        });
        await server.WaitRunTicks(1);

        await server.WaitPost(() => server.System<NpcTacticalSystem>().BeginEngagement(soldier, peek: true));

        // Longest gap between peeks.
        await server.WaitRunTicks(server.Timing.TickRate * 6);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var tactical = server.System<NpcTacticalSystem>();
            var comp = entMan.GetComponent<NpcTacticalComponent>(soldier);

            tactical.UpdatePeek(soldier, target, moving: false);

            Assert.That(comp.PeekTile, Is.Not.Null, "The soldier never stepped out to shoot.");

            var own = entMan.GetComponent<TransformComponent>(soldier).Coordinates.Position;
            var step = comp.PeekTile!.Value.Position - own;

            Assert.Multiple(() =>
            {
                Assert.That(step.Length(), Is.InRange(0.5f, 1.6f), "The peek tile isn't next to the soldier.");
                Assert.That(step.X, Is.LessThan(0.9f), "The soldier stepped straight at its target.");
            });

            tactical.EndEngagement(soldier);
            Assert.That(comp.PeekTile, Is.Null);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A soldier with nothing to do walks about and looks around instead of standing frozen where it spawned.
    /// </summary>
    [Test]
    public async Task IdleSoldierMovesAndLooksAround()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default;
        Vector2 startPos = default;
        Angle startRot = default;

        await server.WaitPost(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            GiveGravity(server.EntMan, map.Grid);

            for (var x = -6; x <= 6; x++)
            {
                for (var y = -6; y <= 6; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            soldier = server.EntMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
        });
        await server.WaitRunTicks(5);

        var xformSys = server.System<SharedTransformSystem>();

        await server.WaitPost(() =>
        {
            startPos = xformSys.GetWorldPosition(soldier);
            startRot = xformSys.GetWorldRotation(soldier);
        });

        var moved = false;
        var turned = false;

        for (var second = 0; second < 12 && !(moved && turned); second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                moved |= (xformSys.GetWorldPosition(soldier) - startPos).Length() > 0.5f;
                turned |= !xformSys.GetWorldRotation(soldier).EqualsApprox(startRot, 0.1);
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True, "The idle soldier never moved.");
            Assert.That(turned, Is.True, "The idle soldier never looked anywhere else.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Ordered to attack, a squad soldier pushes in on the enemy in bounds instead of holding at its
    /// preferred range, and stops once it's close enough for its gun.
    /// </summary>
    [Test]
    public async Task AttackOrderPushesInOnEnemy()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default, leader = default, enemy = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            GiveGravity(server.EntMan, map.Grid);

            for (var x = -4; x <= 16; x++)
            {
                for (var y = -4; y <= 4; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            leader = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(-2.5f, 0.5f)), "DSM");
            entMan.RemoveComponent<BarotraumaComponent>(leader);
            enemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(12.5f, 0.5f)));

            // Only there to be pushed in on: it doesn't fight back, and doesn't go down before the soldier
            // gets anywhere.
            server.System<NPCSystem>().SleepNPC(enemy);
            server.System<GodmodeSystem>().EnableGodmode(enemy);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var squad = server.System<NpcSquadSystem>();
            var tactical = server.System<NpcTacticalSystem>();

            Assert.That(squad.TryRecruit(leader, soldier), Is.True);
            squad.SetOrder(soldier, server.EntMan.GetComponent<NpcSquadMemberComponent>(soldier), NpcSquadOrder.Attack);

            Assert.That(tactical.IsAssaulting(soldier), Is.True);
            Assert.That(tactical.TryFindAdvanceSpot(soldier, enemy, 4f, out var spot), Is.True,
                "An attacking soldier found nowhere closer to push up to in the open.");

            var xformSys = server.System<SharedTransformSystem>();
            var enemyPos = xformSys.GetWorldPosition(enemy);
            var from = (xformSys.GetWorldPosition(soldier) - enemyPos).Length();
            var to = (xformSys.ToMapCoordinates(spot!.Value).Position - enemyPos).Length();

            Assert.That(from - to, Is.GreaterThanOrEqualTo(2f), "The first bound barely gets it any closer.");
        });

        var xform = server.System<SharedTransformSystem>();
        var closest = float.MaxValue;

        for (var second = 0; second < 12 && closest > 5f; second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                var distance = (xform.GetWorldPosition(soldier) - xform.GetWorldPosition(enemy)).Length();
                closest = MathF.Min(closest, distance);
            });
        }

        Assert.That(closest, Is.LessThanOrEqualTo(5f),
            $"The attacking soldier never closed in (got within {closest:F1} of a target 12 tiles off).");

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A soldier going after an enemy in the next room opens the airlock between them and goes through it -
    /// rather than trying to beat its way through the wall beside it, which the pathfinder used to send it at
    /// because it didn't count the soldier as able to work doors.
    /// </summary>
    [Test]
    public async Task SoldierGoesThroughAirlockNotWall()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default, shooter = default, airlock = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            GiveGravity(entMan, map.Grid);

            for (var x = -4; x <= 16; x++)
            {
                for (var y = -4; y <= 4; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }
            }

            // A wall across the middle, an airlock in it.
            for (var y = -4; y <= 4; y++)
            {
                var uid = entMan.SpawnEntity(y == 0 ? "Airlock" : "WallSolid",
                    new EntityCoordinates(map.Grid, new Vector2(6.5f, y + 0.5f)));

                if (y != 0)
                    continue;

                airlock = uid;
                // Nothing is wired up on a test grid, and an unpowered airlock opens for nobody.
                entMan.GetComponent<ApcPowerReceiverComponent>(uid).NeedsPower = false;
            }

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f)));
            shooter = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(12.5f, 2.5f)));
            server.System<NPCSystem>().SleepNPC(shooter);
        });
        await server.WaitRunTicks(server.Timing.TickRate);

        // Shot at from the far side, so it goes to look.
        await server.WaitPost(() =>
        {
            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Piercing"), 5);
            server.System<DamageableSystem>().TryChangeDamage(soldier, damage, true, origin: shooter);
        });

        var xform = server.System<SharedTransformSystem>();
        var doorOpened = false;
        var reached = false;

        for (var second = 0; second < 10 && !reached; second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                doorOpened |= server.EntMan.GetComponent<DoorComponent>(airlock).State is DoorState.Opening or DoorState.Open;

                // Through into the other room, or firing at the shooter through the open airlock - either way
                // the door did its job, and the soldier isn't stuck against the wall.
                var crossed = xform.GetWorldPosition(soldier).X > 7f;
                var engaged = server.EntMan.TryGetComponent<NPCRangedCombatComponent>(soldier, out var ranged) &&
                              ranged.Target == shooter && ranged.TargetInLOS;
                reached = crossed || engaged;
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(doorOpened, Is.True, "The soldier never opened the airlock.");
            Assert.That(reached, Is.True, "The soldier never got through the airlock to its enemy.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Left to its own HTN, a squad soldier told to defend with an enemy in sight digs in: puts up a barricade
    /// on the side facing the enemy, out of its own steel, and then fights from behind it.
    /// </summary>
    [Test]
    public async Task DefendingSoldierBuildsBarricadeByItself()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default, leader = default, enemy = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            LayFloor(server, map, -6, 14, -5, 5);

            soldier = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            leader = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(-2.5f, 0.5f)), "DSM");
            entMan.RemoveComponent<BarotraumaComponent>(leader);
            enemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(9.5f, 0.5f)));

            // Only there to be dug in against.
            server.System<NPCSystem>().SleepNPC(enemy);
            server.System<GodmodeSystem>().EnableGodmode(enemy);
        });
        await server.WaitRunTicks(5);

        var steelBefore = 0;

        await server.WaitAssertion(() =>
        {
            var squad = server.System<NpcSquadSystem>();
            Assert.That(squad.TryRecruit(leader, soldier), Is.True);
            squad.SetOrder(soldier, server.EntMan.GetComponent<NpcSquadMemberComponent>(soldier), NpcSquadOrder.Defend);
            steelBefore = server.System<NpcTacticalSystem>().CountMaterial(soldier, "Steel");
        });

        EntityUid? barricade = null;
        var ammoWhenBuilt = -1;
        var fired = false;

        for (var second = 0; second < 20 && !fired; second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                var entMan = server.EntMan;
                barricade ??= FindBarricade(entMan, soldier, 3f);

                if (barricade == null || !server.System<GunSystem>().TryGetGun(soldier, out var gun, out _))
                    return;

                var ammo = AmmoIn(entMan, gun);
                if (ammoWhenBuilt < 0)
                    ammoWhenBuilt = ammo;
                else if (ammo != ammoWhenBuilt)
                    fired = true;
            });
        }

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var xform = server.System<SharedTransformSystem>();

            Assert.That(barricade, Is.Not.Null, "The defending soldier never put a barricade up.");

            var facing = xform.GetWorldRotation(barricade!.Value).ToWorldVec();
            var toEnemy = Vector2.Normalize(xform.GetWorldPosition(enemy) - xform.GetWorldPosition(barricade.Value));

            Assert.Multiple(() =>
            {
                Assert.That(Vector2.Dot(facing, toEnemy), Is.GreaterThan(0.5f), "The barricade doesn't face the enemy.");
                Assert.That(server.System<NpcTacticalSystem>().CountMaterial(soldier, "Steel"), Is.LessThan(steelBefore),
                    "The barricade didn't come out of the soldier's steel.");
                Assert.That(fired, Is.True, "The soldier never fired from behind its barricade.");
            });
        });

        // Having dug in, it stays dug in: it doesn't wander off to some other spot a few seconds later.
        var furthest = 0f;

        for (var second = 0; second < 12; second++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                var xform = server.System<SharedTransformSystem>();
                var distance = (xform.GetWorldPosition(soldier) - xform.GetWorldPosition(barricade!.Value)).Length();
                furthest = MathF.Max(furthest, distance);
            });
        }

        Assert.That(furthest, Is.LessThan(2.5f),
            $"The soldier left its barricade behind (got {furthest:F1} away from it).");

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Left to their own HTNs, when the squad leader goes down one soldier goes to patch them up and the other
    /// throws up a barricade by them, facing the enemy.
    /// </summary>
    [Test]
    public async Task SquadFortifiesAroundWoundedLeaderByItself()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid first = default, second = default, leader = default, enemy = default;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            LayFloor(server, map, -6, 14, -5, 5);

            leader = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)), "DSM");
            entMan.RemoveComponent<BarotraumaComponent>(leader);
            first = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(-1.5f, 2.5f)));
            second = entMan.SpawnEntity(DsmSoldier, new EntityCoordinates(map.Grid, new Vector2(-1.5f, -1.5f)));
            enemy = entMan.SpawnEntity(NcwlSoldier, new EntityCoordinates(map.Grid, new Vector2(9.5f, 0.5f)));

            server.System<NPCSystem>().SleepNPC(enemy);
            server.System<GodmodeSystem>().EnableGodmode(enemy);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var squad = server.System<NpcSquadSystem>();
            Assert.That(squad.TryRecruit(leader, first), Is.True);
            Assert.That(squad.TryRecruit(leader, second), Is.True);

            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Blunt"), 60);
            server.System<DamageableSystem>().TryChangeDamage(leader, damage, true);
        });

        EntityUid? barricade = null;
        var tended = false;

        for (var s = 0; s < 20 && (barricade == null || !tended); s++)
        {
            await server.WaitRunTicks(server.Timing.TickRate);

            await server.WaitPost(() =>
            {
                barricade ??= FindBarricade(server.EntMan, leader, 2.5f);
                tended |= server.System<NpcSquadSystem>().TryGetActiveMedic(leader, out _);
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(tended, Is.True, "Nobody went to patch the leader up.");
            Assert.That(barricade, Is.Not.Null, "Nobody put a barricade up by the wounded leader.");
        });

        await pair.CleanReturnAsync();
    }

    private static void LayFloor(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server, Pair.TestMapData map, int x0, int x1, int y0, int y1)
    {
        var mapSys = server.System<SharedMapSystem>();
        GiveGravity(server.EntMan, map.Grid);

        for (var x = x0; x <= x1; x++)
        {
            for (var y = y0; y <= y1; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
            }
        }
    }

    private static EntityUid? FindBarricade(IEntityManager entMan, EntityUid near, float range)
    {
        var xform = entMan.System<SharedTransformSystem>();
        var pos = xform.GetMapCoordinates(near);
        var query = entMan.EntityQueryEnumerator<DirectionalBarricadeComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var barricadeXform))
        {
            var at = xform.GetMapCoordinates(uid, barricadeXform);
            if (at.MapId == pos.MapId && (at.Position - pos.Position).Length() <= range)
                return uid;
        }

        return null;
    }

    private static int AmmoIn(IEntityManager entMan, EntityUid gun)
    {
        var ev = new Content.Shared.Weapons.Ranged.Events.GetAmmoCountEvent();
        entMan.EventBus.RaiseLocalEvent(gun, ref ev);
        return ev.Count;
    }

    /// <summary>
    /// Gives the test grid gravity, like a ship with its generator on. Without it everyone is weightless and
    /// slides on with nothing to stop them, which says nothing about how they move on a real ship.
    /// </summary>
    private static void GiveGravity(IEntityManager entMan, EntityUid grid)
    {
        var gravity = entMan.EnsureComponent<GravityComponent>(grid);
        entMan.System<GravitySystem>().EnableGravity(grid, gravity);
        gravity.Inherent = true;
    }

    private static EntityUid SpawnPlayer(IEntityManager entMan, EntityCoordinates coords, string faction)
    {
        var player = entMan.SpawnEntity("MobHuman", coords);
        entMan.EnsureComponent<HullrotFactionComponent>(player).Faction = faction;
        return player;
    }

    private static EntityUid SpawnPlayer(IEntityManager entMan, MapCoordinates coords, string faction)
    {
        var player = entMan.SpawnEntity("MobHuman", coords);
        entMan.EnsureComponent<HullrotFactionComponent>(player).Faction = faction;
        return player;
    }
}
