using System.Collections.Generic;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server._Crescent.Barricades;
using Content.Shared._Crescent.Barricades;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Robust.Shared.Exceptions;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Crescent.Barricades;

[TestFixture]
public sealed class BarricadePvsTest
{
    private static readonly ProtoId<DamageTypePrototype> BluntDamage = "Blunt";

    private static readonly EntProtoId[] BarricadePrototypes =
    [
        "CrescentBarricadeMetal",
        "CrescentBarricadePlasteel",
        "CrescentBarricadeWood",
        "CrescentBarricadeSandbag",
        "CrescentBarricadeMetalReinforced",
        "CrescentBarricadeMetalBiohazard",
        "CrescentBarricadeMetalComposite",
        "CrescentBarricadePlasteelReinforced",
        "CrescentBarricadePlasteelBiohazard",
        "CrescentBarricadePlasteelComposite",
        "CrescentBarbedWireDeployed",
    ];

    [Test]
    public async Task BarricadesSerializeToConnectedClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            DummyTicker = false,
        });
        var (server, client) = pair;
        var map = await pair.CreateTestMap();

        var spawned = new List<EntityUid>();
        await server.WaitAssertion(() =>
        {
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 21.824779f;
            moles[(int) Gas.Nitrogen] = 82.10312f;
            server.EntMan.System<AtmosphereSystem>().SetMapAtmosphere(map.MapUid, false,
                new GasMixture(moles, Atmospherics.T20C));

            for (var i = 0; i < BarricadePrototypes.Length; i++)
            {
                var coordinates = new MapCoordinates(new Vector2(i * 2, 0), map.MapId);
                spawned.Add(server.EntMan.Spawn(BarricadePrototypes[i], coordinates));
            }
        });

        await pair.RunTicksSync(10);

        foreach (var barricade in spawned)
        {
            var clientUid = pair.ToClientUid(barricade);
            Assert.That(client.EntMan.EntityExists(clientUid), Is.True);
        }

        await server.WaitAssertion(() =>
        {
            var coordinates = new MapCoordinates(new Vector2(0.5f, 4.5f), map.MapId);
            // Give the projectile a connected floor with air, away from the barricades.
            for (var y = 1; y <= 4; y++)
                server.EntMan.System<SharedMapSystem>().SetTile(map.Grid.Owner, map.Grid.Comp,
                    new Vector2i(0, y), map.Tile.Tile);
            server.EntMan.Spawn("BulletFlamethrower", coordinates);
        });
        await pair.RunSeconds(0.3f);

        var floorFires = 0;
        await server.WaitAssertion(() =>
            floorFires = server.EntMan.Count<CrescentTileFireComponent>());
        Assert.That(floorFires, Is.GreaterThan(0), "A flamethrower projectile must leave floor fire.");

        await server.WaitAssertion(() =>
        {
            var coordinates = new MapCoordinates(new Vector2(0, 8), map.MapId);
            var original = server.EntMan.Spawn("CrescentBarricadeMetal", coordinates);
            var kit = server.EntMan.Spawn("CrescentBarricadeUpgradeReinforced", coordinates);
            var user = server.EntMan.SpawnEntity(null, coordinates);

            var damageable = server.EntMan.System<DamageableSystem>();
            var prototypeManager = server.ResolveDependency<IPrototypeManager>();
            var damage = new DamageSpecifier(
                prototypeManager.Index(BluntDamage),
                FixedPoint2.New(50));
            damageable.TryChangeDamage(original, damage, ignoreResistances: true);

            var flammableSystem = server.EntMan.System<FlammableSystem>();
            var flammable = server.EntMan.GetComponent<FlammableComponent>(original);
            flammableSystem.SetFireStacks(original, 0.25f, flammable);
            flammableSystem.Ignite(original, original, flammable);

            var barbed = server.EntMan.GetComponent<BarricadeBarbedComponent>(original);
            barbed.IsBarbed = true;

            var interact = new InteractUsingEvent(
                user,
                kit,
                original,
                server.EntMan.GetComponent<TransformComponent>(original).Coordinates);
            server.EntMan.EventBus.RaiseLocalEvent(original, interact);

            EntityUid upgraded = default;
            var query = server.EntMan.AllEntityQueryEnumerator<BarricadeUpgradeableComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out _, out var metadata))
            {
                if (metadata.EntityPrototype?.ID == "CrescentBarricadeMetalReinforced" &&
                    !spawned.Contains(uid))
                {
                    upgraded = uid;
                    break;
                }
            }

            Assert.That(upgraded.IsValid(), Is.True, "Applying an upgrade kit must create the upgraded barricade.");

            var upgradedDamage = server.EntMan.GetComponent<DamageableComponent>(upgraded);
            Assert.That(upgradedDamage.TotalDamage,
                Is.EqualTo(FixedPoint2.New(50)),
                "Upgrading must preserve existing structural damage.");

            var upgradedFlammable = server.EntMan.GetComponent<FlammableComponent>(upgraded);
            Assert.Multiple(() =>
            {
                Assert.That(upgradedFlammable.OnFire, Is.True, "Upgrading must not extinguish a burning barricade.");
                Assert.That(upgradedFlammable.FireStacks, Is.EqualTo(0.25f).Within(0.001f));
                Assert.That(
                    server.EntMan.GetComponent<BarricadeBarbedComponent>(upgraded).IsBarbed,
                    Is.True,
                    "Upgrading must preserve attached barbed wire.");
            });
        });

        var serverLog = server.ResolveDependency<IRuntimeLog>();
        var clientLog = client.ResolveDependency<IRuntimeLog>();
        Assert.Multiple(() =>
        {
            Assert.That(serverLog.ExceptionCount, Is.EqualTo(0), "Barricades must not log server exceptions.");
            Assert.That(clientLog.ExceptionCount, Is.EqualTo(0), "Barricades must not log client exceptions.");
        });

        await pair.CleanReturnAsync();
    }
}
