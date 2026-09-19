using Content.Server.Emp;
using Content.Server.Mech.Systems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Mech;
using Content.Shared.Mech.Components;
using Robust.Server.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Mech;

[TestFixture]
public sealed class MechPowerVisualTest
{
    [TestCase("MechSHISuzumeBattery")]
    [TestCase("MechDSMBastionBattery")]
    public async Task IndicatorTracksBatteryAndDisabledStates(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.EntMan;
        var appearance = entities.System<SharedAppearanceSystem>();
        var batteries = entities.System<BatterySystem>();
        var mechs = entities.System<MechSystem>();
        var containers = entities.System<ContainerSystem>();
        var emp = entities.System<EmpSystem>();
        EntityUid mech = default;
        EntityUid cell = default;

        void AssertPower(MechPowerState expected)
        {
            Assert.That(appearance.TryGetData<MechPowerState>(mech, MechVisuals.Power, out var actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        await server.WaitPost(() => mech = entities.SpawnEntity(prototype, map.GridCoords));
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            cell = entities.GetComponent<MechComponent>(mech).BatterySlot.ContainedEntity!.Value;
            var battery = entities.GetComponent<BatteryComponent>(cell);
            AssertPower(MechPowerState.Powered);
            batteries.SetCharge(cell, battery.MaxCharge * 0.2f);
            AssertPower(MechPowerState.Low);
            batteries.SetCharge(cell, battery.MaxCharge * 0.21f);
            AssertPower(MechPowerState.Powered);
            batteries.SetCharge(cell, 0);
            AssertPower(MechPowerState.Off);
            batteries.SetCharge(cell, battery.MaxCharge * 0.5f);
            AssertPower(MechPowerState.Powered);

            // Moving/deleting a cell must work even without RemoveBattery().
            var slot = entities.GetComponent<MechComponent>(mech).BatterySlot;
            containers.Remove(cell, slot);
            AssertPower(MechPowerState.Off);
            mechs.InsertBattery(mech, cell);
            AssertPower(MechPowerState.Powered);

            emp.DoEmpEffects(mech, 0, 1f);
            AssertPower(MechPowerState.Off);
        });

        await server.WaitRunTicks(90);
        await server.WaitAssertion(() =>
        {
            AssertPower(MechPowerState.Powered);
            mechs.BreakMech(mech);
            AssertPower(MechPowerState.Off);
            batteries.SetCharge(cell, entities.GetComponent<BatteryComponent>(cell).MaxCharge);
            AssertPower(MechPowerState.Off);

            var empty = entities.SpawnEntity("MechSHISuzume", map.GridCoords);
            Assert.That(appearance.TryGetData<MechPowerState>(empty, MechVisuals.Power, out var emptyState), Is.True);
            Assert.That(emptyState, Is.EqualTo(MechPowerState.Off));
        });
        await pair.CleanReturnAsync();
    }
}
