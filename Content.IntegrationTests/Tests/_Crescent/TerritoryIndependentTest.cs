using System.Linq;
using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Server._Crescent.Territory;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.CaptureFlag;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Interaction;
using Content.Shared.NPC.Components;
using Content.Shared.Pointing;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class TerritoryIndependentTest
{
    [Test]
    public async Task SpacerHoldsOneTerritoryOfTheirOwn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;

        EntityUid first = default;
        EntityUid second = default;
        EntityUid turret = default;
        EntityUid spacer = default;
        EntityUid rival = default;
        EntityCoordinates secondCoords = default;
        var firstId = $"integration-test-{Guid.NewGuid():N}";
        var secondId = $"integration-test-{Guid.NewGuid():N}";

        await server.WaitAssertion(() =>
        {
            first = Flag(entMan, map.GridCoords, firstId);
            var mapSystem = server.System<SharedMapSystem>();
            var otherGrid = mapSystem.CreateGridEntity(map.MapId);
            server.System<SharedTransformSystem>().SetWorldPosition(otherGrid, new Vector2(50f, 0f));
            mapSystem.SetTile(otherGrid, Vector2i.Zero, map.Tile.Tile);
            secondCoords = new EntityCoordinates(otherGrid, new Vector2(0.5f, 0.5f));
            second = Flag(entMan, secondCoords, secondId);
            turret = entMan.SpawnEntity("WeaponTurretAutoPDCapturable", map.GridCoords);

            spacer = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.EnsureComponent<HullrotFactionComponent>(spacer).Faction = PersistentTerritoryFactions.IndependentFaction;
            server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), spacer);

            rival = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.EnsureComponent<HullrotFactionComponent>(rival).Faction = PersistentTerritoryFactions.IndependentFaction;
            entMan.EnsureComponent<TerritoryIndependentComponent>(rival).Owner = "IND9999";
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => entMan.EventBus.RaiseLocalEvent(first, new InteractHandEvent(spacer, first)));
        await server.WaitRunTicks(10);

        string owner = default!;
        await server.WaitAssertion(() =>
        {
            owner = entMan.GetComponent<CaptureFlagComponent>(first).OwnerTeam!;
            Assert.That(PersistentTerritoryFactions.IsIndependent(owner), Is.True, $"Spacer claimed as '{owner}'.");
            Assert.That(PersistentTerritoryFactions.GetTeam(entMan, spacer), Is.EqualTo(owner));

            var grid = map.Grid.Owner;
            var territory = entMan.GetComponent<PersistentPoiGridComponent>(grid);
            Assert.That(PersistentTerritoryFactions.Holds(entMan, spacer, territory), Is.True);
            Assert.That(PersistentTerritoryFactions.Holds(entMan, rival, territory), Is.False,
                "Another independent counts as the holder.");

            Assert.That(entMan.GetComponent<MetaDataComponent>(grid).EntityName, Does.StartWith($"{owner} "));
            Assert.That(entMan.GetComponent<IFFComponent>(grid).Color,
                Is.EqualTo(PersistentTerritoryFactions.IndependentColor(owner)));

            Assert.That(entMan.GetComponent<NpcFactionMemberComponent>(turret).Factions,
                Does.Contain(TerritoryIndependentSystem.TurretFaction));
            var ignored = entMan.GetComponent<FactionExceptionComponent>(turret).Ignored;
            Assert.That(ignored, Does.Contain(spacer), "The holder's own turret would shoot them.");
            Assert.That(ignored, Does.Not.Contain(rival));

            server.System<SharedTransformSystem>().SetCoordinates(spacer, secondCoords);
            entMan.EventBus.RaiseLocalEvent(second, new InteractHandEvent(spacer, second));
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<CaptureFlagComponent>(second).OwnerTeam, Is.Null,
                "An independent took a second territory.");
            Assert.That(entMan.GetComponent<PersistentCaptureRegionComponent>(second).Capturer, Is.Null);

            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(firstId, null), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => entMan.EventBus.RaiseLocalEvent(second, new InteractHandEvent(spacer, second)));
        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<CaptureFlagComponent>(second).OwnerTeam, Is.EqualTo(owner),
                "An independent who gave their ground up could not claim another, or got a new number.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MercenariesOnlyAttackWhatTheyAreSetOn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var regionId = $"integration-test-{Guid.NewGuid():N}";

        EntityUid flag = default;
        EntityUid spacer = default;
        EntityUid merc = default;
        EntityUid pointed = default;
        EntityUid bystander = default;
        EntityUid attacker = default;

        await server.WaitAssertion(() =>
        {
            flag = Flag(entMan, map.GridCoords, regionId);

            spacer = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.EnsureComponent<HullrotFactionComponent>(spacer).Faction = PersistentTerritoryFactions.IndependentFaction;
            entMan.EnsureComponent<TerritoryIndependentComponent>(spacer).Owner = "IND9998";

            merc = entMan.SpawnEntity("MobSoldierAIMercenaryRifleman", map.GridCoords);
            pointed = Human(entMan, map.GridCoords, "DSM");
            // same faction as the one pointed out: a soldier would turn on all of DSM, a mercenary must not
            bystander = Human(entMan, map.GridCoords, "DSM");
            attacker = Human(entMan, map.GridCoords, "TFSC");
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "IND9998"), Is.True);

            var standard = (flag, entMan.GetComponent<TerritoryGarrisonComponent>(flag));
            var garrison = server.System<TerritoryGarrisonSystem>();
            Assert.That(garrison.GetRoles(standard, "IND9998").Count(), Is.EqualTo(5), "Independents have no mercenaries to hire.");
            Assert.That(garrison.CallIn(standard, TerritoryRecruitKind.Garrison, 2), Is.Zero,
                "An independent was given a garrison.");

            // the Fishbed adds IND, which would turn it on every power's people
            Assert.That(entMan.GetComponent<NpcFactionMemberComponent>(merc).Factions.Select(f => f.Id),
                Is.EquivalentTo(new[] { "Mercenary" }));

            Assert.That(server.System<NpcMercenarySystem>().Hire(spacer, merc), Is.True);

            var squad = server.System<NpcSquadSystem>();
            Assert.That(squad.IsEnemy(merc, bystander), Is.False, "A mercenary goes for people of its own accord.");

            var ev = new AfterPointedAtEvent(pointed);
            entMan.EventBus.RaiseLocalEvent(spacer, ref ev);

            var damage = new DamageSpecifier(server.ProtoMan.Index<DamageTypePrototype>("Blunt"), 5);
            server.System<DamageableSystem>().TryChangeDamage(spacer, damage, origin: attacker);
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var squad = server.System<NpcSquadSystem>();
            Assert.That(squad.IsEnemy(merc, pointed), Is.True, "A mercenary ignored who its hirer pointed out.");
            Assert.That(squad.IsEnemy(merc, attacker), Is.True, "A mercenary let its hirer be attacked.");
            Assert.That(squad.IsEnemy(merc, bystander), Is.False,
                "Pointing someone out set a mercenary on more than that one person.");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid Human(IEntityManager entMan, EntityCoordinates coords, string faction)
    {
        var uid = entMan.SpawnEntity("MobHuman", coords);
        entMan.EnsureComponent<HullrotFactionComponent>(uid).Faction = faction;
        return uid;
    }

    private static EntityUid Flag(IEntityManager entMan, EntityCoordinates coords, string regionId)
    {
        var flag = entMan.CreateEntityUninitialized("PersistentCaptureRegionFlag", coords);
        entMan.GetComponent<PersistentCaptureRegionComponent>(flag).RegionId = regionId;
        entMan.InitializeAndStartEntity(flag);

        var capture = entMan.GetComponent<CaptureFlagComponent>(flag);
        capture.CaptureTime = 0.05f;
        capture.NeutralizeTime = 0.05f;
        return flag;
    }
}
