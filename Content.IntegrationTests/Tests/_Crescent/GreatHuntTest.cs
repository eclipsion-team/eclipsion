#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server._Crescent.GreatHunt;
using Content.Shared.GameTicking.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Starts a real Great Hunt round and walks it through its phases: both home bases walled in while the altar sleeps,
/// the walls falling when the preparation phase runs out or an admin skips it, and the hold on the altar winning.
/// </summary>
[TestFixture]
[TestOf(typeof(GreatHuntRuleSystem))]
public sealed class GreatHuntTest
{
    private const string Preset = "GreatHunt";

    /// <summary>A 32x32 ship, about as big as anything bought during preparation.</summary>
    private const string ShipPath = "/Maps/_Crescent/Shuttles/TAP/caravan.yml";

    [Test]
    public async Task PreparationEndsOnItsOwn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true,
            Dirty = true,
        });
        var server = pair.Server;
        var timing = server.ResolveDependency<IGameTiming>();
        var transform = server.System<SharedTransformSystem>();
        var shuttles = server.System<ShuttleSystem>();
        var mapLoader = server.System<MapLoaderSystem>();

        var hunt = await StartHunt(pair);

        // A ship that finds no free dock is set down beside the base by FTL proximity. It has to land inside the wall,
        // even with earlier ships already parked there pushing it further out.
        await server.WaitAssertion(() =>
        {
            foreach (var (baseUid, baseBox) in Bases(server.EntMan, transform, hunt))
            {
                var inside = baseBox.Enlarged(hunt.BarrierMargin);
                var map = server.EntMan.GetComponent<TransformComponent>(baseUid).MapID;

                for (var i = 0; i < 6; i++)
                {
                    Assert.That(mapLoader.TryLoadGrid(map, new ResPath(ShipPath), out var ship, offset: new Vector2(0f, 20000f + i * 100f)),
                        Is.True, $"Failed to load {ShipPath}.");
                    Assert.That(shuttles.TryFTLProximity(ship!.Value.Owner, baseUid), Is.True);

                    var shipBox = WorldBox(server.EntMan, transform, ship.Value.Owner);
                    Assert.That(inside.Contains(shipBox.BottomLeft) && inside.Contains(shipBox.TopRight), Is.True,
                        $"A ship placed beside {server.EntMan.ToPrettyString(baseUid)} landed at {shipBox}, outside its wall at {inside}.");
                }
            }
        });

        // Wind the clock down to a few seconds rather than sitting through the real ten minutes.
        await server.WaitPost(() => hunt.GraceEndsAt = timing.CurTime + TimeSpan.FromSeconds(2));
        await pair.RunTicksSync(timing.TickRate * 3);

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hunt.GraceOver, Is.True, "The preparation phase did not end when its time ran out.");
                Assert.That(server.EntMan.Count<GreatHuntGraceBarrierComponent>(), Is.Zero,
                    "The preparation walls are still standing after the phase ended.");
                Assert.That(Altars(server.EntMan).All(a => a.UnlockTime <= timing.CurTime), Is.True,
                    "The altar is still asleep after the preparation phase ended.");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SkipGraceThenHoldWins()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true,
            Dirty = true,
        });
        var server = pair.Server;
        var timing = server.ResolveDependency<IGameTiming>();
        var ticker = server.System<GameTicker>();

        var hunt = await StartHunt(pair);

        await server.WaitPost(() => server.ConsoleHost.ExecuteCommand("greathunt_skipgrace"));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hunt.GraceOver, Is.True, "greathunt_skipgrace did not end the preparation phase.");
                Assert.That(server.EntMan.Count<GreatHuntGraceBarrierComponent>(), Is.Zero,
                    "greathunt_skipgrace left the preparation walls standing.");
                Assert.That(Altars(server.EntMan).All(a => a.UnlockTime <= timing.CurTime), Is.True,
                    "greathunt_skipgrace left the altar asleep.");
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            }

            // Hand the altar to the SRM with the full hold already behind them.
            var altar = Altars(server.EntMan).Single();
            altar.HolderFaction = "SRM";
            altar.HeldSince = timing.CurTime - hunt.HoldTime - TimeSpan.FromSeconds(1);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hunt.Decided, Is.True, "Holding the altar for the full hold time did not decide the hunt.");
                Assert.That(hunt.Winner, Is.EqualTo("SRM"));
                Assert.That(Altars(server.EntMan).Single().Locked, Is.True, "The altar was not frozen after the win.");
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PostRound), "The win did not end the round.");
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Starts a Great Hunt round from the lobby and checks the preparation phase came up the way it should: running,
    /// each home base closed in by a full ring of wall, and the altar asleep until the walls fall.
    /// </summary>
    private static async Task<GreatHuntRuleComponent> StartHunt(TestPair pair)
    {
        var server = pair.Server;
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var ticker = server.System<GameTicker>();
        var transform = server.System<SharedTransformSystem>();

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset(Preset);
            ticker.StartRound(force: true);
        });

        // The walls go up on the rule's first tick; the altar resolves from its spawn point the update after map init.
        await pair.RunTicksSync(5);

        GreatHuntRuleComponent? hunt = null;

        await server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound), "The Great Hunt round failed to start.");

            var hunts = new List<GreatHuntRuleComponent>();
            var query = entMan.EntityQueryEnumerator<ActiveGameRuleComponent, GreatHuntRuleComponent>();
            while (query.MoveNext(out _, out var rule))
            {
                hunts.Add(rule);
            }

            Assert.That(hunts, Has.Count.EqualTo(1), "Expected exactly one running Great Hunt rule.");
            hunt = hunts[0];

            var strips = new List<(EntityUid Uid, Box2 Box)>();
            var barriers = entMan.EntityQueryEnumerator<GreatHuntGraceBarrierComponent, MapGridComponent>();
            while (barriers.MoveNext(out var barrier, out _, out _))
            {
                strips.Add((barrier, WorldBox(entMan, transform, barrier)));
            }

            var bases = Bases(entMan, transform, hunt);
            var altars = Altars(entMan);
            var left = hunt.GraceEndsAt - timing.CurTime;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hunt.GraceOver, Is.False, "The round started outside its preparation phase.");
                Assert.That(left, Is.InRange(hunt.GracePeriod - TimeSpan.FromSeconds(5), hunt.GracePeriod),
                    "The preparation phase is not counting down from its configured length.");

                Assert.That(bases.Select(b => entMan.GetComponent<BecomesStationComponent>(b.Uid).Id),
                    Is.EquivalentTo(hunt.BarrierStations), "Not every home base to wall in was loaded.");
                Assert.That(strips, Has.Count.EqualTo(bases.Count * 4), "Expected four wall strips per home base.");

                foreach (var (baseUid, baseBox) in bases)
                {
                    var name = entMan.ToPrettyString(baseUid);
                    var ring = strips.Where(s => s.Box.Intersects(baseBox.Enlarged(hunt.BarrierMargin + 2))).ToList();
                    Assert.That(ring, Has.Count.EqualTo(4), $"{name} is not ringed by exactly four wall strips.");

                    // The ring is whole if its strips cover the full outline: top and bottom across, sides between.
                    var outline = ring.Aggregate(ring[0].Box, (box, strip) => box.Union(strip.Box));
                    Assert.That(outline.Contains(baseBox.Enlarged(hunt.BarrierMargin).BottomLeft) &&
                                outline.Contains(baseBox.Enlarged(hunt.BarrierMargin).TopRight), Is.True,
                        $"The wall around {name} does not enclose the base with its margin.");
                    Assert.That(ring.Sum(s => s.Box.Width * s.Box.Height),
                        Is.EqualTo(2 * outline.Width + 2 * outline.Height - 4).Within(0.01f),
                        $"The wall around {name} has gaps or overlaps.");

                    foreach (var (strip, _) in ring)
                    {
                        var grid = entMan.GetComponent<MapGridComponent>(strip);
                        var tiles = server.System<SharedMapSystem>().GetAllTiles(strip, grid).Count();
                        var walls = 0;
                        var children = entMan.GetComponent<TransformComponent>(strip).ChildEnumerator;
                        while (children.MoveNext(out var child))
                        {
                            if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == hunt.BarrierWall.Id &&
                                entMan.GetComponent<TransformComponent>(child).Anchored)
                                walls++;
                        }

                        Assert.That(walls, Is.EqualTo(tiles), $"A wall strip around {name} is missing anchored walls.");
                    }
                }

                Assert.That(altars, Has.Count.EqualTo(1), "Expected exactly one Great Altar in the round.");
                Assert.That(altars.All(a => a.UnlockTime >= hunt.GraceEndsAt), Is.True,
                    "The altar can be claimed during the preparation phase.");
            }
        });

        return hunt!;
    }

    private static List<(EntityUid Uid, Box2 Box)> Bases(IEntityManager entMan, SharedTransformSystem transform,
        GreatHuntRuleComponent hunt)
    {
        var bases = new List<(EntityUid, Box2)>();
        var query = entMan.EntityQueryEnumerator<BecomesStationComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var becomes, out _))
        {
            if (hunt.BarrierStations.Contains(becomes.Id))
                bases.Add((uid, WorldBox(entMan, transform, uid)));
        }

        return bases;
    }

    private static Box2 WorldBox(IEntityManager entMan, SharedTransformSystem transform, EntityUid grid)
    {
        return transform.GetWorldMatrix(grid).TransformBox(entMan.GetComponent<MapGridComponent>(grid).LocalAABB);
    }

    private static List<GreatAltarComponent> Altars(IEntityManager entMan)
    {
        var altars = new List<GreatAltarComponent>();
        var query = entMan.EntityQueryEnumerator<GreatAltarComponent>();
        while (query.MoveNext(out _, out var altar))
        {
            altars.Add(altar);
        }

        return altars;
    }
}
