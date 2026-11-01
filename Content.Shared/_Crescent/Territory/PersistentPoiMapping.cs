using System.Numerics;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

/// <summary>
/// A console that lifts its operator's view off their body and over the territory, to build and tear down from
/// the material silo as if mapping.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PersistentPoiMappingConsoleComponent : Component
{
    [DataField]
    public EntProtoId EyePrototype = "PersistentPoiMappingEye";

    // how far past the territory's own tiles the view may wander
    [DataField]
    public float Margin = 8f;

    // how far from the view a change can be made
    [DataField]
    public float Reach = 24f;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PersistentPoiMappingViewerComponent : Component
{
    [AutoNetworkedField]
    public EntityUid Console;

    [AutoNetworkedField]
    public EntityUid Eye;

    [ViewVariables]
    public bool HadFov = true;
}

[Serializable, NetSerializable]
public enum PersistentPoiMappingUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class PersistentPoiMappingOpenMenuMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class PersistentPoiMappingRecenterMessage : BoundUserInterfaceMessage;

public static class PersistentPoiMapping
{
    /// <summary>
    /// Where a builder's reach is measured from: the console's view while they work one, else their own body.
    /// </summary>
    public static EntityCoordinates GetReachOrigin(IEntityManager entMan, EntityUid user, float bodyReach, out float reach)
    {
        if (entMan.TryGetComponent(user, out PersistentPoiMappingViewerComponent? viewer) &&
            entMan.TryGetComponent(viewer.Eye, out TransformComponent? eyeXform) &&
            entMan.TryGetComponent(viewer.Console, out PersistentPoiMappingConsoleComponent? console))
        {
            reach = console.Reach;
            return eyeXform.Coordinates;
        }

        reach = bodyReach;
        return entMan.GetComponent<TransformComponent>(user).Coordinates;
    }

    public static bool IsViewing(IEntityManager entMan, EntityUid user)
    {
        return entMan.TryGetComponent(user, out PersistentPoiMappingViewerComponent? viewer) && entMan.EntityExists(viewer.Eye);
    }
}

/// <summary>
/// Keeps the view over the territory and the operator's hands off everything but the console while they look
/// through it, so the wide view cannot be used to aim, throw or reach.
/// </summary>
public sealed class PersistentPoiMappingViewerSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, ShotAttemptedEvent>(OnShotAttempt);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, ThrowAttemptEvent>(OnThrowAttempt);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, UseAttemptEvent>(OnUseAttempt);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, InteractionAttemptEvent>(OnInteractionAttempt);
        SubscribeLocalEvent<PersistentPoiMappingViewerComponent, PickupAttemptEvent>(OnPickupAttempt);
    }

    private void OnAttackAttempt(Entity<PersistentPoiMappingViewerComponent> ent, ref AttackAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnShotAttempt(Entity<PersistentPoiMappingViewerComponent> ent, ref ShotAttemptedEvent args)
    {
        args.Cancel();
    }

    private void OnThrowAttempt(Entity<PersistentPoiMappingViewerComponent> ent, ref ThrowAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnUseAttempt(Entity<PersistentPoiMappingViewerComponent> ent, ref UseAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnPickupAttempt(Entity<PersistentPoiMappingViewerComponent> ent, ref PickupAttemptEvent args)
    {
        args.Cancel();
    }

    // the console itself stays usable, and so do target-less checks such as its UI messages
    private void OnInteractionAttempt(Entity<PersistentPoiMappingViewerComponent> ent, ref InteractionAttemptEvent args)
    {
        if (args.Target is { } target && target != ent.Comp.Console && target != ent.Owner)
            args.Cancelled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<PersistentPoiMappingViewerComponent>();
        while (query.MoveNext(out var uid, out var viewer))
        {
            // the client only predicts its own view
            if (_net.IsClient && uid != _player.LocalEntity)
                continue;

            Clamp(viewer);
        }
    }

    private void Clamp(PersistentPoiMappingViewerComponent viewer)
    {
        if (!TryComp(viewer.Eye, out TransformComponent? eyeXform) ||
            !TryComp(viewer.Console, out TransformComponent? consoleXform) ||
            consoleXform.GridUid is not { } grid ||
            !TryComp(grid, out MapGridComponent? mapGrid) ||
            !TryComp(viewer.Console, out PersistentPoiMappingConsoleComponent? console))
        {
            return;
        }

        var bounds = mapGrid.LocalAABB.Enlarged(console.Margin);
        var local = Vector2.Transform(_transform.GetWorldPosition(eyeXform), _transform.GetInvWorldMatrix(grid));
        if (bounds.Contains(local) && eyeXform.ParentUid == grid)
            return;

        var clamped = Vector2.Clamp(local, bounds.BottomLeft, bounds.TopRight);
        _transform.SetCoordinates(viewer.Eye, new EntityCoordinates(grid, clamped));

        if (TryComp(viewer.Eye, out PhysicsComponent? body))
            _physics.SetLinearVelocity(viewer.Eye, Vector2.Zero, body: body);
    }
}
