using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Territory;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Crescent.Territory;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class PersistentPoiTurretSaveTest
{
    private static readonly string[] Built =
    {
        "HardpointPersistentPoiLaserEmplacement",
        "WeaponTurretRetributionPersistentPoi",
        "WeaponTurretAutoPDPersistentPoi",
        "HardpointPersistentPoiMediumLaserEmplacement",
        "WeaponTurretWardenPersistentPoi",
        "HardpointPersistentPoiHeavyLaserEmplacement",
        "WeaponTurretBastionPersistentPoi",
    };

    [Test]
    public async Task BuiltTurretsSurviveSaveAndLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var saves = server.System<PersistentPoiSaveSystem>();
        var key = $"turret-save-test-{Guid.NewGuid():N}";

        EntityUid grid = default;
        EntityUid? loaded = null;

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            grid = gridEnt.Owner;
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 6; x++)
            {
                mapSystem.SetTile(gridEnt, new Vector2i(x, 0), new Tile(plating.TileId));
            }

            saves.MarkPersistent(grid, key);

            entMan.SpawnEntity("HardpointPersistentPoiLaserEmplacement", new EntityCoordinates(grid, 0.5f, 0.5f));
            entMan.SpawnEntity("WeaponTurretAutoPDPersistentPoi", new EntityCoordinates(grid, 2.5f, 0.5f));
            entMan.SpawnEntity("HardpointPersistentPoiMediumLaserEmplacement", new EntityCoordinates(grid, 3.5f, 0.5f));
            entMan.SpawnEntity("HardpointPersistentPoiHeavyLaserEmplacement", new EntityCoordinates(grid, 5.5f, 0.5f));
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            AssertBuilt(entMan, grid, "before the save");
            Assert.That(saves.TrySave((grid, entMan.GetComponent<PersistentPoiSaveComponent>(grid))), Is.True);
            AssertBuilt(entMan, grid, "after the save");
            entMan.DeleteEntity(grid);
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            Assert.That(saves.TryLoad(map.MapId, key, 1, new ResPath("/Maps/none.yml"), Vector2.Zero, out loaded),
                Is.True);
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            AssertBuilt(entMan, loaded!.Value, "after the load");
            saves.Reset(key);
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertBuilt(IEntityManager entMan, EntityUid grid, string when)
    {
        var onGrid = new List<EntityUid>();
        var query = entMan.AllEntityQueryEnumerator<PersistentPoiStructureComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == grid)
                onGrid.Add(uid);
        }

        foreach (var proto in Built)
        {
            var found = onGrid.Where(u => entMan.GetComponent<MetaDataComponent>(u).EntityPrototype?.ID == proto)
                .ToList();
            Assert.That(found, Has.Count.EqualTo(1), $"{proto} count {when}");
            if (found.Count == 1)
                Assert.That(entMan.GetComponent<TransformComponent>(found[0]).Anchored, Is.True, $"{proto} anchored {when}");
        }

        foreach (var proto in new[] { Built[1], Built[4], Built[6] })
        {
            var laser = onGrid.Single(u => entMan.GetComponent<MetaDataComponent>(u).EntityPrototype?.ID == proto);
            Assert.That(entMan.GetComponent<HardpointAnchorableOnlyComponent>(laser).anchoredTo, Is.Not.Null,
                $"{proto} mounted {when}");
        }
    }
}
