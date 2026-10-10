using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Body.Systems;
using Content.Server.Destructible;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Shared.Body.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;

namespace Content.Server._Crescent.NuclearBlast;

public sealed partial class NuclearBlastSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly BodySystem _body = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly HashSet<EntityUid> _targets = new();
    private readonly List<Entity<BodyComponent>> _bodies = new();

    /// <summary>
    /// Detonates <paramref name="uid"/> as a nuclear blast, if it is one.
    /// </summary>
    /// <returns>
    /// False if the entity has no <see cref="NuclearBlastComponent"/>, so the caller can fall back to a regular
    /// explosion. True otherwise, including when the blast already went off.
    /// </returns>
    public bool TryDetonate(EntityUid uid, EntityUid? user = null, NuclearBlastComponent? blast = null)
    {
        if (!Resolve(uid, ref blast, logMissing: false))
            return false;

        if (blast.Detonated)
            return true;

        blast.Detonated = true;

        var epicenter = _transform.GetMapCoordinates(uid);

        // The fireball, sound and hull damage. This only queues the explosion, so it is processed after the sweep below.
        if (TryComp<ExplosiveComponent>(uid, out var explosive))
            _explosion.TriggerExplosive(uid, explosive, radius: blast.Radius, user: user);

        if (epicenter.MapId != MapId.Nullspace)
            Annihilate(uid, epicenter, blast.Radius);

        return true;
    }

    private void Annihilate(EntityUid source, MapCoordinates epicenter, float radius)
    {
        // Gather everything before touching any of it. Gibbing spawns giblets and organs, and those are the meat that
        // is meant to be left behind.
        _targets.Clear();
        _bodies.Clear();
        _lookup.GetEntitiesInRange(epicenter.MapId, epicenter.Position, radius, _targets, LookupFlags.Uncontained);

        // Bodies are found through containers too, so someone hiding in a locker or a mech is gibbed instead of being
        // quietly deleted along with it. Anything else in a container goes when its container does. Not a lookup: over
        // an area this size the lookup only searches the containers of bodies it already found.
        var bodies = EntityQueryEnumerator<BodyComponent, TransformComponent>();
        while (bodies.MoveNext(out var uid, out var body, out var xform))
        {
            if (xform.MapID == epicenter.MapId
                && (_transform.GetWorldPosition(xform) - epicenter.Position).LengthSquared() <= radius * radius)
            {
                _bodies.Add((uid, body));
            }
        }

        var gibbed = 0;
        foreach (var body in _bodies)
        {
            var uid = body.Owner;
            if (IsImmune(uid) || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
                continue;

            // Gibbing drops whatever they were wearing or holding on the floor; it is meant to go with them.
            foreach (var item in _inventory.GetHandOrInventoryEntities(uid).ToList())
            {
                QueueDel(item);
            }

            _adminLogger.Add(LogType.Gib, LogImpact.Extreme,
                $"{ToPrettyString(uid):player} was gibbed by the nuclear blast of {ToPrettyString(source):entity} at {epicenter:coordinates}");
            _body.GibBody(uid, gibOrgans: true, body: body.Comp);
            gibbed++;
        }

        var erased = 0;
        foreach (var target in _targets)
        {
            if (IsImmune(target) || TerminatingOrDeleted(target) || EntityManager.IsQueuedForDeletion(target))
                continue;

            // Things that are not items, mobs or destructible are either meant to be indestructible (map boundaries,
            // indestructible walls) or are not physical at all (spawn points, audio, effects).
            if (!HasComp<ItemComponent>(target)
                && !HasComp<DestructibleComponent>(target)
                && !HasComp<MobStateComponent>(target))
            {
                continue;
            }

            QueueDel(target);
            erased++;
        }

        _adminLogger.Add(LogType.Explosion, LogImpact.Extreme,
            $"{ToPrettyString(source):entity} went off as a nuclear blast at {epicenter:coordinates} with radius {radius}, gibbing {gibbed} bodies and erasing {erased} entities");
    }

    /// <summary>
    /// Ghosts, admins in godmode, round-critical objectives and anything FTL arrivals already refuse to crush.
    /// </summary>
    private bool IsImmune(EntityUid uid)
    {
        return HasComp<NuclearBlastImmuneComponent>(uid)
               || HasComp<FTLSmashImmuneComponent>(uid)
               || HasComp<GodmodeComponent>(uid)
               || HasComp<GhostComponent>(uid);
    }
}
