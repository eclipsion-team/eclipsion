using Content.Shared._Crescent.Pulling;
using Content.Shared.Mech.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Server.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests.Mech;

[TestFixture]
public sealed class MechDragTest
{
    [Test]
    public async Task MechBlocksMobsAndDragsHeavily()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.EntMan;
        var pulling = entities.System<PullingSystem>();
        var containers = entities.System<ContainerSystem>();
        EntityUid mech = default;
        EntityUid human = default;

        await server.WaitPost(() =>
        {
            mech = entities.SpawnEntity("MechSHISuzume", map.GridCoords);
            human = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(1f, 0f)));
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var mechBody = entities.GetComponent<PhysicsComponent>(mech);
            var humanBody = entities.GetComponent<PhysicsComponent>(human);

            // Two KinematicControllers never collide, so the mech must be something else, and the layers must meet.
            Assert.That(mechBody.BodyType, Is.EqualTo(BodyType.Dynamic));
            Assert.That(humanBody.CollisionMask & mechBody.CollisionLayer, Is.Not.Zero);

            var originalMass = mechBody.Mass;
            var drag = entities.GetComponent<HeavyPullableComponent>(mech);
            Assert.That(originalMass, Is.GreaterThan(drag.DragMass));

            Assert.That(pulling.TryStartPull(human, mech), Is.True);
            Assert.That(mechBody.Mass, Is.EqualTo(drag.DragMass).Within(1f));
            Assert.That(entities.GetComponent<MovementSpeedModifierComponent>(human).WalkSpeedModifier,
                Is.LessThanOrEqualTo(drag.DragWalkSpeedMultiplier));

            Assert.That(pulling.TryStopPull(mech, user: human, ignoreGrab: true), Is.True);
            Assert.That(mechBody.Mass, Is.EqualTo(originalMass).Within(1f));
            Assert.That(drag.AppliedDensityScale, Is.EqualTo(1f));

            // Nobody drags a mech while somebody is driving it.
            var pilot = entities.SpawnEntity("MobHuman", map.GridCoords);
            containers.Insert(pilot, entities.GetComponent<MechComponent>(mech).PilotSlot);
            Assert.That(pulling.CanPull(human, mech), Is.False);
        });

        await pair.CleanReturnAsync();
    }
}
