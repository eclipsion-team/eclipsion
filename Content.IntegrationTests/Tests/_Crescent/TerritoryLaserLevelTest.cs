using Content.Shared._Crescent.Territory;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class TerritoryLaserLevelTest
{
    [Test]
    public async Task HeavierLasersUnlockByLevel()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var proto = server.ProtoMan;
        var mapSystem = server.System<SharedMapSystem>();
        var levels = server.System<PersistentPoiLevelSystem>();

        Entity<PersistentPoiGridComponent> territory = default;

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapSystem.CreateGridEntity(map.MapId);
            var plating = server.ResolveDependency<ITileDefinitionManager>()["Plating"];
            for (var x = 0; x < 8; x++)
            {
                mapSystem.SetTile(gridEnt, new Vector2i(x, 0), new Tile(plating.TileId));
            }

            var comp = entMan.EnsureComponent<PersistentPoiGridComponent>(gridEnt.Owner);
            comp.Owner = "DSM";
            territory = (gridEnt.Owner, comp);
        });

        PersistentPoiRecipeStatus Status(string recipe)
        {
            return levels.GetRecipeStatus(territory, proto.Index<ConstructionPrototype>(recipe), "DSM");
        }

        await server.WaitAssertion(() =>
        {
            territory.Comp.Level = 1;
            var medium = Status("PersistentPoiMediumLaserEmplacement");
            Assert.That(medium.Block, Is.EqualTo(PersistentPoiBuildBlock.NeedsLevel));
            Assert.That(medium.Level, Is.EqualTo(2));
            var heavy = Status("PersistentPoiHeavyLaserEmplacement");
            Assert.That(heavy.Block, Is.EqualTo(PersistentPoiBuildBlock.NeedsLevel));
            Assert.That(heavy.Level, Is.EqualTo(3));

            territory.Comp.Level = 2;
            Assert.That(Status("PersistentPoiMediumLaserEmplacement").Block, Is.EqualTo(PersistentPoiBuildBlock.None));
            Assert.That(Status("PersistentPoiHeavyLaserEmplacement").Block, Is.EqualTo(PersistentPoiBuildBlock.NeedsLevel));

            territory.Comp.Level = 3;
            Assert.That(Status("PersistentPoiHeavyLaserEmplacement").Block, Is.EqualTo(PersistentPoiBuildBlock.None));

            // the heavy lasers still count as point-defence guns
            for (var x = 0; x < 8; x++)
            {
                entMan.SpawnEntity("HardpointPersistentPoiLaserEmplacement", new EntityCoordinates(territory, x + 0.5f, 0.5f));
            }
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var full = Status("PersistentPoiHeavyLaserEmplacement");
            Assert.That(full.Block, Is.EqualTo(PersistentPoiBuildBlock.LimitReached));
            Assert.That(full.Category, Is.EqualTo(PersistentPoiWorksCategory.PointDefence));
            entMan.DeleteEntity(territory);
        });

        await pair.CleanReturnAsync();
    }
}
