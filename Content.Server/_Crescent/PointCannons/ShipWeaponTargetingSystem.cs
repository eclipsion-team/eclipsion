using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent;
using Content.Shared.Physics;
using Content.Shared.PointCannons;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server.PointCannons;

public sealed class ShipWeaponTargetingSystem : EntitySystem
{
    [Dependency] private readonly PointCannonSystem _cannons = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttles = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<TargetingConsoleComponent, ShipWeaponTargetingModeMessage>(OnTargetingMode);
        SubscribeLocalEvent<ShuttleConsoleComponent, ShipWeaponTargetingModeMessage>(OnShuttleMode);
        SubscribeLocalEvent<GunComponent, AmmoShotEvent>(OnShot);
    }

    public ShipWeaponTargetingMode GetMode(EntityUid console)
    {
        return TryComp<ShipWeaponTargetingComponent>(console, out var targeting)
            ? targeting.Mode
            : ShipWeaponTargetingMode.Walls;
    }

    public void SetConsole(EntityUid gun, EntityUid? console)
    {
        EnsureComp<ShipWeaponTargetingComponent>(gun).Console = console;
    }

    private bool SetMode(EntityUid uid, ShipWeaponTargetingMode mode)
    {
        if (!Enum.IsDefined(mode))
            return false;

        EnsureComp<ShipWeaponTargetingComponent>(uid).Mode = mode;
        return true;
    }

    private void OnTargetingMode(EntityUid uid, TargetingConsoleComponent comp, ShipWeaponTargetingModeMessage args)
    {
        if (SetMode(uid, args.Mode))
            _cannons.UpdateConsoleState(uid, comp);
    }

    private void OnShuttleMode(EntityUid uid, ShuttleConsoleComponent comp, ShipWeaponTargetingModeMessage args)
    {
        if (SetMode(uid, args.Mode))
            _shuttles.UpdateState(uid, comp);
    }

    private void OnShot(EntityUid uid, GunComponent comp, AmmoShotEvent args)
    {
        if (!TryComp<ShipWeaponTargetingComponent>(uid, out var targeting) ||
            targeting.Console is not { } console || !Exists(console))
            return;

        var mode = GetMode(console);
        foreach (var projectile in args.FiredProjectiles)
        {
            if (!HasComp<ProjectileComponent>(projectile))
                continue;

            // Snapshot at launch: changing the console must not retarget rounds already in flight.
            if (mode != ShipWeaponTargetingMode.TilesAndWalls)
                continue;

            var phase = EnsureComp<ProjectilePhasePreventComponent>(projectile);
            phase.TargetTiles = true;
            if (phase.relevantBitmasks == 0)
                phase.relevantBitmasks = (int) (CollisionGroup.Impassable | CollisionGroup.BulletImpassable);
        }
    }
}
