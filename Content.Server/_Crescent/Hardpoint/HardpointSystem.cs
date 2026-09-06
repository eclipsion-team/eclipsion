using Content.Server._Crescent.Hullmods;
using Content.Server.DeviceLinking.Events;
using Content.Server.DeviceLinking.Systems;
using Content.Server.Factory.Components;
using Content.Server.PointCannons;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared.Construction.Components;
using Content.Shared.PointCannons;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Physics;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Hardpoint;

/// <summary>
/// This handles...
/// </summary>
public sealed class HardpointSystem : SharedHardpointSystem
{
    [Dependency] private readonly PointCannonSystem _cannonSystem = default!;
    [Dependency] private readonly DeviceLinkSystem _signalSystem = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    /// <inheritdoc/>
    public override void Initialize()
    {
        // Subscribed on the CANNON, not the hardpoint: a hardpoint destroyed along with the tile under it is
        // gone by the time its gun needs unlinking, and an event raised on a deleted entity reaches nobody.
        SubscribeLocalEvent<HardpointAnchorableOnlyComponent, HardpointCannonDeanchoredEvent>(OnCannonDeanchor);
        SubscribeLocalEvent<HardpointFixedMountComponent, SignalReceivedEvent>(OnSignalReceived);
    }
    private void OnSignalReceived(EntityUid uid, HardpointFixedMountComponent component, ref SignalReceivedEvent args)
    {
        if (!TryComp<HardpointComponent>(uid, out var hard))
            return;
        if (hard.anchoring is null)
            return;
        if (!TryComp<GunComponent>(hard.anchoring.Value, out var gun))
            return;
        if (!TryComp<HardpointAnchorableOnlyComponent>(hard.anchoring.Value, out var anchor) ||
            !IsMounted((hard.anchoring.Value, anchor)))
            return;

        var gridUid = Transform(uid).GridUid;
        if (gridUid != null && HasComp<PacifistShipHullmodComponent>(gridUid))
        {
                return;
        }

        if (args.Port == component.Trigger || args.Port == component.Toggle)
            EntityManager.System<ShipWeaponTargetingSystem>().SetConsole(hard.anchoring.Value, args.Trigger);

        if (args.Port == component.Trigger)
            _gun.AttemptShoot(hard.anchoring.Value, gun);

        if (args.Port == component.Toggle)
        {
            var autoShoot = EnsureComp<AutoShootGunComponent>(hard.anchoring.Value);
            _gun.SetEnabled(hard.anchoring.Value, autoShoot, !autoShoot.Enabled);
        }
    }

    public void OnCannonDeanchor(EntityUid uid, HardpointAnchorableOnlyComponent comp, ref HardpointCannonDeanchoredEvent args)
    {
        StopContinuousFire(args.CannonUid);

        if (!HasComp<PointCannonComponent>(args.CannonUid))
            return;

        _cannonSystem.UnlinkCannon(args.CannonUid);
    }

    private void StopContinuousFire(EntityUid cannonUid)
    {
        if (TryComp<AutoShootGunComponent>(cannonUid, out var autoShoot) && autoShoot.Enabled)
        {
            _gun.SetEnabled(cannonUid, autoShoot, false);
            Dirty(cannonUid, autoShoot);
        }

        if (!TryComp<GunComponent>(cannonUid, out var gun) ||
            !gun.BurstActivated && gun.BurstShotsCount == 0 && gun.ShotCounter == 0)
        {
            return;
        }

        gun.BurstActivated = false;
        gun.BurstShotsCount = 0;
        gun.ShotCounter = 0;
        gun.ShootCoordinates = null;
        gun.Target = null;
        Dirty(cannonUid, gun);
    }

}
