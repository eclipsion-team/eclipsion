using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.IntegrationTests.Pair;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Robust.Shared;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Shuttle;

/// <summary>
///     A player's client froze mid-fight (2026-10-10) with its game thread spinning forever inside
///     <c>B2DynamicTree.InsertLeaf</c>, reached from <c>EntityLookupSystem.AddOrUpdatePhysicsTree</c> while a transform
///     state was being applied. InsertLeaf only loops on a tree that already has a cycle in it: a broadphase tree that
///     was corrupted earlier, silently, typically by moving a proxy id in a tree it does not belong to. Players run the
///     official launcher engine, so the trigger has to be found and removed in content.
///     <para/>
///     These tests run with PVS on (test pairs normally have it off) and walk every client broadphase tree after every
///     tick, so corruption fails the test on the tick it happens instead of hanging the client some time later.
/// </summary>
[TestFixture]
public sealed class ClientBroadphaseIntegrityTest
{
    /// <summary>
    ///     Far enough that PVS (25 tile range) drops everything near the origin.
    /// </summary>
    private const float FarAway = 500f;

    /// <summary>
    ///     A healthy client tick takes milliseconds. Anything this long is the InsertLeaf loop.
    /// </summary>
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(30);

    private const string EmitterPrototype = "ShieldEmitter";

    public enum AwayChange
    {
        /// <summary>Pushed off its grid into open space, as a ram does to crew and loose items.</summary>
        MovedIntoSpace,

        /// <summary>Ended up on another grid, as when debris splits off or something is flung across.</summary>
        MovedToAnotherGrid,

        /// <summary>Stayed put but went from dynamic to static (anchoring, embedding) while out of view.</summary>
        BecameStatic,
    }

    /// <summary>
    ///     A physics entity leaves the client's view, changes broadphase or tree while the client cannot see it, and
    ///     comes back. The client must drop its proxies on the way out and build fresh ones on the way in.
    /// </summary>
    [Test]
    [TestCase(AwayChange.MovedIntoSpace)]
    [TestCase(AwayChange.MovedToAnotherGrid)]
    [TestCase(AwayChange.BecameStatic)]
    public async Task ChangedOutOfViewDoesNotCorruptClientBroadphase(AwayChange change)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();

        server.CfgMan.SetCVar(CVars.NetPVS, true);

        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var mapSystem = sEntMan.System<SharedMapSystem>();
        var xformSystem = sEntMan.System<SharedTransformSystem>();
        var physics = sEntMan.System<SharedPhysicsSystem>();
        var fixtures = sEntMan.System<FixtureSystem>();
        var tileId = server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;

        EntityUid homeGrid = default;
        EntityUid farGrid = default;
        EntityUid crate = default;
        EntityUid viewer = default;

        await server.WaitPost(() =>
        {
            sEntMan.DeleteEntity(map.Grid);

            var home = mapSystem.CreateGridEntity(map.MapId);
            homeGrid = home.Owner;
            mapSystem.SetTiles(homeGrid, home.Comp, Block(8, 8, tileId));

            var far = mapSystem.CreateGridEntity(map.MapId);
            farGrid = far.Owner;
            mapSystem.SetTiles(farGrid, far.Comp, Block(8, 8, tileId));
            xformSystem.SetLocalPosition(farGrid, new Vector2(FarAway, 0f));

            crate = sEntMan.SpawnEntity(null, new EntityCoordinates(homeGrid, new Vector2(4f, 4f)));
            var body = sEntMan.AddComponent<PhysicsComponent>(crate);
            var shape = new PolygonShape();
            shape.SetAsBox(0.4f, 0.4f);
            fixtures.TryCreateFixture(crate, shape, "fix1", hard: true, collisionLayer: 1, collisionMask: 1, body: body);
            physics.SetCanCollide(crate, true, body: body);
            physics.SetBodyType(crate, BodyType.Dynamic, body: body);

            viewer = sEntMan.SpawnEntity(null, new EntityCoordinates(homeGrid, new Vector2(2f, 2f)));
            AttachViewer(pair, viewer);
        });

        var homeNet = sEntMan.GetNetEntity(homeGrid);
        var farNet = sEntMan.GetNetEntity(farGrid);
        var crateNet = sEntMan.GetNetEntity(crate);

        await RunTicksChecked(pair, 20);

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.TryGetEntity(crateNet, out var cCrate),
                "The client never received the crate, so there is nothing to detach.");
            var proxies = cEntMan.GetComponent<FixturesComponent>(cCrate!.Value).Fixtures.Values.Sum(f => f.ProxyCount);
            Assert.That(proxies, Is.GreaterThan(0),
                "The crate has no broadphase proxies on the client, so there are none to drop or leave behind.");
        });

        await server.WaitPost(() =>
            xformSystem.SetCoordinates(viewer, new EntityCoordinates(farGrid, new Vector2(2f, 2f))));

        await RunTicksChecked(pair, 20);

        await client.WaitAssertion(() =>
        {
            Assert.That(IsDetached(cEntMan, cEntMan.GetEntity(crateNet)), Is.True,
                "The crate never left the client's view, so the PVS detach path was not exercised.");
        });

        // Change it while the client cannot see it, then let the client see it again.
        await server.WaitPost(() =>
        {
            switch (change)
            {
                case AwayChange.MovedIntoSpace:
                    xformSystem.SetCoordinates(crate, new EntityCoordinates(map.MapUid, new Vector2(FarAway + 4f, -6f)));
                    break;
                case AwayChange.MovedToAnotherGrid:
                    xformSystem.SetCoordinates(crate, new EntityCoordinates(farGrid, new Vector2(5f, 5f)));
                    break;
                case AwayChange.BecameStatic:
                    physics.SetBodyType(crate, BodyType.Static);
                    xformSystem.SetCoordinates(viewer, new EntityCoordinates(homeGrid, new Vector2(2f, 2f)));
                    break;
            }
        });

        await RunTicksChecked(pair, 20);

        // A stale proxy id only does damage when it is next moved, so move the crate again on the client.
        await server.WaitPost(() =>
        {
            var xform = sEntMan.GetComponent<TransformComponent>(crate);
            xformSystem.SetCoordinates(crate, new EntityCoordinates(xform.ParentUid, xform.LocalPosition + new Vector2(0.5f, 0.5f)));
        });

        await RunTicksChecked(pair, 10);

        await client.WaitAssertion(() =>
        {
            var cCrate = cEntMan.GetEntity(crateNet);
            Assert.That(IsDetached(cEntMan, cCrate), Is.False,
                "The crate never came back into the client's view, so the re-entry path was not exercised.");

            var expectedParent = change switch
            {
                AwayChange.MovedIntoSpace => cEntMan.GetEntity(sEntMan.GetNetEntity(map.MapUid)),
                AwayChange.MovedToAnotherGrid => cEntMan.GetEntity(farNet),
                _ => cEntMan.GetEntity(homeNet),
            };
            Assert.That(cEntMan.GetComponent<TransformComponent>(cCrate).ParentUid, Is.EqualTo(expectedParent),
                "The crate came back somewhere other than where this case put it.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     The fight the player was in, seen from a client standing next to it: a shuttle grinding into another while
    ///     rounds fly, tiles break, debris splits off and crew get thrown around - and the viewer keeps moving in and
    ///     out of range so entities keep leaving and re-entering PVS.
    /// </summary>
    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RamSeenUpCloseKeepsClientBroadphaseIntact(bool shields)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        server.CfgMan.SetCVar(CVars.NetPVS, true);

        var entMan = server.EntMan;
        var mapSystem = entMan.System<SharedMapSystem>();
        var xformSystem = entMan.System<SharedTransformSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileId = server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;

        EntityUid rammer = default;
        EntityUid target = default;
        EntityUid viewer = default;
        var gridsSeen = new HashSet<EntityUid>();
        var targetTilesBefore = 0;

        var near = new Vector2(24f, -12f);
        var far = new Vector2(FarAway, 0f);

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(map.Grid);

            var rammerGrid = mapSystem.CreateGridEntity(map.MapId);
            var targetGrid = mapSystem.CreateGridEntity(map.MapId);
            rammer = rammerGrid.Owner;
            target = targetGrid.Owner;

            mapSystem.SetTiles(rammer, rammerGrid.Comp, Block(12, 6, tileId));
            mapSystem.SetTiles(target, targetGrid.Comp, Block(20, 20, tileId));
            xformSystem.SetLocalPosition(target, new Vector2(30f, -7f));

            if (shields)
            {
                SpawnPoweredEmitter(entMan, rammer, new Vector2(4f, 3f));
                SpawnPoweredEmitter(entMan, target, new Vector2(4f, 4f));
            }

            // Crew on both ships, so the ram has bodies to throw off grids and across them.
            for (var x = 1; x < 12; x += 3)
            {
                entMan.SpawnEntity("MobHuman", new EntityCoordinates(rammer, new Vector2(x + 0.5f, 2.5f)));
            }

            for (var y = 1; y < 20; y += 3)
            {
                entMan.SpawnEntity("MobHuman", new EntityCoordinates(target, new Vector2(1.5f, y + 0.5f)));
            }

            viewer = entMan.SpawnEntity(null, new MapCoordinates(near, map.MapId));
            AttachViewer(pair, viewer);

            targetTilesBefore = CountTiles(entMan, mapSystem, target);
        });

        // Power recalculates next tick and emitters update once a second.
        await RunTicksChecked(pair, (int) (2.5f * server.Timing.TickRate));

        for (var i = 0; i < 40; i++)
        {
            var round = i;
            await server.WaitPost(() =>
            {
                // The rammer can be ground to nothing against a shield; the rest of the fight goes on without it.
                if (entMan.TryGetComponent(rammer, out PhysicsComponent? body))
                {
                    physics.SetBodyType(rammer, BodyType.Dynamic, body: body);
                    physics.SetLinearDamping(rammer, body, 0f);
                    physics.SetLinearVelocity(rammer, new Vector2(40f, 0f), body: body);
                    physics.WakeBody(rammer, body: body);
                }

                for (var b = 0; b < 8; b++)
                {
                    var bullet = entMan.SpawnEntity("BulletRifle", new MapCoordinates(new Vector2(18f, -6f + b * 2.5f), map.MapId));
                    physics.SetLinearVelocity(bullet, new Vector2(150f, 0f));
                }

                // Break a crewed slice off the target, as a hull breach does: the crew on it move to a new grid.
                if ((round == 10 || round == 25) && entMan.TryGetComponent(target, out MapGridComponent? targetComp))
                {
                    var column = round == 10 ? 6 : 13;
                    for (var y = -2; y < 22; y++)
                    {
                        mapSystem.SetTile(target, targetComp, new Vector2i(column, y), Tile.Empty);
                    }
                }

                // Swing the view away and back so everything near the impact keeps leaving and re-entering PVS.
                if (round % 8 == 4)
                    xformSystem.SetCoordinates(viewer, new EntityCoordinates(map.MapUid, far));
                else if (round % 8 == 0)
                    xformSystem.SetCoordinates(viewer, new EntityCoordinates(map.MapUid, near));
            });

            await RunTicksChecked(pair, 5, () => AddGrids(entMan, map.MapId, gridsSeen));
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(CountTiles(entMan, mapSystem, target), Is.LessThan(targetTilesBefore),
                "No tiles broke, so the grids never actually rammed and this test proved nothing.");
            Assert.That(gridsSeen, Has.Count.GreaterThan(2),
                "No debris ever split off, so grid splitting never happened on the client.");
        });

        await pair.CleanReturnAsync();
    }

    private static void AttachViewer(TestPair pair, EntityUid viewer)
    {
        var session = pair.Player!;
        pair.Server.PlayerMan.SetAttachedEntity(session, viewer);

        // PVS only sends leave messages to in-game sessions; a test pair's session is merely connected.
        if (session.Status != SessionStatus.InGame)
            pair.Server.PlayerMan.JoinGame(session);
    }

    private static EntityUid SpawnPoweredEmitter(IEntityManager entMan, EntityUid grid, Vector2 localPos)
    {
        var emitter = entMan.SpawnEntity(EmitterPrototype, new EntityCoordinates(grid, localPos));
        // No APC in a test map: this makes the receiver count as powered, the same state a working grid gives it.
        entMan.System<PowerReceiverSystem>().SetNeedsPower(emitter, false);
        return emitter;
    }

    private static void AddGrids(IEntityManager entMan, MapId mapId, HashSet<EntityUid> grids)
    {
        var query = entMan.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapID == mapId)
                grids.Add(uid);
        }
    }

    private static int CountTiles(IEntityManager entMan, SharedMapSystem mapSystem, EntityUid grid)
    {
        if (!entMan.TryGetComponent(grid, out MapGridComponent? comp))
            return 0;

        var count = 0;
        var enumerator = mapSystem.GetAllTilesEnumerator(grid, comp);
        while (enumerator.MoveNext(out _))
        {
            count++;
        }

        return count;
    }

    private static bool IsDetached(IEntityManager entMan, EntityUid uid)
    {
        return (entMan.GetComponent<MetaDataComponent>(uid).Flags & MetaDataFlags.Detached) != 0;
    }

    /// <summary>
    ///     Runs ticks one at a time, checking the client's broadphase after each, and fails instead of hanging if the
    ///     client stops responding.
    /// </summary>
    private static async Task RunTicksChecked(TestPair pair, int ticks, Action? afterServerTick = null)
    {
        for (var i = 0; i < ticks; i++)
        {
            var tick = pair.RunTicksSync(1);
            if (await Task.WhenAny(tick, Task.Delay(HangTimeout)) != tick)
            {
                Assert.Fail($"The client did not finish a tick within {HangTimeout.TotalSeconds}s. This is the " +
                            "B2DynamicTree.InsertLeaf infinite loop on a corrupted broadphase tree.");
            }

            await tick;

            if (afterServerTick != null)
                await pair.Server.WaitPost(afterServerTick);

            await pair.Client.WaitAssertion(() =>
            {
                var problems = ClientBroadphaseIntegrity.FindProblems(pair.Client.EntMan);
                Assert.That(problems, Is.Empty,
                    $"Client broadphase is corrupt after tick {i + 1}:\n" + string.Join("\n", problems));
            });
        }
    }

    private static List<(Vector2i, Tile)> Block(int width, int height, int tileId)
    {
        var tiles = new List<(Vector2i, Tile)>(width * height);
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                tiles.Add((new Vector2i(x, y), new Tile(tileId)));
            }
        }

        return tiles;
    }
}

/// <summary>
///     Checks every physics broadphase tree an entity manager holds for the corruption that hangs a client.
///     The engine's own tree validation is compiled out (DEBUG_DYNAMIC_TREE), so this reads the tree's nodes directly.
/// </summary>
public static class ClientBroadphaseIntegrity
{
    private const int MaxProblemsPerTree = 5;

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags Public = BindingFlags.Instance | BindingFlags.Public;

    public static List<string> FindProblems(IEntityManager entMan)
    {
        var problems = new List<string>();

        var broadphases = entMan.AllEntityQueryEnumerator<BroadphaseComponent>();
        while (broadphases.MoveNext(out var uid, out var broadphase))
        {
            var name = entMan.ToPrettyString(uid).ToString();
            CheckTree(broadphase.StaticTree.Tree, $"{name} static", problems);
            CheckTree(broadphase.DynamicTree.Tree, $"{name} dynamic", problems);
        }

        CheckEntityProxies(entMan, problems);
        return problems;
    }

    /// <summary>
    ///     Every proxy of a collidable entity on a broadphase must be the proxy its id points at in the tree the
    ///     lookup will move it in. If it is not, the entity's next move runs RemoveLeaf/InsertLeaf on someone else's
    ///     node (or a free one) and breaks that tree.
    /// </summary>
    private static void CheckEntityProxies(IEntityManager entMan, List<string> problems)
    {
        var lookup = entMan.System<EntityLookupSystem>();
        var query = entMan.AllEntityQueryEnumerator<FixturesComponent, PhysicsComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var fixtures, out var body, out var xform))
        {
            if (xform.GridUid == uid || !body.CanCollide)
                continue;

            if (!lookup.TryGetCurrentBroadphase(xform, out var broadphase))
                continue;

            var tree = body.BodyType == BodyType.Static ? broadphase.StaticTree : broadphase.DynamicTree;
            var capacity = tree.Tree.Capacity;

            foreach (var (id, fixture) in fixtures.Fixtures)
            {
                for (var i = 0; i < fixture.ProxyCount; i++)
                {
                    var proxy = fixture.Proxies[i];
                    int index = proxy.ProxyId;

                    if (index < 0 || index >= capacity || !ReferenceEquals(tree.GetProxy(proxy.ProxyId), proxy))
                    {
                        problems.Add($"{entMan.ToPrettyString(uid)} fixture '{id}' proxy {i} has id {index}, which " +
                                     $"is not its own leaf in the {body.BodyType} tree of {entMan.ToPrettyString(broadphase.Owner)}. " +
                                     "Its next move corrupts that tree.");
                    }
                }
            }
        }
    }

    private static void CheckTree(B2DynamicTree<FixtureProxy> tree, string name, List<string> problems)
    {
        var start = problems.Count;
        void Problem(string message)
        {
            if (problems.Count - start < MaxProblemsPerTree)
                problems.Add($"[{name} tree] {message}");
        }

        var treeType = tree.GetType();
        var nodes = (Array) treeType.GetField("_nodes", Private)!.GetValue(tree)!;
        int root = (DynamicTree.Proxy) treeType.GetField("_root", Private)!.GetValue(tree)!;
        int freeList = (DynamicTree.Proxy) treeType.GetField("_freeList", Private)!.GetValue(tree)!;

        var nodeType = nodes.GetType().GetElementType()!;
        var parentField = nodeType.GetField("Parent", Public)!;
        var nextField = nodeType.GetField("Next", Public)!;
        var child1Field = nodeType.GetField("Child1", Public)!;
        var child2Field = nodeType.GetField("Child2", Public)!;
        var heightField = nodeType.GetField("Height", Public)!;
        var userDataField = nodeType.GetField("UserData", Public)!;

        int Parent(object node) => (DynamicTree.Proxy) parentField.GetValue(node)!;
        int Next(object node) => (DynamicTree.Proxy) nextField.GetValue(node)!;
        int Child1(object node) => (DynamicTree.Proxy) child1Field.GetValue(node)!;
        int Child2(object node) => (DynamicTree.Proxy) child2Field.GetValue(node)!;
        short Height(object node) => (short) heightField.GetValue(node)!;

        const int free = -1;
        var capacity = nodes.Length;
        var seen = new bool[capacity];
        var reached = 0;
        var leaves = 0;

        if (root != free)
        {
            if (root < 0 || root >= capacity)
            {
                Problem($"root {root} is outside the node pool ({capacity})");
                return;
            }

            if (Parent(nodes.GetValue(root)!) != free)
                Problem($"root {root} has a parent");

            var stack = new Stack<int>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var index = stack.Pop();

                if (seen[index])
                {
                    Problem($"node {index} is reachable twice: the tree has a cycle or a shared child. InsertLeaf/" +
                            "FindBestSibling loops forever on this.");
                    return;
                }

                seen[index] = true;
                reached++;

                var node = nodes.GetValue(index)!;
                var height = Height(node);

                if (height < 0)
                {
                    Problem($"node {index} is in the tree but marked free");
                    continue;
                }

                var c1 = Child1(node);
                var c2 = Child2(node);

                if (height == 0)
                {
                    leaves++;
                    if (c1 != free || c2 != free)
                        Problem($"leaf {index} has children {c1}/{c2}");

                    var proxy = (FixtureProxy?) userDataField.GetValue(node);
                    if (proxy == null)
                        Problem($"leaf {index} holds no proxy");
                    else if ((int) proxy.ProxyId != index)
                        Problem($"leaf {index} holds the proxy of {proxy.Entity} fixture '{proxy.FixtureId}', whose id is {(int) proxy.ProxyId}");

                    continue;
                }

                if (c1 < 0 || c1 >= capacity || c2 < 0 || c2 >= capacity)
                {
                    Problem($"branch {index} has children {c1}/{c2} outside the node pool");
                    continue;
                }

                var child1 = nodes.GetValue(c1)!;
                var child2 = nodes.GetValue(c2)!;

                if (Parent(child1) != index || Parent(child2) != index)
                    Problem($"branch {index}'s children {c1}/{c2} point at parents {Parent(child1)}/{Parent(child2)}");

                if (height != 1 + Math.Max(Height(child1), Height(child2)))
                    Problem($"branch {index} has height {height}, children {Height(child1)}/{Height(child2)}");

                stack.Push(c1);
                stack.Push(c2);
            }
        }

        if (leaves != tree.ProxyCount)
            Problem($"{leaves} leaves reachable but ProxyCount is {tree.ProxyCount}");

        if (reached != tree.NodeCount)
            Problem($"{reached} nodes reachable but NodeCount is {tree.NodeCount}");

        var freeCount = 0;
        for (var index = freeList; index != free; index = Next(nodes.GetValue(index)!))
        {
            if (index < 0 || index >= capacity)
            {
                Problem($"free list runs outside the node pool ({index})");
                break;
            }

            if (seen[index])
            {
                Problem($"node {index} is both in the tree and on the free list (double free); the next allocation " +
                        "hands out a node that is still in use");
                break;
            }

            seen[index] = true;
            freeCount++;

            if (Height(nodes.GetValue(index)!) != -1)
                Problem($"free-list node {index} is not marked free");
        }

        if (freeCount + tree.NodeCount != capacity)
            Problem($"{freeCount} free + {tree.NodeCount} used nodes != capacity {capacity} (leaked or double-freed nodes)");
    }
}
