using System.Linq;
using System.Numerics;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Content.Shared.Damage;
using Content.Shared.Explosion;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server.Abilities.Psionics;

/// <summary>
/// Implements the defense tree's standing dome: an area barrier that shelters everyone inside it
/// and fails once it has soaked enough.
///
/// The dome has no fixtures. A physics wall would need doors for the people it is protecting and
/// would shove anyone standing on its edge when it went up, so coverage is a range scan instead and
/// the barrier works by taking a share of the damage its occupants receive.
///
/// Anything that arrives under its own power - a bullet, a thrown grenade - is stopped outright at
/// the rim rather than shared out, unless whoever sent it is standing under the dome themselves.
/// Occupants shoot and throw out freely; nothing gets in.
/// </summary>
public sealed class AegisDomeSystem : EntitySystem
{
    private const string DomePrototype = "PsionicAegisDome";
    private const string ImpactPrototype = "EffectPsionicAegisImpact";
    private const string ShatterPrototype = "EffectPsionicAegisShatter";

    /// <summary>
    /// Coverage is rechecked on its own cadence. People do not cross a 2.5m dome in a single tick,
    /// and a scan per dome per tick would buy nothing for it.
    /// </summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(0.2);

    /// <summary>
    /// Occupants leave coverage a little outside the radius they gained it at, so someone standing
    /// exactly on the boundary does not flicker in and out every scan.
    /// </summary>
    private const float ReleaseMargin = 0.35f;

    private static readonly TimeSpan ImpactCooldown = TimeSpan.FromSeconds(0.35);

    /// <summary>
    /// Integrity billed for catching a thrown object. A thrown thing carries no damage figure worth
    /// reading before it lands, so it costs a flat, small amount.
    /// </summary>
    private const float ThrownInterceptCost = 5f;

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPsionicAbilitiesSystem _psionics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ThrownItemSystem _thrownItem = default!;

    private TimeSpan _nextScan;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PsionicAegisDomeActionEvent>(OnAegisDome);
        SubscribeLocalEvent<AegisDomeComponent, MapInitEvent>(OnDomeMapInit);
        SubscribeLocalEvent<AegisDomeComponent, ComponentShutdown>(OnDomeShutdown);
        SubscribeLocalEvent<AegisShelteredComponent, DamageModifyEvent>(OnShelteredDamage);
        SubscribeLocalEvent<AegisShelteredComponent, GetExplosionResistanceEvent>(OnShelteredExplosion);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;

        // A round restart rewinds CurTime, which would otherwise park the next scan in the future
        // and stop every dome covering anyone for the rest of the process.
        if (_nextScan > now + ScanInterval)
            _nextScan = now;

        var rescan = now >= _nextScan;
        if (rescan)
            _nextScan = now + ScanInterval;

        var query = EntityQueryEnumerator<AegisDomeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var dome, out var xform))
        {
            if (now >= dome.ExpiresAt)
            {
                // Running out is not the same as being broken: no shatter, no noise.
                QueueDel(uid);
                continue;
            }

            // Unlike coverage this cannot run on the slow cadence: a rifle round crosses a 2.5m
            // dome in a handful of ticks, and a fifth of a second is long enough for one to pass
            // clean through and hit somebody on the far side.
            Intercept((uid, dome), xform);

            if (rescan)
                Rescan((uid, dome), xform);
        }
    }

    /// <summary>
    /// Stops anything flying in from outside at the barrier, and bills the dome for it.
    /// </summary>
    private void Intercept(Entity<AegisDomeComponent> dome, TransformComponent xform)
    {
        if (dome.Comp.Integrity <= 0)
            return;

        var origin = _transform.GetMapCoordinates(dome.Owner, xform);
        var found = new HashSet<EntityUid>();
        _lookup.GetEntitiesInRange(origin.MapId, origin.Position, dome.Comp.Radius, found);

        foreach (var candidate in found)
        {
            if (Deleted(candidate))
                continue;

            if (TryComp<ProjectileComponent>(candidate, out var projectile))
            {
                if (SentFromInside(dome, projectile.Shooter))
                    continue;

                // The whole round is stopped, so the whole round is what it costs.
                SpendIntegrity(dome, MathF.Max(1f, (float) projectile.Damage.GetTotal()));
                QueueDel(candidate);

                // Whatever is left in this volley belongs to the far side of a dome that no longer
                // exists. Carrying on would shatter it once per remaining round.
                if (dome.Comp.Integrity <= 0)
                    return;

                continue;
            }

            if (!TryComp<ThrownItemComponent>(candidate, out var thrown)
                || thrown.Landed
                || SentFromInside(dome, thrown.Thrower))
            {
                continue;
            }

            // Dropped at the rim rather than deleted: the dome is a wall, not an incinerator, and
            // whatever was lobbed at it still belongs to whoever lobbed it.
            _physics.SetLinearVelocity(candidate, Vector2.Zero);
            _physics.SetAngularVelocity(candidate, 0f);
            _thrownItem.StopThrow(candidate, thrown);
            SpendIntegrity(dome, ThrownInterceptCost);

            if (dome.Comp.Integrity <= 0)
                return;
        }
    }

    /// <summary>
    /// Whether whoever launched something is standing under this dome. The barrier only faces
    /// outwards, so people inside can still shoot and throw out of it.
    /// </summary>
    private bool SentFromInside(Entity<AegisDomeComponent> dome, EntityUid? source) =>
        source is { } sender && (sender == dome.Comp.Caster || dome.Comp.Covered.Contains(sender));

    private void OnDomeMapInit(EntityUid uid, AegisDomeComponent component, MapInitEvent args)
    {
        component.ExpiresAt = _timing.CurTime + component.Lifetime;
        component.Integrity = component.MaxIntegrity;
        Dirty(uid, component);
    }

    private void OnAegisDome(PsionicAegisDomeActionEvent args)
    {
        if (args.Handled || !_psionics.OnAttemptPowerUse(args.Performer, "aegis dome", true))
            return;

        var dome = Spawn(DomePrototype, Transform(args.Performer).Coordinates);
        var comp = EnsureComp<AegisDomeComponent>(dome);

        // Integrity and expiry are set by map init; the power only has to say who raised it.
        comp.Caster = args.Performer;
        Dirty(dome, comp);

        // The dome travels with the Psion holding it up. Parented rather than made a follower: the
        // follower system hangs OrbitVisuals on anything it moves, which would set the barrier
        // spinning around its caster.
        _transform.SetParent(dome, args.Performer);
        _transform.SetLocalPosition(dome, Vector2.Zero);

        // Cover immediately: waiting for the next scan would leave everyone the dome was raised for
        // unprotected for a fifth of a second, which is a lot of bullets.
        Rescan((dome, comp), Transform(dome));

        _popup.PopupEntity(
            Loc.GetString("psionic-aegis-dome-raised"),
            args.Performer,
            args.Performer,
            PopupType.Medium);

        _psionics.LogPowerUsed(args.Performer, "aegis dome", 7, 10);
        args.Handled = true;
    }

    /// <summary>
    /// The barrier takes its share of the hit and pays for it out of its own integrity. Reducing
    /// damage without spending integrity would make the dome an unbreakable 40 percent armour
    /// bonus, so the two always move together.
    /// </summary>
    private void OnShelteredDamage(
        EntityUid uid,
        AegisShelteredComponent component,
        DamageModifyEvent args)
    {
        if (!TryComp<AegisDomeComponent>(component.Dome, out var dome) || dome.Integrity <= 0)
            return;

        var incoming = (float) args.Damage.GetTotal();
        if (incoming <= 0)
            return;

        // A hit bigger than what is left does not get fully absorbed - the dome only ever stops as
        // much as it can still pay for, and the overflow lands on whoever is standing there.
        var absorbed = MathF.Min(incoming * dome.Absorption, dome.Integrity);
        args.Damage *= 1f - absorbed / incoming;

        SpendIntegrity((component.Dome, dome), absorbed);
    }

    private void OnShelteredExplosion(
        EntityUid uid,
        AegisShelteredComponent component,
        ref GetExplosionResistanceEvent args)
    {
        if (!TryComp<AegisDomeComponent>(component.Dome, out var dome) || dome.Integrity <= 0)
            return;

        args.DamageCoefficient *= 1f - dome.Absorption;

        // Resistance is queried before the damage is known and can be queried more than once for a
        // single blast, so the charge is billed at a flat share and throttled - otherwise one
        // grenade could bill the dome half a dozen times and drop it instantly.
        if (_timing.CurTime < dome.NextImpact)
            return;

        SpendIntegrity((component.Dome, dome), dome.MaxIntegrity * 0.2f);
    }

    private void SpendIntegrity(Entity<AegisDomeComponent> dome, float amount)
    {
        if (amount <= 0)
            return;

        dome.Comp.Integrity = MathF.Max(0f, dome.Comp.Integrity - amount);
        Dirty(dome.Owner, dome.Comp);

        if (_timing.CurTime >= dome.Comp.NextImpact)
        {
            dome.Comp.NextImpact = _timing.CurTime + ImpactCooldown;
            Spawn(ImpactPrototype, _transform.GetMapCoordinates(dome.Owner));
        }

        if (dome.Comp.Integrity > 0)
            return;

        Shatter(dome);
    }

    public void Shatter(Entity<AegisDomeComponent> dome)
    {
        Spawn(ShatterPrototype, _transform.GetMapCoordinates(dome.Owner));

        if (!Deleted(dome.Comp.Caster))
        {
            _popup.PopupEntity(
                Loc.GetString("psionic-aegis-dome-shattered"),
                dome.Comp.Caster,
                dome.Comp.Caster,
                PopupType.LargeCaution);
        }

        QueueDel(dome.Owner);
    }

    /// <summary>
    /// Brings one dome's occupant list in line with who is actually standing under it.
    /// </summary>
    private void Rescan(Entity<AegisDomeComponent> dome, TransformComponent xform)
    {
        var origin = _transform.GetMapCoordinates(dome.Owner, xform);
        var radius = dome.Comp.Radius;

        foreach (var covered in dome.Comp.Covered.ToArray())
        {
            if (Deleted(covered) || !HasComp<AegisShelteredComponent>(covered))
            {
                dome.Comp.Covered.Remove(covered);
                continue;
            }

            if (!InRange(covered, origin, radius + ReleaseMargin))
                Uncover(dome, covered);
        }

        var found = new HashSet<EntityUid>();
        _lookup.GetEntitiesInRange(origin.MapId, origin.Position, radius, found);

        foreach (var candidate in found)
        {
            // People only. Sheltering every crate and puddle in the bubble would burn the dome's
            // integrity on scenery.
            if (Deleted(candidate)
                || !HasComp<MobStateComponent>(candidate)
                || HasComp<AegisShelteredComponent>(candidate))
            {
                continue;
            }

            var sheltered = AddComp<AegisShelteredComponent>(candidate);
            sheltered.Dome = dome.Owner;
            Dirty(candidate, sheltered);

            dome.Comp.Covered.Add(candidate);
        }
    }

    private void Uncover(Entity<AegisDomeComponent> dome, EntityUid covered)
    {
        dome.Comp.Covered.Remove(covered);
        RemComp<AegisShelteredComponent>(covered);
    }

    private bool InRange(EntityUid uid, MapCoordinates origin, float radius)
    {
        var xform = Transform(uid);
        if (xform.MapID != origin.MapId)
            return false;

        return (_transform.GetWorldPosition(xform) - origin.Position).LengthSquared() <= radius * radius;
    }

    /// <summary>
    /// A dome that expires, shatters or is deleted by an admin must not leave people believing they
    /// are still covered.
    /// </summary>
    private void OnDomeShutdown(EntityUid uid, AegisDomeComponent component, ComponentShutdown args)
    {
        foreach (var covered in component.Covered.ToArray())
        {
            if (!Deleted(covered))
                RemComp<AegisShelteredComponent>(covered);
        }

        component.Covered.Clear();
    }
}
