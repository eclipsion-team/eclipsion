#nullable enable
using Content.Server.Abilities.Psionics;
using Content.Server._Crescent.Psionics;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Crescent.Psionics;
using Content.Shared.Psionics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Saint's Militia hunters are never psionic however psionics reach them, and their leaders' Null Ward cuts nearby
/// psions off for its duration and then lets go.
/// </summary>
[TestFixture]
[TestOf(typeof(PsionHunterSystem))]
public sealed class PsionHunterTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: PsionHunterTestLeader
  parent: MobHuman
  components:
  - type: PsionHunter
  - type: NullWard
    duration: 1
";

    [Test]
    public async Task HuntersLosePsionicsFromAnySource()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        EntityUid hunter = default;

        // Psionic before becoming a hunter, with a power that hands out a component of its own.
        await server.WaitPost(() =>
        {
            hunter = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.System<PsionicAbilitiesSystem>().InitializePsionicPower(hunter, proto.Index<PsionicPowerPrototype>("TelepathyPower"));
            entMan.AddComponent<PsionHunterComponent>(hunter);
        });

        await pair.RunTicksSync(timing.TickRate);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<PsionicComponent>(hunter), Is.False, "A hunter kept the psionics they had.");
            Assert.That(entMan.HasComponent<TelepathyComponent>(hunter), Is.False, "A hunter kept a psionic power.");
        });

        // Psionic after: a trait, an implant or a glimmer event handing them a fresh one.
        await server.WaitPost(() => entMan.AddComponent<PsionicComponent>(hunter));
        await pair.RunTicksSync(timing.TickRate);

        await server.WaitAssertion(() =>
            Assert.That(entMan.HasComponent<PsionicComponent>(hunter), Is.False, "A hunter became psionic."));

        await server.WaitPost(() => entMan.DeleteEntity(hunter));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NullWardCutsOffPsionsUntilItLapses()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();

        EntityUid leader = default;
        EntityUid psion = default;

        await server.WaitPost(() =>
        {
            leader = entMan.SpawnEntity("PsionHunterTestLeader", map.GridCoords);
            psion = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(1f, 0f)));
            entMan.AddComponent<PsionicComponent>(psion);

            entMan.EventBus.RaiseLocalEvent(leader, new NullWardActionEvent { Performer = leader });
        });

        // A few null field scans.
        await pair.RunTicksSync(timing.TickRate / 2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<PsionicNullifierComponent>(leader), Is.True, "The ward put up no field.");
            Assert.That(entMan.HasComponent<PsionicallyNullifiedComponent>(psion), Is.True, "A psion beside the ward kept the noosphere.");
            Assert.That(entMan.HasComponent<PsionicComponent>(leader), Is.False, "The ward made its bearer psionic.");
        });

        // The test ward lasts a second.
        await pair.RunTicksSync(timing.TickRate * 2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<PsionicNullifierComponent>(leader), Is.False, "The ward never lapsed.");
            Assert.That(entMan.HasComponent<PsionicallyNullifiedComponent>(psion), Is.False, "The psion stayed cut off after the ward lapsed.");
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(leader);
            entMan.DeleteEntity(psion);
        });
        await pair.CleanReturnAsync();
    }
}
