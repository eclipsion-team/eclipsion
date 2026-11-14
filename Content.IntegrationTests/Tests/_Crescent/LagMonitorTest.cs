using System.Diagnostics;
using System.Linq;
using System.Threading;
using Content.Server._Crescent.LagMonitor;
using Content.Shared._Crescent.LagMonitor;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class LagMonitorTest
{
    [Test]
    public async Task ShowsSystemTimesAndFreezes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var monitor = server.ResolveDependency<LagMonitorManager>();

        // samples are taken every 5 s of real time, the first one is only a baseline
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(11))
        {
            await server.WaitRunTicks(15);
        }

        await server.WaitPost(() => Thread.Sleep(400));
        await server.WaitRunTicks(2);

        LagMonitorEuiState state = default!;
        var report = string.Empty;
        await server.WaitPost(() =>
        {
            state = monitor.BuildState();
            report = monitor.BuildReport();
        });

        Assert.That(state.Enabled, Is.True);
        Assert.That(state.Current, Is.Not.Null);
        Assert.That(state.Current!.Systems, Is.Not.Empty, "per system times aren't being read");
        Assert.That(state.Current.Parts.Any(p => p.Name == "EntitySystems"), Is.True);
        Assert.That(state.Current.TickRate, Is.GreaterThan(0));
        Assert.That(state.Findings, Is.Not.Empty);
        Assert.That(state.Hitches.Any(h => h.Ms >= 400), Is.True, "the stalled tick wasn't saved as a freeze");
        Assert.That(report, Does.Contain("Heaviest systems"));

        // it reads a few hundred numbers per tick; a full tick is 33 ms
        TestContext.Out.WriteLine($"lag monitor: {state.Current.MonitorMs:0.0000} ms per tick, server tick {state.Current.TickMs:0.00} ms");
        Assert.That(state.Current.MonitorMs, Is.LessThan(0.5f));

        await pair.CleanReturnAsync();
    }
}
