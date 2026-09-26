using Content.Server._Crescent.NPC;
using Content.Server.NPC.Components;
using Content.Shared.CombatMode;
using Content.Shared.Interaction;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCCombatSystem
{
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly NpcGunHandlingSystem _npcGun = default!; // Crescent
    [Dependency] private readonly NpcTacticalSystem _npcTactical = default!; // Crescent

    private EntityQuery<CombatModeComponent> _combatQuery;
    private EntityQuery<NPCSteeringComponent> _steeringQuery;
    private EntityQuery<RechargeBasicEntityAmmoComponent> _rechargeQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    // TODO: Don't predict for hitscan
    private const float ShootSpeed = 20f;

    /// <summary>
    /// Cooldown on raycasting to check LOS.
    /// </summary>
    public const float UnoccludedCooldown = 0.2f;

    private void InitializeRanged()
    {
        _combatQuery = GetEntityQuery<CombatModeComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _rechargeQuery = GetEntityQuery<RechargeBasicEntityAmmoComponent>();
        _steeringQuery = GetEntityQuery<NPCSteeringComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<NPCRangedCombatComponent, ComponentStartup>(OnRangedStartup);
        SubscribeLocalEvent<NPCRangedCombatComponent, ComponentShutdown>(OnRangedShutdown);
    }

    private void OnRangedStartup(EntityUid uid, NPCRangedCombatComponent component, ComponentStartup args)
    {
        if (TryComp<CombatModeComponent>(uid, out var combat))
        {
            _combat.SetInCombatMode(uid, true, combat);
        }
        else
        {
            component.Status = CombatStatus.Unspecified;
        }
    }

    private void OnRangedShutdown(EntityUid uid, NPCRangedCombatComponent component, ComponentShutdown args)
    {
        if (TryComp<CombatModeComponent>(uid, out var combat))
        {
            _combat.SetInCombatMode(uid, false, combat);
        }
    }

    private void UpdateRanged(float frameTime)
    {
        var query = EntityQueryEnumerator<NPCRangedCombatComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (comp.Status == CombatStatus.Unspecified)
                continue;

            if (_steeringQuery.TryGetComponent(uid, out var steering) && steering.Status == SteeringStatus.NoPath)
            {
                comp.Status = CombatStatus.TargetUnreachable;
                comp.ShootAccumulator = 0f;
                continue;
            }

            if (!_xformQuery.TryGetComponent(comp.Target, out var targetXform) ||
                !_physicsQuery.TryGetComponent(comp.Target, out var targetBody))
            {
                comp.Status = CombatStatus.TargetUnreachable;
                comp.ShootAccumulator = 0f;
                continue;
            }

            if (targetXform.MapID != xform.MapID)
            {
                comp.Status = CombatStatus.TargetUnreachable;
                comp.ShootAccumulator = 0f;
                continue;
            }

            if (_combatQuery.TryGetComponent(uid, out var combatMode))
            {
                _combat.SetInCombatMode(uid, true, combatMode);
            }

            if (!_gun.TryGetGun(uid, out var gunUid, out var gun))
            {
                comp.Status = CombatStatus.NoWeapon;
                comp.ShootAccumulator = 0f;
                continue;
            }

            // Crescent: wield it, close the bolt, work the action - the manual steps a Hullrot gun needs
            // before it will fire that an NPC has no other way to perform. While that is in progress the
            // NPC keeps tracking and facing its target below, it just doesn't get to shoot; the ammo
            // check is skipped too, since a gun halfway through a magazine change reads as empty.
            var gunReady = _npcGun.TryReadyGun(uid, gunUid);

            if (gunReady)
            {
                var ammoEv = new GetAmmoCountEvent();
                RaiseLocalEvent(gunUid, ref ammoEv);
                if (ammoEv.Count == 0)
                {
                    // Recharging then?
                    if (_rechargeQuery.HasComponent(gunUid))
                    {
                        continue;
                    }

                    comp.Status = CombatStatus.Unspecified;
                    comp.ShootAccumulator = 0f;
                    continue;
                }
            }

            var worldPos = _transform.GetWorldPosition(xform);
            var targetPos = _transform.GetWorldPosition(targetXform);


            comp.LOSAccumulator -= frameTime;

            // We'll work out the projected spot of the target and shoot there instead of where they are.
            var distance = (targetPos - worldPos).Length();
            var oldInLos = comp.TargetInLOS;

            // TODO: Should be doing these raycasts in parallel
            // Ideally we'd have 2 steps, 1. to go over the normal details for shooting and then 2. to handle beep / rotate / shoot
            if (comp.LOSAccumulator < 0f)
            {
                comp.LOSAccumulator += UnoccludedCooldown;

                // For consistency with NPC steering.
                var collisionGroup = comp.UseOpaqueForLOSChecks ? CollisionGroup.Opaque : (CollisionGroup.Impassable | CollisionGroup.InteractImpassable);
                comp.TargetInLOS = _interaction.InRangeUnobstructed(uid, comp.Target, distance + 0.1f, collisionGroup);

                // Crescent: soldier AI doesn't waste rounds on cover that sight passes over but bullets don't.
                comp.ShotBlocked = comp.TargetInLOS && _npcTactical.UpdateLineOfFire(uid, comp.Target);
            }

            if (!comp.TargetInLOS)
            {
                comp.ShootAccumulator = 0f;
                comp.Status = CombatStatus.NotInSight;

                if (TryComp(uid, out steering))
                {
                    steering.ForceMove = true;
                }
                continue;
            }

            if (!oldInLos && comp.SoundTargetInLOS != null)
            {
                _audio.PlayPvs(comp.SoundTargetInLOS, uid);
            }

            comp.ShootAccumulator += frameTime;

            if (comp.ShootAccumulator < comp.ShootDelay)
            {
                continue;
            }

            var mapVelocity = targetBody.LinearVelocity;
            var targetSpot = targetPos + mapVelocity * distance / ShootSpeed;

            // If we have a max rotation speed then do that.
            var goalRotation = (targetSpot - worldPos).ToWorldAngle();
            var rotationSpeed = comp.RotationSpeed;

            if (!_rotate.TryRotateTo(uid, goalRotation, frameTime, comp.AccuracyThreshold, rotationSpeed?.Theta ?? double.MaxValue, xform))
            {
                continue;
            }

            // TODO: LOS
            // TODO: Ammo checks
            // TODO: Burst fire
            // TODO: Cycling
            // Max rotation speed

            // TODO: Check if we can face

            // Crescent: facing and LOS are up to date now, but the gun still isn't ready to fire.
            if (!gunReady)
                continue;

            // Crescent: hold fire until the way clears or the NPC finds a better angle.
            if (comp.ShotBlocked)
                continue;

            if (!Enabled || !_gun.CanShoot(gun))
                continue;

            EntityCoordinates targetCordinates;

            if (_mapManager.TryFindGridAt(xform.MapID, targetPos, out var gridUid, out var mapGrid))
                targetCordinates = new EntityCoordinates(gridUid, _mapManager.WorldToLocal(gridUid, mapGrid, targetSpot));
            else
                targetCordinates = new EntityCoordinates(xform.MapUid!.Value, targetSpot);

            comp.Status = CombatStatus.Normal;

            // Crescent: continue, not return - returning here ended the whole loop, so one NPC waiting on
            // its fire rate stopped every NPC after it in the query from shooting that tick.
            if (gun.NextFire > _timing.CurTime)
            {
                continue;
            }

            gun.Target = comp.AlwaysDirectTargets || _random.Prob(comp.DirectTargetChance) ? comp.Target : null;

            _gun.AttemptShoot(uid, gunUid, gun, targetCordinates);
        }
    }
}
