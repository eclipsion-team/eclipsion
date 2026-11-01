using System.Numerics;
using Content.Server._Crescent.Diplomacy;
using Content.Server._Crescent.ProximityFuse;
using Content.Server._Mono.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.PointCannons;
using Content.Server.Power.EntitySystems;
using Content.Server.Radio.EntitySystems;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.HeatSeeking;
using Content.Shared._Crescent.Territory;
using Content.Shared.PointCannons;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// Runs the hands-off guard mode for persistent territory point-defence consoles. Opening the targeting UI gives
/// the operator exclusive fire control; closing it returns every linked point cannon to automatic watch.
/// The exception is <see cref="TerritoryMissileInterceptorComponent"/> guns, which drop everything for an incoming
/// missile whether anyone is at the console or not.
/// </summary>
public sealed class TerritoryAutoDefenseSystem : EntitySystem
{
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromSeconds(15);

    private static readonly SoundPathSpecifier LockWarningSound =
        new("/Audio/Machines/warning_buzzer.ogg", AudioParams.Default.WithVolume(-4f));

    [Dependency] private readonly DiplomacySystem _iffDiplomacy = default!;
    [Dependency] private readonly RatDiplomacySystem _factionDiplomacy = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly FactionMachineSystem _factionMachines = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly PointCannonSystem _pointCannons = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedShuttleSystem _shuttle = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan MissileScanInterval = TimeSpan.FromSeconds(0.25);

    private static readonly TimeSpan InterceptorHold = TimeSpan.FromSeconds(0.5);

    // how wide of the hull's bounding circle a rocket may pass and still be shot at
    private const float MissileMissMargin = 15f;

    private EntityQuery<HeatSeekingComponent> _seekerQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<TerritoryMissileInterceptorComponent> _interceptorQuery;

    private readonly HashSet<Entity<ProximityFuseTargetComponent>> _missileScratch = new();

    private readonly Dictionary<EntityUid, TimeSpan> _nextGridWarning = new();
    private readonly Dictionary<EntityUid, TimeSpan> _nextNeutralRadioWarning = new();
    // Not readonly: FindGridsIntersecting takes it by ref so it can grow the list for us.
    private List<Entity<MapGridComponent>> _gridScratch = new();

    private enum TargetDisposition
    {
        Ignore,
        WarnNeutral,
        EngageNeutral,
        EngageHostile,
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGridComponent, EntityTerminatingEvent>(OnGridTerminating);

        _seekerQuery = GetEntityQuery<HeatSeekingComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _interceptorQuery = GetEntityQuery<TerritoryMissileInterceptorComponent>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<TerritoryAutoDefenseComponent, TargetingConsoleComponent>();
        while (query.MoveNext(out var consoleUid, out var defense, out var console))
        {
            var consoleXform = Transform(consoleUid);
            if (consoleXform.GridUid is not { } ownGrid ||
                !this.IsPowered(consoleUid, EntityManager) ||
                _iffDiplomacy.GetGridFaction(ownGrid) is not { } ownFaction ||
                !string.Equals(_factionMachines.GetFaction(consoleUid), ownFaction, StringComparison.Ordinal))
            {
                ClearTarget(defense);
                defense.Missile = null;
                continue;
            }

            // Keep the ordinary manual console link list authoritative too. This is idempotent and catches
            // mapped hardpoints that finished anchoring after the console itself initialized. Done with auto fire
            // off as well, or the missile guns would never be linked.
            if (now >= defense.NextLinkRefresh)
            {
                defense.NextLinkRefresh = now + TimeSpan.FromSeconds(MathF.Max(0.25f, defense.ScanInterval));
                _pointCannons.LinkAllCannonsToConsole(consoleUid, console);
            }

            // missiles first and always: shooting down what is locked on us needs no war and no operator
            InterceptMissiles((consoleUid, console), defense, ownGrid, ownFaction, now);

            // The normal targeting console system owns the guns for as long as at least one operator has its UI
            // open. This also prevents an automatic shot from racing a manual order on the same server tick.
            if (_ui.IsUiOpen(consoleUid, TargetingConsoleUiKey.Key) ||
                AnotherConsoleOwnsAutomaticControl(consoleUid, ownGrid, ownFaction) ||
                TryComp<PersistentPoiGridComponent>(ownGrid, out var territory) && !territory.AutoDefense)
            {
                ClearTarget(defense);
                continue;
            }

            if (now >= defense.NextScan)
            {
                defense.NextScan = now + TimeSpan.FromSeconds(MathF.Max(0.25f, defense.ScanInterval));

                var previousGrid = defense.TargetGrid;
                defense.Target = FindNearestTarget(
                    consoleUid,
                    ownGrid,
                    ownFaction,
                    defense,
                    now,
                    out var acquiredTargetGrid);
                defense.TargetGrid = acquiredTargetGrid;

                if (acquiredTargetGrid is { } acquiredGrid && acquiredGrid != previousGrid)
                    WarnTargetGrid(acquiredGrid, now);
            }

            if (defense.Target is not { } target ||
                !TryValidateEngagementTarget(consoleUid, ownGrid, ownFaction, defense, target, out var targetGrid))
            {
                ClearTarget(defense);
                continue;
            }

            defense.TargetGrid = targetGrid;

            FireTerritoryWeapons((consoleUid, console), ownGrid, ownFaction, target, targetGrid, now);
        }
    }

    private void InterceptMissiles(
        Entity<TargetingConsoleComponent> console,
        TerritoryAutoDefenseComponent defense,
        EntityUid ownGrid,
        string ownFaction,
        TimeSpan now)
    {
        // one console per grid answers for the guns
        if (LowerConsoleOnGrid(console.Owner, ownGrid, ownFaction) || !HasInterceptor(console.Comp))
        {
            defense.Missile = null;
            return;
        }

        var origin = _transform.GetMapCoordinates(console.Owner);
        if (now >= defense.NextMissileScan)
        {
            defense.NextMissileScan = now + MissileScanInterval;
            defense.Missile = FindMissile(origin, defense.MissileRange, ownGrid, ownFaction);
        }

        if (defense.Missile is not { } missile ||
            !IsMissileThreat(missile, origin, defense.MissileRange, ownGrid, ownFaction))
        {
            defense.Missile = null;
            return;
        }

        if (!console.Comp.CannonGroups.TryGetValue("all", out var cannons))
            return;

        var missilePosition = _transform.GetWorldPosition(missile);
        var relativeVelocity = _physics.GetMapLinearVelocity(missile) - _physics.GetMapLinearVelocity(ownGrid);

        foreach (var weaponUid in cannons)
        {
            if (!_interceptorQuery.TryComp(weaponUid, out var interceptor) ||
                !TryComp<PointCannonComponent>(weaponUid, out var cannon) ||
                !TryComp<GunComponent>(weaponUid, out var gun) ||
                !TryComp<TransformComponent>(weaponUid, out var weaponXform) ||
                weaponXform.GridUid != ownGrid ||
                !cannon.LinkedConsoleIds.Contains(console.Owner))
            {
                continue;
            }

            var aimPosition = PredictIntercept(_transform.GetWorldPosition(weaponXform), missilePosition,
                relativeVelocity, gun.ProjectileSpeedModified);

            if (!_pointCannons.CanAimAt(weaponUid, aimPosition, weaponXform) ||
                NonHostileGridInLineOfFire(weaponUid, ownGrid, EntityUid.Invalid, ownFaction, missile))
            {
                continue;
            }

            interceptor.EngagedUntil = now + InterceptorHold;
            _pointCannons.TryFireCannon(weaponUid, aimPosition, weaponXform, gun, cannon);
        }
    }

    // only a console that would itself answer counts, or a dead one silences the rest
    private bool LowerConsoleOnGrid(EntityUid consoleUid, EntityUid ownGrid, string ownFaction)
    {
        var consoles = EntityQueryEnumerator<TerritoryAutoDefenseComponent, TargetingConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var otherUid, out _, out var other, out var otherXform))
        {
            if (otherXform.GridUid == ownGrid &&
                otherUid.Id < consoleUid.Id &&
                this.IsPowered(otherUid, EntityManager) &&
                string.Equals(_factionMachines.GetFaction(otherUid), ownFaction, StringComparison.Ordinal) &&
                HasInterceptor(other))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasInterceptor(TargetingConsoleComponent console)
    {
        if (!console.CannonGroups.TryGetValue("all", out var cannons))
            return false;

        foreach (var cannon in cannons)
        {
            if (_interceptorQuery.HasComp(cannon))
                return true;
        }

        return false;
    }

    private EntityUid? FindMissile(MapCoordinates origin, float range, EntityUid ownGrid, string ownFaction)
    {
        _missileScratch.Clear();
        _lookup.GetEntitiesInRange(origin, range, _missileScratch, LookupFlags.Dynamic | LookupFlags.Sundries);

        EntityUid? nearest = null;
        var nearestDistance = float.MaxValue;
        var center = _physics.GetWorldAABB(ownGrid).Center;

        foreach (var (candidate, _) in _missileScratch)
        {
            if (!IsMissileThreat(candidate, origin, range, ownGrid, ownFaction))
                continue;

            var distance = Vector2.DistanceSquared(_transform.GetWorldPosition(candidate), center);
            if (distance >= nearestDistance)
                continue;

            nearest = candidate;
            nearestDistance = distance;
        }

        return nearest;
    }

    private bool IsMissileThreat(EntityUid missile, MapCoordinates origin, float range, EntityUid ownGrid,
        string ownFaction)
    {
        // thrusters are proximity fuse targets too, so it has to be a round in flight
        if (TerminatingOrDeleted(missile) || !_projectileQuery.TryComp(missile, out var projectile))
            return false;

        // over our own plating our bolts pass straight through it
        var xform = Transform(missile);
        if (xform.GridUid == ownGrid)
            return false;

        var position = _transform.GetMapCoordinates(missile, xform);
        if (position.MapId != origin.MapId ||
            Vector2.DistanceSquared(position.Position, origin.Position) > range * range ||
            FiredByFriend(projectile, ownGrid, ownFaction))
        {
            return false;
        }

        if (_seekerQuery.TryComp(missile, out var seeker) &&
            seeker.TargetEntity is { } locked &&
            !TerminatingOrDeleted(locked) &&
            Transform(locked).GridUid == ownGrid)
        {
            return true;
        }

        // a dumb rocket, or a seeker after somebody else, only counts while it is about to hit us
        var bounds = _physics.GetWorldAABB(ownGrid);
        var offset = position.Position - bounds.Center;
        var velocity = _physics.GetMapLinearVelocity(missile) - _physics.GetMapLinearVelocity(ownGrid);
        var closing = -Vector2.Dot(offset, velocity);
        if (closing <= 0f)
            return false;

        var miss = offset + velocity * (closing / velocity.LengthSquared());
        var radius = bounds.Size.Length() / 2f + MissileMissMargin;
        return miss.LengthSquared() <= radius * radius;
    }

    private bool FiredByFriend(ProjectileComponent projectile, EntityUid ownGrid, string ownFaction)
    {
        var source = projectile.Weapon ?? projectile.Shooter;
        if (source is not { } sourceUid || TerminatingOrDeleted(sourceUid) ||
            Transform(sourceUid).GridUid is not { } sourceGrid)
        {
            return false;
        }

        if (sourceGrid == ownGrid)
            return true;

        return _iffDiplomacy.GetGridFaction(sourceGrid) is { } faction &&
               (faction == ownFaction || _factionDiplomacy.GetRelation(ownFaction, faction) == FactionRelation.Alliance);
    }

    private void OnGridTerminating(Entity<MapGridComponent> ent, ref EntityTerminatingEvent args)
    {
        _nextGridWarning.Remove(ent.Owner);
        _nextNeutralRadioWarning.Remove(ent.Owner);
    }

    private bool AnotherConsoleOwnsAutomaticControl(EntityUid consoleUid, EntityUid ownGrid, string ownFaction)
    {
        var consoles = EntityQueryEnumerator<TerritoryAutoDefenseComponent, TargetingConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var otherUid, out _, out _, out var otherXform))
        {
            if (otherUid == consoleUid || otherXform.GridUid != ownGrid)
                continue;

            // Any manual operator pauses the whole battery. With nobody operating, the lowest entity UID that can
            // actually fire is the sole automatic controller, so two mapped consoles cannot issue competing orders
            // and an unpowered one does not leave the guns idle.
            if (_ui.IsUiOpen(otherUid, TargetingConsoleUiKey.Key))
                return true;

            if (otherUid.Id < consoleUid.Id &&
                this.IsPowered(otherUid, EntityManager) &&
                string.Equals(_factionMachines.GetFaction(otherUid), ownFaction, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private EntityUid? FindNearestTarget(
        EntityUid consoleUid,
        EntityUid ownGrid,
        string ownFaction,
        TerritoryAutoDefenseComponent defense,
        TimeSpan now,
        out EntityUid? targetGrid)
    {
        EntityUid? nearest = null;
        targetGrid = null;
        var nearestDistance = float.MaxValue;
        var origin = _transform.GetMapCoordinates(consoleUid);

        foreach (var (candidate, _) in
                 _lookup.GetEntitiesInRange<ShipNpcTargetComponent>(origin, defense.Range))
        {
            var disposition = ClassifyTarget(
                consoleUid,
                ownGrid,
                ownFaction,
                defense,
                candidate,
                out var candidateGrid);

            // Only traffic outside the exclusion zone is warned off it. Anything already inside is being shot
            // at, and gets the far louder lock warning instead.
            if (disposition is TargetDisposition.WarnNeutral)
                WarnNeutralGrid(consoleUid, candidateGrid, defense, now);

            if (disposition is not (TargetDisposition.EngageNeutral or TargetDisposition.EngageHostile))
                continue;

            var distance = Vector2.DistanceSquared(
                _transform.GetWorldPosition(consoleUid),
                _transform.GetWorldPosition(candidate));
            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = candidate;
            targetGrid = candidateGrid;
        }

        return nearest;
    }

    private bool TryValidateEngagementTarget(
        EntityUid consoleUid,
        EntityUid ownGrid,
        string ownFaction,
        TerritoryAutoDefenseComponent defense,
        EntityUid target,
        out EntityUid targetGrid)
    {
        var disposition = ClassifyTarget(consoleUid, ownGrid, ownFaction, defense, target, out targetGrid);
        return disposition is TargetDisposition.EngageNeutral or TargetDisposition.EngageHostile;
    }

    private TargetDisposition ClassifyTarget(
        EntityUid consoleUid,
        EntityUid ownGrid,
        string ownFaction,
        TerritoryAutoDefenseComponent defense,
        EntityUid target,
        out EntityUid targetGrid)
    {
        targetGrid = default;
        if (!TryComp<ShipNpcTargetComponent>(target, out var targetComp) ||
            targetComp.NeedPower && !this.IsPowered(target, EntityManager))
        {
            return TargetDisposition.Ignore;
        }

        var targetXform = Transform(target);
        if (targetXform.GridUid is not { } grid || grid == ownGrid)
            return TargetDisposition.Ignore;

        var consoleMap = _transform.GetMapCoordinates(consoleUid);
        var targetMap = _transform.GetMapCoordinates(target);
        if (consoleMap.MapId != targetMap.MapId)
            return TargetDisposition.Ignore;

        var distanceSquared = Vector2.DistanceSquared(consoleMap.Position, targetMap.Position);
        if (distanceSquared > defense.Range * defense.Range)
            return TargetDisposition.Ignore;

        // Never open fire on a hull flying no diplomatic faction at all - derelicts, unaligned civilian traffic
        // and anything parked without a transponder. Which factions may HOLD territory is a separate question:
        // filtering targets by that list too left a battery ignoring a Minutemen or Militia raider its owner was
        // openly at war with, purely because that faction cannot raise a standard of its own.
        if (_iffDiplomacy.GetGridFaction(grid) is not { } targetFaction)
            return TargetDisposition.Ignore;

        var relation = _factionDiplomacy.GetRelation(ownFaction, targetFaction);
        if (relation == FactionRelation.Alliance)
            return TargetDisposition.Ignore;

        targetGrid = grid;
        if (relation == FactionRelation.War)
            return TargetDisposition.EngageHostile;

        // With warOnly enabled the battery holds fire on everything short of a declared war, so it has no
        // exclusion zone to warn anybody off. Broadcasting a perimeter threat it will never carry out put a
        // radio call on the Common channel for every non-allied hull in acquisition range, every fifteen
        // seconds, from every territory in the sector. Disabling warOnly opts into the real exclusion zone.
        var warOnly = defense.WarOnly &&
                      !(TryComp<PersistentPoiGridComponent>(ownGrid, out var territory) && territory.ExclusionZone);
        if (warOnly)
        {
            targetGrid = default;
            return TargetDisposition.Ignore;
        }

        var neutralRange = Math.Clamp(defense.NeutralEngagementRange, 0f, defense.Range);
        return distanceSquared <= neutralRange * neutralRange
            ? TargetDisposition.EngageNeutral
            : TargetDisposition.WarnNeutral;
    }

    private void FireTerritoryWeapons(
        Entity<TargetingConsoleComponent> console,
        EntityUid ownGrid,
        string ownFaction,
        EntityUid target,
        EntityUid targetGrid,
        TimeSpan now)
    {
        // The console already keeps the list of guns bound to it, refreshed on every scan by
        // LinkAllCannonsToConsole. Walking every PointCannon in the world instead ran over every ship gun in the
        // sector once per tick per battery, to end up firing the handful mounted on one outpost.
        if (!console.Comp.CannonGroups.TryGetValue("all", out var cannons) || cannons.Count == 0)
            return;

        var targetPosition = _transform.GetWorldPosition(target);
        var ownVelocity = _physics.GetMapLinearVelocity(ownGrid);
        var targetVelocity = _physics.GetMapLinearVelocity(targetGrid);
        var relativeVelocity = targetVelocity - ownVelocity;

        foreach (var weaponUid in cannons)
        {
            // The group list is only ever added to, so a gun that has since been unlinked, unmounted or carried
            // off the grid can still be sitting in it.
            if (!TryComp<PointCannonComponent>(weaponUid, out var cannon) ||
                !TryComp<GunComponent>(weaponUid, out var gun) ||
                !TryComp<TransformComponent>(weaponUid, out var weaponXform) ||
                weaponXform.GridUid != ownGrid ||
                !cannon.LinkedConsoleIds.Contains(console.Owner) ||
                _interceptorQuery.TryComp(weaponUid, out var interceptor) && interceptor.EngagedUntil > now)
            {
                continue;
            }

            var weaponPosition = _transform.GetWorldPosition(weaponXform);
            var aimPosition = PredictIntercept(weaponPosition, targetPosition, relativeVelocity,
                gun.ProjectileSpeedModified);

            if (!_pointCannons.CanAimAt(weaponUid, aimPosition, weaponXform) ||
                NonHostileGridInLineOfFire(weaponUid, ownGrid, targetGrid, ownFaction, target))
            {
                continue;
            }

            _pointCannons.TryFireCannon(weaponUid, aimPosition, weaponXform, gun, cannon);
        }
    }

    private bool NonHostileGridInLineOfFire(
        EntityUid sourceUid,
        EntityUid ownGrid,
        EntityUid targetGrid,
        string ownFaction,
        EntityUid target)
    {
        var start = _transform.GetMapCoordinates(sourceUid);
        var end = _transform.GetMapCoordinates(target);
        if (start.MapId == MapId.Nullspace || start.MapId != end.MapId)
            return false;

        var segment = end.Position - start.Position;
        var segmentLengthSquared = segment.LengthSquared();
        if (segmentLengthSquared < 0.01f)
            return false;

        var bounds = new Box2(Vector2.Min(start.Position, end.Position), Vector2.Max(start.Position, end.Position))
            .Enlarged(1f);
        _gridScratch.Clear();
        _map.FindGridsIntersecting(start.MapId, bounds, ref _gridScratch, approx: true, includeMap: false);

        foreach (var grid in _gridScratch)
        {
            if (grid.Owner == ownGrid || grid.Owner == targetGrid)
                continue;

            if (_iffDiplomacy.GetGridFaction(grid.Owner) is not { } gridFaction ||
                _factionDiplomacy.GetRelation(ownFaction, gridFaction) == FactionRelation.War)
            {
                continue;
            }

            var gridBounds = _physics.GetWorldAABB(grid.Owner);
            var center = gridBounds.Center;
            var projection = Math.Clamp(Vector2.Dot(center - start.Position, segment) / segmentLengthSquared, 0f, 1f);
            var closest = start.Position + segment * projection;
            var radiusSquared = gridBounds.Size.LengthSquared() / 4f;

            // The bounding circle is deliberately conservative: holding fire slightly early is preferable to
            // punching through an allied vessel with a large-calibre station gun.
            if (Vector2.DistanceSquared(center, closest) <= radiusSquared)
                return true;
        }

        return false;
    }

    private static Vector2 PredictIntercept(
        Vector2 origin,
        Vector2 target,
        Vector2 relativeVelocity,
        float projectileSpeed)
    {
        if (projectileSpeed <= 0f)
            return target;

        var displacement = target - origin;
        var a = relativeVelocity.LengthSquared() - projectileSpeed * projectileSpeed;
        var b = 2f * Vector2.Dot(displacement, relativeVelocity);
        var c = displacement.LengthSquared();
        float time;

        if (MathF.Abs(a) < 0.0001f)
        {
            time = MathF.Abs(b) < 0.0001f ? 0f : -c / b;
        }
        else
        {
            var discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
                return target;

            var root = MathF.Sqrt(discriminant);
            var first = (-b - root) / (2f * a);
            var second = (-b + root) / (2f * a);
            time = first > 0f && second > 0f ? MathF.Min(first, second) : MathF.Max(first, second);
        }

        return time > 0f ? target + relativeVelocity * time : target;
    }

    private void WarnTargetGrid(EntityUid targetGrid, TimeSpan now)
    {
        if (_nextGridWarning.TryGetValue(targetGrid, out var nextWarning) && now < nextWarning)
            return;

        _nextGridWarning[targetGrid] = now + WarningCooldown;
        var message = Loc.GetString("territory-auto-defense-lock-warning");

        var actors = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (actors.MoveNext(out var actorUid, out var actor, out var xform))
        {
            if (xform.GridUid != targetGrid)
                continue;

            _popup.PopupEntity(message, actorUid, actor.PlayerSession, PopupType.LargeCaution);
            _audio.PlayGlobal(LockWarningSound, actor.PlayerSession);
        }
    }

    private void WarnNeutralGrid(
        EntityUid consoleUid,
        EntityUid targetGrid,
        TerritoryAutoDefenseComponent defense,
        TimeSpan now)
    {
        if (_nextNeutralRadioWarning.TryGetValue(targetGrid, out var nextWarning) && now < nextWarning)
            return;

        _nextNeutralRadioWarning[targetGrid] = now + WarningCooldown;
        var vessel = _shuttle.GetIFFLabel(targetGrid) ?? MetaData(targetGrid).EntityName;

        // Quote the perimeter this battery actually holds. A distance hardcoded into the string goes stale the
        // moment a mapper tunes neutralEngagementRange, and then the warning misstates it in both directions.
        var clearance = MathF.Round(Math.Clamp(defense.NeutralEngagementRange, 0f, defense.Range));
        var message = Loc.GetString(
            "territory-auto-defense-neutral-radio-warning",
            ("vessel", vessel),
            ("range", clearance));
        _radio.SendRadioMessage(consoleUid, message, "Common", consoleUid);
    }

    private static void ClearTarget(TerritoryAutoDefenseComponent defense)
    {
        defense.Target = null;
        defense.TargetGrid = null;
    }
}
