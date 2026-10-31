using Content.Server.Lathe;
using Content.Server.Materials;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Lathe;
using Content.Shared.Power;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests;

/// <summary>
/// Checks that a lathe with batchOutput holds a whole run and hands it over in one piece, rather than
/// dropping an entity per finished item.
/// </summary>
[TestFixture]
public sealed class LatheBatchOutputTest
{
    private const int Sheets = 20;

    private const string Processor = "OreProcessor";
    private const string Recipe = "SheetSteel";
    private const string StackType = "Steel";

    [Test]
    public async Task BatchLatheHandsOverTheWholeRunAtOnce()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var latheSys = entMan.System<LatheSystem>();
        var materialSys = entMan.System<MaterialStorageSystem>();

        var recipe = protoMan.Index<LatheRecipePrototype>(Recipe);
        var processor = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            processor = entMan.SpawnEntity(Processor, map.GridCoords);
            Assert.That(entMan.GetComponent<LatheComponent>(processor).BatchOutput,
                $"{Processor} is not set up for batch output, this test proves nothing");

            // There is no APC out here, so make it read as powered rather than build a grid to feed it.
            // A receiver on no network never reaches the power solver, so it never gets recalculated either
            // and the flag has to be set by hand, same as DisposalUnitTest does.
            var power = entMan.GetComponent<ApcPowerReceiverComponent>(processor);
            entMan.System<PowerReceiverSystem>().SetNeedsPower(processor, false, power);
            power.Powered = true;
        });

        // Let the power state settle, otherwise the lathe is not allowed to start.
        await server.WaitRunTicks(2);
        Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(processor).Powered, "Test setup failed to power the lathe");

        await server.WaitPost(() =>
        {
            foreach (var (material, amount) in recipe.Materials)
            {
                materialSys.TryChangeMaterialAmount(processor, material, amount * Sheets);
            }

            for (var i = 0; i < Sheets; i++)
            {
                Assert.That(latheSys.TryAddToQueue(processor, recipe), $"Could not queue sheet {i + 1}");
            }

            Assert.That(latheSys.TryStartProducing(processor), "Lathe refused to start");
        });

        // Mid-run it should be sitting on everything it has made so far.
        await server.WaitRunTicks(5);
        Assert.That(CountStack(entMan, StackType), Is.Zero, "Batch lathe dropped output before the run was over");

        // One item per tick, plus slack.
        await server.WaitRunTicks(Sheets + 10);

        var lathe = entMan.GetComponent<LatheComponent>(processor);
        Assert.Multiple(() =>
        {
            Assert.That(lathe.Queue, Is.Empty, "Queue never drained");
            Assert.That(lathe.PendingOutput, Is.Empty, "Output was tallied but never handed over");
            Assert.That(CountStack(entMan, StackType), Is.EqualTo(Sheets), "Wrong amount came out of the lathe");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Losing power part way through a run should hand over what the lathe has already made, rather than
    /// hold it until it can finish a queue it may never get to finish.
    /// </summary>
    [Test]
    public async Task LosingPowerHandsOverWhatWasAlreadyMade()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var latheSys = entMan.System<LatheSystem>();
        var materialSys = entMan.System<MaterialStorageSystem>();

        var recipe = protoMan.Index<LatheRecipePrototype>(Recipe);
        var processor = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            processor = entMan.SpawnEntity(Processor, map.GridCoords);
            var power = entMan.GetComponent<ApcPowerReceiverComponent>(processor);
            entMan.System<PowerReceiverSystem>().SetNeedsPower(processor, false, power);
            power.Powered = true;

            foreach (var (material, amount) in recipe.Materials)
            {
                materialSys.TryChangeMaterialAmount(processor, material, amount * Sheets);
            }

            for (var i = 0; i < Sheets; i++)
            {
                latheSys.TryAddToQueue(processor, recipe);
            }

            latheSys.TryStartProducing(processor);
        });

        // Long enough to have made some, nowhere near long enough to finish.
        await server.WaitRunTicks(5);
        Assert.That(CountStack(entMan, StackType), Is.Zero);

        await server.WaitPost(() =>
        {
            var power = entMan.GetComponent<ApcPowerReceiverComponent>(processor);
            power.Powered = false;
            var ev = new PowerChangedEvent(false, 0);
            entMan.EventBus.RaiseLocalEvent(processor, ref ev);
        });

        var lathe = entMan.GetComponent<LatheComponent>(processor);
        Assert.Multiple(() =>
        {
            Assert.That(lathe.PendingOutput, Is.Empty, "Lathe kept a half-finished run after losing power");
            Assert.That(CountStack(entMan, StackType), Is.GreaterThan(0), "Nothing came out of a lathe that had been running");
            Assert.That(lathe.Queue, Is.Not.Empty, "The rest of the order should still be queued");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Total number of sheets lying around, however many entities they are spread over.
    /// </summary>
    private static int CountStack(IEntityManager entMan, string stackType)
    {
        var total = 0;
        var query = entMan.EntityQueryEnumerator<StackComponent>();
        while (query.MoveNext(out _, out var stack))
        {
            if (stack.StackTypeId == stackType)
                total += stack.Count;
        }

        return total;
    }
}
