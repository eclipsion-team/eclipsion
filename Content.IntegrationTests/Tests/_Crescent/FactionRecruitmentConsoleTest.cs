#nullable enable
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Factions;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Inventory;
using Content.Shared.Roles;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.HullrotFaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Drives the two CMM recruitment terminals end to end: the Gliess Sheriff's terminal (Sheriff and Deputy only,
/// sheriff only) and the Analiesse's Minutemen terminal (Minutemen roles only, Minutemen command only).
/// </summary>
[TestFixture]
[TestOf(typeof(FactionRecruitmentConsoleSystem))]
public sealed class FactionRecruitmentConsoleTest
{
    private const string Gliess = "ComputerTabletopFactionRecruitmentGliess";
    private const string Minutemen = "ComputerTabletopFactionRecruitmentCMM";

    private static readonly object[] Cases =
    {
        // The Gliess Sheriff fills the sheriff's office.
        new object[] { Gliess, new[] { "GliessSheriffCommand" }, "GliessianDeputy", true },
        new object[] { Gliess, new[] { "GliessSheriffCommand" }, "GliessianSheriff", true },
        // ...but cannot hand out Minutemen roles from it.
        new object[] { Gliess, new[] { "GliessSheriffCommand" }, "MinutemanCMM", false },
        // Deputies, the dockmaster and Minutemen command cannot run the sheriff's terminal.
        new object[] { Gliess, new[] { "GliessSheriff", "GliessDockmaster", "Minuteman", "MinutemanMarshal", "CMMFunds" }, "GliessianDeputy", false },
        // Minutemen command recruits Minutemen on the Analiesse...
        new object[] { Minutemen, new[] { "MinutemanMarshal" }, "MinutemanCMM", true },
        new object[] { Minutemen, new[] { "MinutemanRankingOfficer" }, "PhysicianCMM", true },
        // ...but no Gliess posts there.
        new object[] { Minutemen, new[] { "MinutemanMarshal" }, "GliessianDeputy", false },
        new object[] { Minutemen, new[] { "MinutemanMarshal" }, "GliessianSheriff", false },
        // The plain Gliess credentials no longer open the Minutemen terminal.
        new object[] { Minutemen, new[] { "GliessSheriff", "GliessDockmaster" }, "MinutemanCMM", false },
    };

    [Test, TestCaseSource(nameof(Cases))]
    public async Task Recruit(string console, string[] operatorAccess, string jobId, bool expectRecruited)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.EntMan;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        EntityUid target = default;
        EntityUid targetId = default;

        await server.WaitPost(() =>
        {
            var inventory = server.System<InventorySystem>();
            var access = server.System<SharedAccessSystem>();

            var terminal = entMan.SpawnEntity(console, map.GridCoords);
            var operatorMob = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(1f, 0f)));
            target = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(0f, 1f)));

            var operatorId = entMan.SpawnEntity("PassengerIDCard", MapCoordinates.Nullspace);
            access.TrySetTags(operatorId, operatorAccess.Select(a => new ProtoId<AccessLevelPrototype>(a)));
            Assert.That(inventory.TryEquip(operatorMob, operatorId, "id", silent: true, force: true), Is.True);

            targetId = entMan.SpawnEntity("PassengerIDCard", MapCoordinates.Nullspace);
            Assert.That(inventory.TryEquip(target, targetId, "id", silent: true, force: true), Is.True);

            var msg = new FactionRecruitmentAssignMessage(entMan.GetNetEntity(target), jobId)
            {
                Actor = operatorMob,
                UiKey = FactionRecruitmentUiKey.Key,
            };
            entMan.EventBus.RaiseLocalEvent(terminal, msg);
        });

        // The assignment runs behind a 2.5 second do-after.
        await pair.RunSeconds(4f);

        await server.WaitAssertion(() =>
        {
            var faction = entMan.GetComponentOrNull<HullrotFactionComponent>(target)?.Faction;
            var title = entMan.GetComponent<IdCardComponent>(targetId).LocalizedJobTitle;
            var job = protoMan.Index<JobPrototype>(jobId);

            if (expectRecruited)
            {
                var tags = entMan.GetComponent<AccessComponent>(targetId).Tags;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(faction, Is.EqualTo("CMM"), $"{console} did not enlist the target as {jobId}.");
                    Assert.That(title, Is.EqualTo(job.LocalizedName), $"{console} did not retitle the target's ID.");
                    Assert.That(job.Access.All(a => tags.Contains(a)), Is.True,
                        $"{console} did not grant {jobId}'s access on the target's ID.");
                }
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(faction, Is.Not.EqualTo("CMM"), $"{console} enlisted the target as {jobId} when it should not.");
                    Assert.That(title, Is.Not.EqualTo(job.LocalizedName), $"{console} retitled the target's ID as {jobId}.");
                }
            }
        });

        await pair.CleanReturnAsync();
    }
}
