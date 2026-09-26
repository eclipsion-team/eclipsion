using Content.Server.Atmos.EntitySystems;
using Content.Server.Body.Systems;
using Content.Server.Mech.Systems;
using Content.Shared.Atmos;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Mech;

[TestFixture]
public sealed class MechLifeSupportTest
{
    [TestCase("MechCMMDeputyBattery")] // airtight
    [TestCase("MechRipleyBattery")] // open canopy
    public async Task PilotBreathesStandardAirInVacuum(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.EntMan;
        var mechs = entities.System<MechSystem>();
        var atmos = entities.System<AtmosphereSystem>();
        var respirator = entities.System<RespiratorSystem>();
        EntityUid mech = default;
        EntityUid pilot = default;

        await server.WaitPost(() =>
        {
            mech = entities.SpawnEntity(prototype, map.GridCoords);
            pilot = entities.SpawnEntity("MobHuman", map.GridCoords);
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            Assert.That(atmos.GetContainingMixture(pilot)?.Pressure ?? 0f, Is.LessThan(Atmospherics.HazardLowPressure),
                "The test map should be a vacuum.");
            Assert.That(mechs.TryInsert(mech, pilot), Is.True);

            // Repeated breaths never deplete the cockpit.
            for (var i = 0; i < 20; i++)
            {
                respirator.Inhale(pilot);
                respirator.Exhale(pilot);
            }

            var air = atmos.GetContainingMixture(pilot);
            Assert.That(air, Is.Not.Null);
            Assert.That(air!.Pressure, Is.EqualTo(Atmospherics.OneAtmosphere).Within(1f));
            Assert.That(air.Temperature, Is.EqualTo(Atmospherics.T20C).Within(0.1f));
            Assert.That(air.GetMoles(Gas.Oxygen) / air.TotalMoles, Is.EqualTo(Atmospherics.OxygenStandard).Within(0.01f));
            Assert.That(air.GetMoles(Gas.CarbonDioxide), Is.Zero);
            Assert.That(respirator.CanMetabolizeInhaledAir(pilot), Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
