using System.Collections.Generic;
using Content.Server._Crescent.Factions;
using Content.Server.GameTicking.Events;
using Content.Server.Spawners.Components;
using Content.Server._Crescent.RoundEnd;
using Content.Shared._Crescent.CCVar;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.RoundEnd;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(FactionBalanceSystem))]
public sealed class FactionBalanceTest
{
    [Test]
    public async Task JobCheckRecountsPlayersFromTheCurrentTick()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        var server = pair.Server;
        var map = await pair.CreateTestMap();

        server.CfgMan.SetCVar(RatCCVars.FactionBalanceEnabled, true);
        server.CfgMan.SetCVar(RatCCVars.FactionBalanceAdminBypass, false);
        server.CfgMan.SetCVar(RatCCVars.FactionBalanceBaseSlots, 1);
        server.CfgMan.SetCVar(RatCCVars.FactionBalanceTolerance, 0);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var session = server.PlayerMan.Sessions.Single();
            var player = entMan.SpawnEntity(null, map.GridCoords);
            var membership = entMan.AddComponent<HullrotFactionComponent>(player);
            membership.Faction = "DSM";
            server.PlayerMan.SetAttachedEntity(session, player);

            var job = new ProtoId<JobPrototype>("UnionfallShipCrewDSM");
            var ev = new IsJobAllowedEvent(session, job);
            entMan.EventBus.RaiseEvent(EventSource.Local, ref ev);

            var balance = server.System<FactionBalanceSystem>();
            Assert.Multiple(() =>
            {
                Assert.That(ev.Cancelled, Is.True);
                Assert.That(balance.IsJobBlocked(session, job, out var faction), Is.True);
                Assert.That(faction, Is.EqualTo("DSM"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FallenFactionCannotSpawnWhenPopulationBalanceIsDisabled()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });

        var server = pair.Server;
        var map = await pair.CreateTestMap();

        server.CfgMan.SetCVar(RatCCVars.FactionBalanceEnabled, false);

        await server.WaitAssertion(() =>
        {
            var session = server.PlayerMan.Sessions.Single();
            var fell = new FactionStationFellEvent(map.Grid, "DSM", "Test Station");
            server.EntMan.EventBus.RaiseEvent(EventSource.Local, ref fell);

            var job = new ProtoId<JobPrototype>("UnionfallShipCrewDSM");
            var allowed = new IsJobAllowedEvent(session, job);
            server.EntMan.EventBus.RaiseEvent(EventSource.Local, ref allowed);

            Assert.Multiple(() =>
            {
                Assert.That(allowed.Cancelled, Is.True);
                Assert.That(server.System<FactionBalanceSystem>().IsJobBlocked(session, job, out var faction), Is.True);
                Assert.That(faction, Is.EqualTo("DSM"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FallenFactionLateJoinPointsAreDisabled()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var spawn = server.EntMan.SpawnEntity(null, map.GridCoords);
            var factionSpawn = server.EntMan.AddComponent<FactionLateJoinSpawnPointComponent>(spawn);
            factionSpawn.Faction = new ProtoId<FactionPrototype>("DSM");

            var fell = new FactionStationFellEvent(map.Grid, "DSM", "Test Station");
            server.EntMan.EventBus.RaiseEvent(EventSource.Local, ref fell);

            Assert.Multiple(() =>
            {
                Assert.That(factionSpawn.Enabled, Is.False);
                Assert.That(server.EntMan.HasComponent<StationInfestationComponent>(map.Grid), Is.True);
                Assert.That(server.EntMan.Deleted(map.Grid), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A round run between the two share factions alone has to keep accepting players. Measured against a
    /// share of a population only they can grow, a quarter each never reaches the next whole player, so
    /// both sides jam at their base slots forever.
    /// </summary>
    [Test]
    public async Task ShareFactionsHoldEachOtherLevelWithNoWarFactionInTheRound()
    {
        await using var pair = await PoolManager.GetServerClient();
        var balance = pair.Server.System<FactionBalanceSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            var counts = new Dictionary<string, int> { ["SHI"] = 4, ["TFSC"] = 3 };
            var inPlay = new HashSet<string> { "SHI", "TFSC" };
            var caps = balance.CalculateCaps(counts, baseSlots: 3, tolerance: 0, inPlay);

            Assert.Multiple(() =>
            {
                // ceil((7 + 1) * 0.25 / 0.5): the side that is behind can always take one more.
                Assert.That(caps["SHI"].Cap, Is.EqualTo(4));
                Assert.That(caps["TFSC"].Cap, Is.EqualTo(4));
                Assert.That(caps["TFSC"].Full, Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// With the war factions in the round nothing about the old maths may change: parity between them,
    /// and the support factions still held to their quarter of everyone playing.
    /// </summary>
    [Test]
    public async Task WarFactionsInTheRoundKeepTheParityAndShareSplit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var balance = pair.Server.System<FactionBalanceSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            var counts = new Dictionary<string, int> { ["DSM"] = 6, ["NCWL"] = 5, ["SHI"] = 4, ["TFSC"] = 3 };
            var inPlay = new HashSet<string> { "DSM", "NCWL", "SHI", "TFSC" };
            var caps = balance.CalculateCaps(counts, baseSlots: 3, tolerance: 0, inPlay);

            Assert.Multiple(() =>
            {
                // ceil((11 + 1) * 1 / 2) for the parity pair, floor(18 * 0.25) for the share factions.
                Assert.That(caps["DSM"].Cap, Is.EqualTo(6));
                Assert.That(caps["NCWL"].Cap, Is.EqualTo(6));
                Assert.That(caps["SHI"].Cap, Is.EqualTo(4));
                Assert.That(caps["TFSC"].Cap, Is.EqualTo(4));
                Assert.That(caps["SHI"].Full, Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }
}
