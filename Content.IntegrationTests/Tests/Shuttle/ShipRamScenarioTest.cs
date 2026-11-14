#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Systems;
using Prometheus;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.Shuttle;

// real mapped ships and stations rammed into each other, prints how long the ticks took
[TestFixture]
[TestOf(typeof(ShuttleSystem))]
public sealed class ShipRamScenarioTest
{
    private const string Clementine = "/Maps/_Crescent/Unionfall/unionfall_clementine.yml";
    private const string Nemesis = "/Maps/_Crescent/Unionfall/unionfall_nemesis.yml";
    private const string Suzerain = "/Maps/_Crescent/Shuttles/DSM/suzerain.yml";
    private const string Balreska = "/Maps/_Crescent/Stations/nova_balreska.yml";
    private const string Aurora = "/Maps/_Crescent/Stations/aurora.yml";

    private const int Ticks = 150;

    // same as the engine's, so this returns the engine's instance
    private static readonly Histogram SystemsHist = Metrics.CreateHistogram("robust_entity_systems_update_usage",
        "Amount of time spent processing each entity system", new HistogramConfiguration
        {
            LabelNames = new[] { "system" },
            Buckets = Histogram.ExponentialBuckets(0.000_001, 1.5, 25)
        });

    public enum RamKind
    {
        HeadOn,   // both flying straight at each other
        Glancing, // both flying, hulls only overlap a quarter
        Rammed,   // one sits still, the other flies into it
    }

    [Test]
    [TestCase(RamKind.HeadOn, 40f)]
    [TestCase(RamKind.HeadOn, 60f)]
    [TestCase(RamKind.HeadOn, 80f)]
    [TestCase(RamKind.Glancing, 40f)]
    [TestCase(RamKind.Glancing, 60f)]
    [TestCase(RamKind.Glancing, 80f)]
    [TestCase(RamKind.Rammed, 40f)]
    [TestCase(RamKind.Rammed, 60f)]
    [TestCase(RamKind.Rammed, 80f)]
    public async Task ClementineNemesis(RamKind kind, float speed)
    {
        await Ram(Clementine, Nemesis, false, kind, speed);
    }

    [Test]
    [TestCase(Balreska, 40f)]
    [TestCase(Balreska, 60f)]
    [TestCase(Balreska, 80f)]
    [TestCase(Aurora, 40f)]
    [TestCase(Aurora, 60f)]
    [TestCase(Aurora, 80f)]
    public async Task SuzerainIntoStation(string station, float speed)
    {
        await Ram(Suzerain, station, true, RamKind.Rammed, speed);
    }

    private static async Task Ram(string rammerPath, string targetPath, bool station, RamKind kind, float speed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var loader = entMan.System<MapLoaderSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();

        EntityUid rammer = default;
        EntityUid target = default;
        var targetStart = Vector2.Zero;

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            Assert.That(loader.TryLoadGrid(map.MapId, new ResPath(targetPath), out var t), $"couldn't load {targetPath}");
            Assert.That(loader.TryLoadGrid(map.MapId, new ResPath(rammerPath), out var r), $"couldn't load {rammerPath}");
            target = t!.Value;
            rammer = r!.Value;

            var tBox = WorldBox(entMan, xforms, target);
            var rBox = WorldBox(entMan, xforms, rammer);
            var y = tBox.Center.Y - rBox.Center.Y;
            if (kind == RamKind.Glancing)
                y += tBox.Height / 2f + rBox.Height / 2f - 0.25f * MathF.Min(tBox.Height, rBox.Height);

            var shift = new Vector2(tBox.Left - rBox.Right - 5f, y);
            xforms.SetWorldPosition(rammer, xforms.GetWorldPosition(rammer) + shift);

            if (station)
                entMan.System<ShuttleSystem>().Disable(target);
            else
                Hold(entMan, physics, target);

            Hold(entMan, physics, rammer);
            targetStart = xforms.GetWorldPosition(target);
        });

        // let the maps settle first so loading isn't counted
        await server.WaitRunTicks(60);

        var startEnergy = 0f;
        var startEntities = 0;
        await server.WaitPost(() =>
        {
            server.ResolveDependency<IEntitySystemManager>().MetricsEnabled = true;
            Launch(entMan, physics, rammer, new Vector2(speed, 0f));
            if (kind != RamKind.Rammed)
                Launch(entMan, physics, target, new Vector2(-speed, 0f));

            startEnergy = KineticEnergy(entMan, map.MapId);
            startEntities = entMan.EntityCount;
        });

        var maxEnergy = startEnergy;
        var last = ReadSystems();
        var ticks = new List<(int Tick, double Ms, string Top)>();
        for (var i = 0; i < Ticks; i++)
        {
            var sw = Stopwatch.StartNew();
            await server.WaitRunTicks(1);
            var ms = sw.Elapsed.TotalMilliseconds;

            var now = ReadSystems();
            var top = string.Join(", ", now
                .Select(kv => (kv.Key, Ms: (kv.Value - last.GetValueOrDefault(kv.Key)) * 1000))
                .OrderByDescending(x => x.Ms)
                .Take(4)
                .Select(x => $"{x.Key} {x.Ms:0}"));
            last = now;
            ticks.Add((i, ms, top));

            await server.WaitPost(() => maxEnergy = MathF.Max(maxEnergy, KineticEnergy(entMan, map.MapId)));
        }

        var grids = 0;
        var endEntities = 0;
        var targetMoved = 0f;
        await server.WaitPost(() =>
        {
            var query = entMan.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (query.MoveNext(out _, out _, out var xform))
            {
                if (xform.MapID == map.MapId)
                    grids++;
            }

            endEntities = entMan.EntityCount;
            if (station && entMan.EntityExists(target))
                targetMoved = (xforms.GetWorldPosition(target) - targetStart).Length();
        });

        var total = ticks.Sum(t => t.Ms);
        var name = $"{Short(rammerPath)} -> {Short(targetPath)} {kind} {speed} m/s";
        TestContext.Out.WriteLine($"{name}: avg {total / Ticks:0.0} ms, max {ticks.Max(t => t.Ms):0} ms, " +
                                  $"ticks over 33 ms: {ticks.Count(t => t.Ms > 33)}/{Ticks}, " +
                                  $"grids after {grids}, entities {startEntities} -> {endEntities}");
        foreach (var t in ticks.OrderByDescending(t => t.Ms).Take(3))
        {
            TestContext.Out.WriteLine($"  tick {t.Tick}: {t.Ms:0} ms ({t.Top})");
        }

        Assert.Multiple(() =>
        {
            Assert.That(maxEnergy, Is.LessThanOrEqualTo(startEnergy * 1.02f), "the ram made kinetic energy out of nothing");
            if (station)
                Assert.That(targetMoved, Is.LessThan(0.01f), "the station moved");
        });

        await pair.CleanReturnAsync();
    }

    // same rams with a client and two more bodies aboard
    [Test]
    [TestCase(Clementine, Nemesis, false, RamKind.HeadOn, 60f)]
    [TestCase(Clementine, Nemesis, false, RamKind.Rammed, 80f)]
    [TestCase(Suzerain, Aurora, true, RamKind.Rammed, 60f)]
    public async Task WithCrew(string rammerPath, string targetPath, bool station, RamKind kind, float speed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var loader = entMan.System<MapLoaderSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var minds = entMan.System<Content.Server.Mind.MindSystem>();
        var session = pair.Player!;

        EntityUid rammer = default;
        EntityUid target = default;
        var bodies = new List<(string Where, EntityUid Body)>();

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);
            Assert.That(loader.TryLoadGrid(map.MapId, new ResPath(targetPath), out var t), $"couldn't load {targetPath}");
            Assert.That(loader.TryLoadGrid(map.MapId, new ResPath(rammerPath), out var r), $"couldn't load {rammerPath}");
            target = t!.Value;
            rammer = r!.Value;

            var tBox = WorldBox(entMan, xforms, target);
            var rBox = WorldBox(entMan, xforms, rammer);
            var shift = new Vector2(tBox.Left - rBox.Right - 5f, tBox.Center.Y - rBox.Center.Y);
            xforms.SetWorldPosition(rammer, xforms.GetWorldPosition(rammer) + shift);

            if (station)
                entMan.System<ShuttleSystem>().Disable(target);
            else
                Hold(entMan, physics, target);

            Hold(entMan, physics, rammer);

            var rLocal = entMan.GetComponent<MapGridComponent>(rammer).LocalAABB;
            var tLocal = entMan.GetComponent<MapGridComponent>(target).LocalAABB;
            var player = SpawnBody(entMan, rammer, new Vector2(rLocal.Right - 6f, rLocal.Center.Y));
            var crewmate = SpawnBody(entMan, rammer, new Vector2(rLocal.Right - 15f, rLocal.Center.Y + 2f));
            // the station/target side facing the rammer is its left edge
            var other = SpawnBody(entMan, target, new Vector2(tLocal.Left + 6f, tLocal.Center.Y));

            var mind = minds.CreateMind(session.UserId, "RamTestPlayer");
            minds.TransferTo(mind, player, mind: mind.Comp);
            server.PlayerMan.SetAttachedEntity(session, player);
            minds.TransferTo(minds.CreateMind(null, "RamTestCrewmate"), crewmate);
            minds.TransferTo(minds.CreateMind(null, "RamTestOther"), other);

            bodies.Add(("player, rammer bow", player));
            bodies.Add(("crewmate, rammer", crewmate));
            bodies.Add(("other ship/station", other));
        });

        // the client has to get the whole of both maps first
        await pair.RunTicksSync(90);

        await server.WaitPost(() =>
        {
            Assert.That(session.AttachedEntity, Is.EqualTo(bodies[0].Body));
            Launch(entMan, physics, rammer, new Vector2(speed, 0f));
            if (kind != RamKind.Rammed)
                Launch(entMan, physics, target, new Vector2(-speed, 0f));
        });

        var serverMs = new List<double>();
        var clientMs = new List<double>();
        for (var i = 0; i < Ticks; i++)
        {
            var sw = Stopwatch.StartNew();
            await server.WaitRunTicks(1);
            serverMs.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            await client.WaitRunTicks(1);
            clientMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        // let the client catch up on whatever is left
        await pair.RunTicksSync(10);

        var report = new List<string>();
        EntityUid? attached = null;
        await server.WaitPost(() =>
        {
            attached = session.AttachedEntity;
            foreach (var (where, body) in bodies)
            {
                if (!entMan.EntityExists(body))
                {
                    report.Add($"  {where}: gone");
                    continue;
                }

                var state = entMan.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(body).CurrentState;
                var damage = entMan.GetComponent<Content.Shared.Damage.DamageableComponent>(body).TotalDamage;
                var grid = entMan.GetComponent<TransformComponent>(body).GridUid;
                var on = grid == rammer ? "rammer" : grid == target ? "target" : grid == null ? "space" : "a broken off piece";
                report.Add($"  {where}: {state}, {damage} damage, on {on}");
            }
        });

        var clientEntity = EntityUid.Invalid;
        await client.WaitPost(() => clientEntity = client.PlayerMan.LocalEntity ?? EntityUid.Invalid);

        TestContext.Out.WriteLine($"{Short(rammerPath)} -> {Short(targetPath)} {kind} {speed} m/s with crew: " +
                                  $"server avg {serverMs.Average():0.0} ms max {serverMs.Max():0}, over 33 ms {serverMs.Count(m => m > 33)}/{Ticks}; " +
                                  $"client avg {clientMs.Average():0.0} ms max {clientMs.Max():0}, over 33 ms {clientMs.Count(m => m > 33)}/{Ticks}");
        foreach (var line in report)
        {
            TestContext.Out.WriteLine(line);
        }

        TestContext.Out.WriteLine($"  player attached to {(attached == bodies[0].Body ? "their body" : attached == null ? "nothing" : entMan.ToPrettyString(attached.Value))}");

        Assert.Multiple(() =>
        {
            Assert.That(client.PlayerMan.LocalSession, Is.Not.Null, "the client got disconnected");
            Assert.That(clientEntity.IsValid(), "the client has no entity after the ram");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnBody(IEntityManager entMan, EntityUid grid, Vector2 near)
    {
        var maps = entMan.System<SharedMapSystem>();
        var turfs = entMan.System<Content.Shared.Maps.TurfSystem>();
        var gridComp = entMan.GetComponent<MapGridComponent>(grid);

        Vector2i? best = null;
        var bestDist = float.MaxValue;
        var tiles = maps.GetAllTiles(grid, gridComp);
        while (tiles.MoveNext(out var maybeTile))
        {
            if (maybeTile is not { } tile)
                continue;

            var dist = (tile.GridIndices + new Vector2(0.5f, 0.5f) - near).LengthSquared();
            if (dist >= bestDist || turfs.IsTileBlocked(tile, Content.Shared.Physics.CollisionGroup.Impassable))
                continue;

            best = tile.GridIndices;
            bestDist = dist;
        }

        Assert.That(best, Is.Not.Null, "no free tile for a body");
        return entMan.SpawnEntity("MobHuman", maps.GridTileToLocal(grid, gridComp, best!.Value));
    }

    private static Dictionary<string, double> ReadSystems()
    {
        var sums = new Dictionary<string, double>();
        foreach (var labels in SystemsHist.GetAllLabelValues())
        {
            sums[labels[0]] = SystemsHist.WithLabels(labels).Sum;
        }

        return sums;
    }

    private static string Short(string path)
    {
        return path[(path.LastIndexOf('/') + 1)..].Replace(".yml", "");
    }

    private static Box2 WorldBox(IEntityManager entMan, SharedTransformSystem xforms, EntityUid grid)
    {
        return entMan.GetComponent<MapGridComponent>(grid).LocalAABB.Translated(xforms.GetWorldPosition(grid));
    }

    private static void Hold(IEntityManager entMan, SharedPhysicsSystem physics, EntityUid grid)
    {
        var body = entMan.GetComponent<PhysicsComponent>(grid);
        physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        physics.SetLinearDamping(grid, body, 0f);
        physics.SetAngularDamping(grid, body, 0f);
        physics.SetLinearVelocity(grid, Vector2.Zero, body: body);
        physics.SetAngularVelocity(grid, 0f, body: body);
    }

    private static void Launch(IEntityManager entMan, SharedPhysicsSystem physics, EntityUid grid, Vector2 velocity)
    {
        var body = entMan.GetComponent<PhysicsComponent>(grid);
        physics.SetLinearVelocity(grid, velocity, body: body);
        physics.WakeBody(grid, body: body);
    }

    private static float KineticEnergy(IEntityManager entMan, MapId mapId)
    {
        var energy = 0f;
        var query = entMan.AllEntityQueryEnumerator<MapGridComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var body, out var xform))
        {
            if (xform.MapID != mapId || body.BodyType != BodyType.Dynamic)
                continue;

            energy += 0.5f * body.Mass * body.LinearVelocity.LengthSquared()
                      + 0.5f * body.Inertia * body.AngularVelocity * body.AngularVelocity;
        }

        return energy;
    }
}
