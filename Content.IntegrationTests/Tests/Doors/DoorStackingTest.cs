using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Doors.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Prying.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Doors;

public sealed class DoorStackingTest : InteractionTest
{
    [Test]
    [TestCase("BlastDoorOpen")]
    [TestCase("ShuttersNormalOpen")]
    [TestCase("Firelock")]
    public async Task ClosesOverClosedAirlock(string proto)
    {
        var doors = SEntMan.System<DoorSystem>();
        EntityUid airlock = default;
        EntityUid over = default;

        await Server.WaitPost(() =>
        {
            var coords = SEntMan.GetCoordinates(TargetCoords);
            airlock = SEntMan.SpawnAtPosition("Airlock", coords);
            over = SEntMan.SpawnAtPosition(proto, coords);
        });
        await RunTicks(5);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<DoorComponent>(airlock).State, Is.EqualTo(DoorState.Closed));
            Assert.That(doors.TryClose(over), Is.True);
        });

        await RunSeconds(3);

        Assert.That(SEntMan.GetComponent<DoorComponent>(over).State, Is.EqualTo(DoorState.Closed));
        Assert.That(SEntMan.GetComponent<DoorComponent>(airlock).State, Is.EqualTo(DoorState.Closed));
    }

    [Test]
    public async Task FirelockOpensWithOneClick()
    {
        await SpawnTarget("Firelock");
        ToggleNeedPower();
        var doors = SEntMan.System<DoorSystem>();
        var uid = STarget!.Value;
        var door = SEntMan.GetComponent<DoorComponent>(uid);

        await Server.WaitAssertion(() => Assert.That(doors.TryClose(uid, door), Is.True));
        await RunSeconds(2);
        Assert.That(door.State, Is.EqualTo(DoorState.Closed));

        // empty hand click
        Assert.That(Hands.ActiveHandEntity, Is.Null);
        await Interact();
        await RunSeconds(2);
        Assert.That(door.State, Is.EqualTo(DoorState.Open));

        await Server.WaitAssertion(() => Assert.That(doors.TryClose(uid, door), Is.True));
        await RunSeconds(2);
        Assert.That(door.State, Is.EqualTo(DoorState.Closed));

        // E key
        await Activate();
        await RunSeconds(2);
        Assert.That(door.State, Is.EqualTo(DoorState.Open));
    }

    [Test]
    public async Task UnpoweredFirelockPriesFaster()
    {
        await SpawnTarget("Firelock");
        var uid = STarget!.Value;

        float unpowered = 0, powered = 0;
        await Server.WaitAssertion(() =>
        {
            var ev = new GetPryTimeModifierEvent(SPlayer);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref ev);
            unpowered = ev.BaseTime * ev.PryTimeModifier;
        });

        ToggleNeedPower();
        await RunSeconds(1);

        await Server.WaitAssertion(() =>
        {
            var ev = new GetPryTimeModifierEvent(SPlayer);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref ev);
            powered = ev.BaseTime * ev.PryTimeModifier;
        });

        Assert.That(unpowered, Is.EqualTo(powered * 0.5f).Within(0.01f));
    }
}
