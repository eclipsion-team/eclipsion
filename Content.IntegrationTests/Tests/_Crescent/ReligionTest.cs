using System.Collections.Generic;
using System.Linq;
using Content.Server._Crescent.Religion;
using Content.Server._Crescent.Religion.Components;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Religion;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(ReligionSystem))]
public sealed class ReligionTest
{
    /// <summary>
    ///     The faction rules from the religion update. Unaffiliated and the Returning Tide are open to everyone.
    /// </summary>
    private static readonly Dictionary<string, string[]> ExpectedByFaction = new()
    {
        ["DSM"] = ["FourSaints"],
        ["NCWL"] = ["CommonHands"],
        ["SHI"] = ["GoldenLedger", "FourSaints"],
        ["TFSC"] = ["InfernalExchange", "FourSaints"],
        ["TAP"] = ["VeiledMother"],
        ["SRM"] = ["UnbrokenPattern"],
        ["CMM"] = ["LastWatch"],
    };

    private static readonly string[] OpenToAll = ["Unaffiliated", "ReturningTide"];

    [Test]
    public async Task FactionsGetTheirFaiths()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var religions = protoMan.EnumeratePrototypes<ReligionPrototype>().ToList();

            Assert.Multiple(() =>
            {
                Assert.That(protoMan.HasIndex(ReligionPrototype.Default));

                foreach (var (faction, own) in ExpectedByFaction)
                {
                    var open = religions.Where(r => r.IsOpenTo(faction, null)).Select(r => r.ID);
                    Assert.That(open, Is.EquivalentTo(own.Concat(OpenToAll)), $"Faiths open to {faction}");
                }

                // Spacers may hold anything, in round through IND and in the editor through their job.
                foreach (var religion in religions)
                {
                    Assert.That(religion.IsOpenTo("IND", null), $"{religion.ID} closed to IND");
                    Assert.That(religion.IsOpenTo("DSM", "Spacer"), $"{religion.ID} closed to a DSM spacer");
                    Assert.That(religion.IsOpenTo("NCWL", "Vagrant"), $"{religion.ID} closed to an NCWL vagrant");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AltarsAndRitesResolve()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var loc = server.ResolveDependency<ILocalizationManager>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                var altarFaiths = new HashSet<string>();
                foreach (var proto in protoMan.EnumeratePrototypes<EntityPrototype>())
                {
                    if (proto.Abstract || !proto.TryGetComponent<ReligionAltarComponent>(out var altar, factory))
                        continue;

                    Assert.That(protoMan.HasIndex(altar.Religion), $"{proto.ID} points at unknown faith {altar.Religion}");
                    altarFaiths.Add(altar.Religion);
                }

                foreach (var religion in protoMan.EnumeratePrototypes<ReligionPrototype>())
                {
                    if (religion.ID != ReligionPrototype.Default)
                        Assert.That(altarFaiths, Does.Contain(religion.ID), $"{religion.ID} has no altar to join it at");

                    Assert.That(loc.HasString(religion.Name), $"{religion.ID} name");
                    Assert.That(loc.HasString(religion.Description), $"{religion.ID} description");
                    Assert.That(loc.HasString(religion.JoinVerb), $"{religion.ID} join verb");

                    foreach (var line in religion.Rite)
                        Assert.That(loc.HasString(line.Line), $"{religion.ID} rite line {line.Line}");

                    foreach (var line in religion.CallLines)
                        Assert.That(loc.HasString(line), $"{religion.ID} call line {line}");

                    foreach (var line in religion.ResponseLines)
                        Assert.That(loc.HasString(line), $"{religion.ID} response line {line}");

                    foreach (var line in religion.PrayerLines)
                        Assert.That(loc.HasString(line), $"{religion.ID} prayer line {line}");

                    // Believers touching their altar should always have something to whisper.
                    if (religion.ID != ReligionPrototype.Default)
                        Assert.That(religion.PrayerLines, Is.Not.Empty, $"{religion.ID} has an altar but no prayers");

                    // A call nobody can answer would make every listener look like an unbeliever.
                    if (religion.CallLines.Count > 0)
                        Assert.That(religion.ResponseLines, Is.Not.Empty, $"{religion.ID} has calls but no responses");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Command roles lead their faction's faith. Not leading any faith would leave them silent through their own
    ///     faction's religious calls.
    /// </summary>
    [Test]
    public async Task CommandRolesHoldTheirFactionFaith()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        var expected = new Dictionary<string, string>
        {
            ["GovernorDSM"] = "FourSaints",
            ["AdvocatusDSM"] = "FourSaints",
            ["CommandantNCWL"] = "CommonHands",
            ["MVDOfficerNCWL"] = "CommonHands",
            ["ExecutiveSHI"] = "GoldenLedger",
            ["RingleaderTFSC"] = "InfernalExchange",
            ["ProphetTAP"] = "VeiledMother",
        };

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var job in protoMan.EnumeratePrototypes<JobPrototype>())
                {
                    if (job.RequiredReligion is { } required)
                        Assert.That(protoMan.HasIndex(required), $"{job.ID} requires unknown faith {required}");
                }

                foreach (var (job, religion) in expected)
                {
                    Assert.That(protoMan.Index<JobPrototype>(job).RequiredReligion?.Id, Is.EqualTo(religion), job);
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     A Binding ignores faction and puts the victim in the Prophet's Host. While bound they cannot leave the
    ///     Covenant, and losing the Prophet sets them free.
    /// </summary>
    [Test]
    public async Task BindingHoldsUntilTheProphetIsGone()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var religion = entMan.System<ReligionSystem>();

            var prophet = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.AddComponent<VeiledProphetComponent>(prophet);

            var victim = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.EnsureComponent<HullrotFactionComponent>(victim).Faction = "DSM";
            religion.SetReligion(victim, "FourSaints");

            Assert.Multiple(() =>
            {
                Assert.That(religion.CanHold(victim, protoMan.Index<ReligionPrototype>("FourSaints")));
                Assert.That(religion.CanHold(victim, protoMan.Index<ReligionPrototype>("CommonHands")), Is.False);
                Assert.That(religion.CanHold(victim, protoMan.Index<ReligionPrototype>("VeiledMother")), Is.False);
            });

            religion.SetReligion(victim, "VeiledMother", prophet, forced: true);

            Assert.Multiple(() =>
            {
                Assert.That(religion.HoldsReligion(victim, "VeiledMother"));
                Assert.That(entMan.TryGetComponent<VeiledHostMemberComponent>(victim, out var member));
                Assert.That(member!.Bound);
                Assert.That(entMan.GetComponent<VeiledProphetComponent>(prophet).Host, Does.Contain(victim));
                Assert.That(religion.CanChangeReligion(victim, "FourSaints", out _), Is.False);
            });

            entMan.RemoveComponent<VeiledProphetComponent>(prophet);

            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<VeiledHostMemberComponent>(victim), Is.False);
                Assert.That(religion.CanChangeReligion(victim, "FourSaints", out _));
                // Released, not deconverted.
                Assert.That(religion.HoldsReligion(victim, "VeiledMother"));
            });

            entMan.DeleteEntity(prophet);
            entMan.DeleteEntity(victim);
        });

        await pair.CleanReturnAsync();
    }
}
