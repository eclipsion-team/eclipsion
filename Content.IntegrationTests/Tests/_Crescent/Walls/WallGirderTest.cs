using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent.Walls;

/// <summary>
/// Destroyed hull walls must leave an anchored girder on their own tile, including on a rotated ship grid.
/// </summary>
[TestFixture]
public sealed class WallGirderTest
{
    private static readonly ProtoId<DamageTypePrototype> BluntDamage = "Blunt";

    private static readonly EntProtoId[] WallPrototypes =
    [
        "WallPlastitanium",
        "WallPlastitaniumSRM",
        "WallPlastitaniumDiagonalCurved",
        "WallMining",
        "WallMiningSHI",
        "WallMiningDiagonalCurved",
    ];

    [Test]
    public async Task DestroyedWallsLeaveGirder()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var damageable = entMan.System<DamageableSystem>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            // Ships are rarely axis-aligned; this is where the old random-offset spawn missed the tile.
            xformSys.SetLocalRotation(map.Grid.Owner, Angle.FromDegrees(37));

            // Fill the gaps between the walls too: isolated tiles get split off into grids of their own.
            for (var x = 0; x < WallPrototypes.Length * 2; x++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, 0), map.Tile.Tile);
            }
        });

        await server.WaitAssertion(() =>
        {
            var damage = new DamageSpecifier(protoMan.Index(BluntDamage), FixedPoint2.New(5000));

            Assert.Multiple(() =>
            {
                for (var i = 0; i < WallPrototypes.Length; i++)
                {
                    var proto = WallPrototypes[i];
                    var tile = new Vector2i(i * 2, 0);
                    var coords = mapSys.GridTileToLocal(map.Grid.Owner, map.Grid.Comp, tile);
                    var wall = entMan.SpawnAtPosition(proto, coords);

                    damageable.TryChangeDamage(wall, damage, ignoreResistances: true);

                    var girderFound = false;
                    var anchored = mapSys.GetAnchoredEntitiesEnumerator(map.Grid.Owner, map.Grid.Comp, tile);
                    while (anchored.MoveNext(out var uid))
                    {
                        if (entMan.GetComponent<MetaDataComponent>(uid.Value).EntityPrototype?.ID == "Girder")
                        {
                            girderFound = true;
                            break;
                        }
                    }

                    Assert.That(girderFound, Is.True, $"{proto} must leave an anchored girder on its own tile.");
                }
            });
        });

        await pair.CleanReturnAsync();
    }
}
