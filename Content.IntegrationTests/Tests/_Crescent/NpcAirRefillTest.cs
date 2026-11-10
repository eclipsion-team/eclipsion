using System.Numerics;
using Content.Server._Crescent.NPC;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Gravity;
using Content.Server.Power.EntitySystems;
using Content.Shared.CCVar;
using Content.Shared.Gravity;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.VendingMachines;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(NpcStartInternalsSystem))]
public sealed class NpcAirRefillTest
{
    private const string Soldier = "MobSoldierAIDSMRifleman";

    /// <summary>
    /// A soldier in vacuum with every tank breathed dry walks over to a full tank on the floor, puts it on and
    /// breathes from it, and drops the empty one.
    /// </summary>
    [Test]
    public async Task SoldierPicksUpLooseTank()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default, tank = default, spent = default;

        await server.WaitPost(() =>
        {
            LayFloor(server, map);
            soldier = server.EntMan.SpawnEntity(Soldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            tank = server.EntMan.SpawnEntity("DoubleEmergencyOxygenTankFilled", new EntityCoordinates(map.Grid, new Vector2(5.5f, 0.5f)));
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() => spent = EmptyAllTanks(server.EntMan, soldier));
        await RunUntil(server, () => IsBreathingFrom(server.EntMan, soldier, tank), 20);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            Assert.That(IsBreathingFrom(entMan, soldier, tank), Is.True, "The soldier didn't put the loose tank on.");
            Assert.That(entMan.Deleted(spent) || !IsCarried(entMan, spent, soldier), Is.True,
                "The soldier kept its empty tank.");
            Assert.That(entMan.System<MobStateSystem>().IsAlive(soldier), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// With no loose tank about, it takes one out of a tank dispenser.
    /// </summary>
    [Test]
    public async Task SoldierTakesTankFromVendor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false));

        EntityUid soldier = default, vendor = default;
        uint stockBefore = 0;

        await server.WaitPost(() =>
        {
            var entMan = server.EntMan;
            LayFloor(server, map);
            soldier = entMan.SpawnEntity(Soldier, new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            vendor = entMan.SpawnEntity("VendingMachineTankDispenserEVA", new EntityCoordinates(map.Grid, new Vector2(5.5f, 0.5f)));
            entMan.System<PowerReceiverSystem>().SetNeedsPower(vendor, false);
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            EmptyAllTanks(server.EntMan, soldier);
            stockBefore = TotalStock(server.EntMan, vendor);
        });

        await RunUntil(server, () => IsBreathingFromFullTank(server.EntMan, soldier), 20);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            Assert.That(IsBreathingFromFullTank(entMan, soldier), Is.True,
                "The soldier didn't get a tank out of the dispenser.");
            Assert.That(TotalStock(entMan, vendor), Is.EqualTo(stockBefore - 1), "The dispenser's stock didn't go down by one.");
        });

        await pair.CleanReturnAsync();
    }

    private static async Task RunUntil(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server, Func<bool> done, int seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            await server.WaitRunTicks(server.Timing.TickRate / 2);

            var finished = false;
            await server.WaitPost(() => finished = done());
            if (finished)
                return;
        }
    }

    /// <summary>
    /// Clears every tank the soldier carries, jetpack included, so it has nothing at all left to breathe.
    /// Returns the pocket tank.
    /// </summary>
    private static EntityUid EmptyAllTanks(IEntityManager entMan, EntityUid soldier)
    {
        EntityUid pocket = default;

        foreach (var item in entMan.System<NpcTacticalSystem>().EnumerateCarried(soldier))
        {
            if (!entMan.TryGetComponent<GasTankComponent>(item, out var gasTank))
                continue;

            gasTank.Air.Clear();

            if (!entMan.HasComponent<JetpackComponent>(item))
                pocket = item;
        }

        Assert.That(pocket, Is.Not.EqualTo(default(EntityUid)), "The soldier spawned without its pocket tank.");
        return pocket;
    }

    private static bool IsBreathingFrom(IEntityManager entMan, EntityUid soldier, EntityUid tank)
    {
        return entMan.GetComponent<InternalsComponent>(soldier).GasTankEntity == tank && IsCarried(entMan, tank, soldier);
    }

    private static bool IsBreathingFromFullTank(IEntityManager entMan, EntityUid soldier)
    {
        return entMan.GetComponent<InternalsComponent>(soldier).GasTankEntity is { } tank &&
               !entMan.HasComponent<JetpackComponent>(tank) &&
               entMan.GetComponent<GasTankComponent>(tank).Air.Pressure > 500f;
    }

    private static bool IsCarried(IEntityManager entMan, EntityUid item, EntityUid carrier)
    {
        var containers = entMan.System<SharedContainerSystem>();
        var current = item;

        while (containers.TryGetContainingContainer((current, null, null), out var container))
        {
            if (container.Owner == carrier)
                return true;

            current = container.Owner;
        }

        return false;
    }

    private static uint TotalStock(IEntityManager entMan, EntityUid vendor)
    {
        uint total = 0;
        foreach (var entry in entMan.GetComponent<VendingMachineComponent>(vendor).Inventory.Values)
        {
            total += entry.Amount;
        }

        return total;
    }

    private static void LayFloor(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server, Pair.TestMapData map)
    {
        var entMan = server.EntMan;
        var mapSys = server.System<SharedMapSystem>();

        var gravity = entMan.EnsureComponent<GravityComponent>(map.Grid);
        entMan.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        gravity.Inherent = true;

        for (var x = -2; x <= 8; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
            }
        }
    }
}
