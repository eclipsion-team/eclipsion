using Content.Server._Crescent.Territory;
using Content.Server.Construction;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class TerritoryDoorAccessTest
{
    [Test]
    public async Task BuiltDoorsAdmitOnlyTheHolder()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var regionId = $"integration-test-{Guid.NewGuid():N}";
        EntityUid holder = default;
        EntityUid outsider = default;
        var frames = new[] { "PersistentPoiFrameAirlock", "PersistentPoiFrameAirlockGlass", "PersistentPoiFrameAirlockPublic", "PersistentPoiFrameDockingPort" };
        var nodes = new[] { "airlock", "airlockGlass", "airlockPublic", "dockingPort" };

        await server.WaitAssertion(() =>
        {
            var flagUid = entMan.CreateEntityUninitialized("PersistentCaptureRegionFlag", map.GridCoords);
            entMan.GetComponent<PersistentCaptureRegionComponent>(flagUid).RegionId = regionId;
            entMan.InitializeAndStartEntity(flagUid);
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<PersistentCaptureRegionSystem>().SetOwner(regionId, "TFSC"), Is.True);
            holder = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.EnsureComponent<HullrotFactionComponent>(holder).Faction = "TFSC";
            outsider = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.EnsureComponent<HullrotFactionComponent>(outsider).Faction = "DSM";
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < frames.Length; i++)
            {
                var frame = entMan.SpawnEntity(frames[i], map.GridCoords);
                server.System<ConstructionSystem>().ChangeNode(frame, holder, nodes[i]);
            }
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var access = server.System<TerritoryAccessSystem>();
            var doors = server.System<SharedDoorSystem>();
            var count = 0;
            var q = entMan.EntityQueryEnumerator<DoorComponent>();
            while (q.MoveNext(out var door, out _))
            {
                count++;
                var name = entMan.ToPrettyString(door);
                var gated = entMan.HasComponent<TerritoryAccessComponent>(door);
                Assert.That(doors.HasAccess(door, holder), Is.True, $"{name} refused the holder's ID");
                if (gated)
                {
                    Assert.That(access.CanPass(door, holder), Is.True, $"{name} refused the holder");
                    Assert.That(access.CanPass(door, outsider), Is.False, $"{name} let an outsider through");
                }
            }
            Assert.That(count, Is.EqualTo(4));
        });

        await server.WaitAssertion(() => server.System<PersistentCaptureRegionSystem>().ForgetRegion(regionId));
        await pair.CleanReturnAsync();
    }
}
