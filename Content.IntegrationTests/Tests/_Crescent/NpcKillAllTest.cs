using System.Numerics;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.Factions;
using Content.Server._Crescent.NPC;
using Content.Server._Crescent.NpcSquad;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(NpcSquadSystem))]
public sealed class NpcKillAllTest
{
    private const string DsmSoldier = "MobSoldierAIDSMRifleman";
    private const string GunTargets = "SoldierGunTargets";

    /// <summary>
    /// Under kill-all a soldier goes for anyone neither of its side nor allied - an unaligned civilian included -
    /// but never its own side, nor someone wearing an ally's ID, even of a faction its NPC faction lists as
    /// hostile. With kill-all off the civilian is left be.
    /// </summary>
    [Test]
    public async Task KillAllTargetsEveryoneButOwnSideAndAllies()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var squad = server.System<NpcSquadSystem>();
            var utility = server.System<NPCUtilitySystem>();
            var diplomacy = server.System<RatDiplomacySystem>();

            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            var civilian = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(3f, 0f)));
            var friend = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(-3f, 0f)), "DSM");

            // SHI is on the DSM NPC faction's hostile list; only the worn ID and the alliance can spare them.
            var ally = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(0f, 3f)), "SHI");
            server.System<HullrotNpcFactionSyncSystem>().Sync(ally);
            var id = entMan.SpawnEntity("SHIIDCardEmployee", MapCoordinates.Nullspace);
            Assert.That(server.System<InventorySystem>().TryEquip(ally, id, "id", silent: true, force: true), Is.True);

            var blackboard = entMan.GetComponent<HTNComponent>(soldier).Blackboard;
            var previous = diplomacy.GetRelation("DSM", "SHI");

            try
            {
                diplomacy.SetRelation("DSM", "SHI", FactionRelation.Alliance, persist: false);
                Assert.That(squad.IsKillAll(soldier), Is.True, "Faction soldiers don't start out under kill-all.");

                var targets = utility.GetEntities(blackboard, GunTargets).Entities.Keys;
                Assert.Multiple(() =>
                {
                    Assert.That(targets, Does.Contain(civilian),
                        "Under kill-all the soldier left an unaligned civilian alone.");
                    Assert.That(targets, Does.Not.Contain(friend), "The soldier went for its own side.");
                    Assert.That(targets, Does.Not.Contain(ally), "The soldier went for someone wearing an ally's ID.");
                });

                diplomacy.SetRelation("DSM", "SHI", FactionRelation.Neutral, persist: false);
                Assert.That(utility.GetEntities(blackboard, GunTargets).Entities.Keys, Does.Contain(ally),
                    "The soldier spared a SHI player whose faction is no ally of its own.");

                squad.SetKillAll(soldier, false);
                Assert.That(utility.GetEntities(blackboard, GunTargets).Entities.Keys, Does.Not.Contain(civilian),
                    "With kill-all off the soldier still went for an unaligned civilian.");
            }
            finally
            {
                diplomacy.SetRelation("DSM", "SHI", previous, persist: false);
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A neutral who shoots a soldier gets shot back even with kill-all off. Before, only its own side did, and a
    /// third party nobody was at war with could gun down a garrison that never fired back.
    /// </summary>
    [Test]
    public async Task SoldierFightsBackAgainstNeutral()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var squad = server.System<NpcSquadSystem>();
            var retaliation = server.System<NpcFriendlyFireRetaliationSystem>();

            var soldier = entMan.SpawnEntity(DsmSoldier, map.GridCoords);
            server.System<NPCSystem>().SleepNPC(soldier);
            squad.SetKillAll(soldier, false);

            // TAP has a seat at the diplomacy table but no NPC faction, so nothing makes it a DSM soldier's enemy.
            var neutral = SpawnPlayer(entMan, new EntityCoordinates(map.Grid, new Vector2(2f, 0f)), "TAP");
            server.System<HullrotNpcFactionSyncSystem>().Sync(neutral);
            Assert.That(squad.IsEnemy(soldier, neutral), Is.False);

            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Blunt"), 5);
            server.System<DamageableSystem>().TryChangeDamage(soldier, damage, true, origin: neutral);

            Assert.Multiple(() =>
            {
                Assert.That(retaliation.HasGrudge(soldier, neutral), Is.True,
                    "The soldier let a neutral shoot it without fighting back.");
                Assert.That(squad.IsTargetAllowed(soldier, neutral), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A squad follows its leader's rules of engagement rather than each soldier's own, and a dismissed soldier
    /// goes back to its own.
    /// </summary>
    [Test]
    public async Task SquadRulesOfEngagementStandInForTheSoldiers()
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
            Assert.That(squad.TryRecruit(leader, soldier), Is.True);

            squad.SetSquadKillAll((leader, entMan.GetComponent<NpcSquadLeaderComponent>(leader)), false);
            Assert.That(squad.IsKillAll(soldier), Is.False, "The squad's soldier ignored its leader's rules of engagement.");

            squad.Dismiss(soldier);
            Assert.That(squad.IsKillAll(soldier), Is.True,
                "A dismissed soldier didn't go back to its own rules of engagement.");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnPlayer(IEntityManager entMan, EntityCoordinates coords, string faction)
    {
        var player = entMan.SpawnEntity("MobHuman", coords);
        entMan.EnsureComponent<HullrotFactionComponent>(player).Faction = faction;
        return player;
    }
}
