using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Server.Body.Components;
using Content.Server.Chat.Systems;
using Content.Server.Humanoid.Systems;
using Content.Server.Medical;
using Content.Server.Medical.Components;
using Content.Shared._Crescent.Barricades;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Access.Systems;
using Content.Shared.Body.Systems;
using Content.Shared.Medical;
using Content.Server.NPC.Components;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Chat;
using Content.Shared.Clothing.Loadouts.Systems;
using Content.Shared.Damage;
using Content.Shared.Dataset;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Physics;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcTacticalComponent"/>
public sealed partial class NpcTacticalSystem : EntitySystem
{
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HealingSystem _healing = default!;
    [Dependency] private readonly SharedIdCardSystem _idCard = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NpcIffSystem _iff = default!;
    [Dependency] private readonly NpcSquadSystem _squad = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// Anything a round stops on. Walls, windows, airlocks and machines all have it, and so do mobs.
    /// </summary>
    private const CollisionGroup ShotBlockers = CollisionGroup.BulletImpassable;

    /// <summary>
    /// What NPC ranged combat treats as blocking its view of the target, see NPCCombatSystem.Ranged.
    /// </summary>
    private const CollisionGroup SightBlockers = CollisionGroup.Impassable | CollisionGroup.InteractImpassable;

    /// <summary>
    /// How far either side of a tile's centre a shot from it has to clear as well - about as far off the
    /// centre as steering leaves the NPC standing.
    /// </summary>
    private const float ShotSpotMargin = 0.3f;

    /// <summary>
    /// At most this many of the best-scoring tiles get a line-of-fire raycast before the search gives up.
    /// </summary>
    private const int MaxCoverRaycasts = 12;

    /// <summary>
    /// Threats a hiding spot is checked against. Past the nearest few it isn't worth the raycasts.
    /// </summary>
    private const int MaxHidingThreats = 3;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private const string CalloutPrefix = "NpcSoldierCallout";

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<StorageComponent> _storageQuery;
    private EntityQuery<DirectionalBarricadeComponent> _barricadeQuery;

    private readonly List<(float Score, Vector2i Indices)> _candidates = new();
    private readonly HashSet<Vector2i> _occupied = new();

    /// <summary>
    /// Anchored collision layers per tile, for the duration of one search. Every tile gets looked at again
    /// as a neighbour of the eight around it, and the anchored lookup is the expensive part.
    /// </summary>
    private readonly Dictionary<Vector2i, int> _layerCache = new();
    private readonly List<RayCastResults> _hits = new();
    private readonly List<EntityUid> _threats = new();

    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();

        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _storageQuery = GetEntityQuery<StorageComponent>();
        _barricadeQuery = GetEntityQuery<DirectionalBarricadeComponent>();

        // After the loadout, since the supplies go into the storage it puts on them, and after the random name,
        // which goes on its ID card.
        SubscribeLocalEvent<NpcTacticalComponent, MapInitEvent>(OnMapInit,
            after: [typeof(SharedLoadoutSystem), typeof(RandomHumanoidAppearanceSystem)]);
        SubscribeLocalEvent<NpcTacticalComponent, NpcGunReloadStartedEvent>(OnReloadStarted);
        SubscribeLocalEvent<NpcTacticalComponent, NpcBuildBarricadeDoAfterEvent>(OnBarricadeDoAfter);
        SubscribeLocalEvent<TargetingComponent, HealingDoAfterEvent>(OnHealingDoAfter, after: [typeof(HealingSystem)]);

        InitializeMovement();
    }

    private void OnMapInit(Entity<NpcTacticalComponent> ent, ref MapInitEvent args)
    {
        // Staggered so a squad spawned together doesn't chatter together.
        var now = _timing.CurTime;
        ent.Comp.NextIdleChatter = now + ent.Comp.IdleChatterInterval * _random.NextFloat(0.3f, 1.5f);
        ent.Comp.NextBattleCry = now + ent.Comp.BattleCryInterval * _random.NextFloat(0.5f, 1.5f);

        HandOutSupplies(ent);
        LabelIdCard(ent);
    }

    /// <summary>
    /// Puts the NPC's name on the faction ID card its loadout gave it. A player's is filled in as they join;
    /// nothing does that for a card an NPC spawns wearing, so it would read as nobody's.
    /// </summary>
    private void LabelIdCard(EntityUid uid)
    {
        if (_idCard.TryFindIdCard(uid, out var idCard))
            _idCard.TryChangeFullName(idCard, Name(uid), idCard.Comp);
    }

    private void OnReloadStarted(Entity<NpcTacticalComponent> ent, ref NpcGunReloadStartedEvent args)
    {
        TryCallout(ent, NpcCalloutType.Reload, comp: ent.Comp);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;

        var query = EntityQueryEnumerator<NpcTacticalComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            // Someone took this body over, so it talks for itself now.
            if (HasComp<ActorComponent>(uid) || !_mobState.IsAlive(uid))
                continue;

            var inCombat = IsFighting(uid);

            UpdateLead(uid, comp, now);

            if (inCombat && !comp.WasInCombat)
            {
                AlertAllies(uid, comp, now);
                TryCallout(uid, NpcCalloutType.Engage, comp: comp);
                comp.NextBattleCry = now + comp.BattleCryInterval * _random.NextFloat(0.5f, 1.5f);
            }
            else if (inCombat && now >= comp.NextBattleCry)
            {
                TryCallout(uid, NpcCalloutType.BattleCry, comp: comp);
                comp.NextBattleCry = now + comp.BattleCryInterval * _random.NextFloat(0.5f, 1.5f);
            }
            else if (!inCombat && now >= comp.NextIdleChatter)
            {
                TryCallout(uid, NpcCalloutType.Idle, comp: comp);
                comp.NextIdleChatter = now + comp.IdleChatterInterval * _random.NextFloat(0.5f, 1.5f);
            }

            if (!inCombat)
            {
                comp.ClaimedCover = null;
                comp.LineOfFireBlockedSince = null;
                comp.PeekTile = null;
            }

            comp.WasInCombat = inCombat;
        }
    }

    #region Callouts

    /// <summary>
    /// Has the NPC say one of its lines for <paramref name="type"/>, subject to its cooldown and chance
    /// unless <paramref name="force"/> is set.
    /// </summary>
    public bool TryCallout(EntityUid uid, NpcCalloutType type, bool force = false, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        if (!TryGetCalloutDataset(uid, comp, type, out var dataset))
            return false;

        var now = _timing.CurTime;
        if (!force && (now < comp.NextCallout || !_random.Prob(comp.CalloutChance)))
            return false;

        if (HasComp<ActorComponent>(uid) || !_mobState.IsAlive(uid))
            return false;

        if (dataset.Values.Count <= 0)
            return false;

        // Localized datasets number their strings from 1.
        var line = Loc.GetString(dataset.Values.Prefix + _random.Next(1, dataset.Values.Count + 1));
        _chat.TrySendInGameICMessage(uid, line, InGameICChatType.Speak, ChatTransmitRange.Normal);
        comp.NextCallout = now + comp.CalloutCooldown;
        return true;
    }

    /// <summary>
    /// The lines for an occasion: the component's own if it lists any, otherwise the ones for the NPC's
    /// faction (<c>NpcSoldierCallout{Faction}{Type}</c>), otherwise the generic ones
    /// (<c>NpcSoldierCallout{Type}</c>).
    /// </summary>
    /// <remarks>
    /// Going by faction means a soldier gets its side's battle cries without every one of the role x faction
    /// mob prototypes having to restate them - which they would, since the first parent wins every shared
    /// component under multiple inheritance.
    /// </remarks>
    private bool TryGetCalloutDataset(EntityUid uid, NpcTacticalComponent comp, NpcCalloutType type,
        [NotNullWhen(true)] out LocalizedDatasetPrototype? dataset)
    {
        if (comp.Callouts.TryGetValue(type, out var explicitId))
            return _proto.TryIndex(explicitId, out dataset);

        if (TryComp<NpcFactionMemberComponent>(uid, out var factions))
        {
            foreach (var faction in factions.Factions)
            {
                if (_proto.TryIndex($"{CalloutPrefix}{faction}{type}", out dataset))
                    return true;
            }
        }

        return _proto.TryIndex($"{CalloutPrefix}{type}", out dataset);
    }

    #endregion

    #region Line of fire

    /// <summary>
    /// Whether a round from <paramref name="from"/> would reach <paramref name="target"/> without first
    /// hitting a wall, or - when <paramref name="checkFriendlies"/> is set - one of the shooter's own side.
    /// </summary>
    /// <remarks>
    /// Enemies and bystanders in the way don't count as blocking: the round hits someone the NPC was
    /// happy to shoot anyway. Downed mobs don't either, since rounds pass over anyone lying on the floor.
    /// </remarks>
    public bool HasClearShot(EntityUid shooter, MapCoordinates from, EntityUid target, MapCoordinates to, bool checkFriendlies = true)
    {
        if (from.MapId != to.MapId)
            return false;

        var delta = to.Position - from.Position;
        var length = delta.Length();

        if (length < 0.01f)
            return true;

        var ray = new CollisionRay(from.Position, delta / length, (int) ShotBlockers);

        _hits.Clear();
        _hits.AddRange(_physics.IntersectRay(from.MapId, ray, length, shooter, returnOnFirstHit: false));
        _hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        foreach (var hit in _hits)
        {
            var hitEnt = hit.HitEntity;

            if (hitEnt == target)
                return true;

            if (!_physicsQuery.TryComp(hitEnt, out var body) || !body.Hard)
                continue;

            if (_mobQuery.TryComp(hitEnt, out var mobState))
            {
                if (mobState.CurrentState != MobState.Alive)
                    continue;

                if (checkFriendlies && _iff.IsFriendly(shooter, hitEnt))
                    return false;

                continue;
            }

            // A wall, a window, a closed airlock or a machine. Loose things - a crate, a mech - take the
            // round instead, which is no reason not to fire.
            if (body.BodyType == BodyType.Static && !PassesBarricade(hitEnt, from, ray.Direction))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the NPC could see and hit <paramref name="target"/> from anywhere around <paramref name="spot"/>,
    /// not just its exact centre. Steering never puts it dead on a tile - walls it is hugging for cover push
    /// it a few tenths off - and a shot that only just clears a corner from the centre is blocked from where
    /// it actually ends up standing. That left soldiers sat in cover staring at a wall.
    /// </summary>
    public bool HasSolidShot(EntityUid shooter, MapCoordinates spot, EntityUid target, MapCoordinates targetMap, bool checkFriendlies)
    {
        if (!HasClearShot(shooter, spot, target, targetMap, checkFriendlies))
            return false;

        var toTarget = targetMap.Position - spot.Position;
        var distance = toTarget.Length();

        if (distance < 0.01f)
            return true;

        var side = new Vector2(-toTarget.Y, toTarget.X) / distance * ShotSpotMargin;

        if (!HasClearShot(shooter, new MapCoordinates(spot.Position + side, spot.MapId), target, targetMap, checkFriendlies: false) ||
            !HasClearShot(shooter, new MapCoordinates(spot.Position - side, spot.MapId), target, targetMap, checkFriendlies: false))
        {
            return false;
        }

        // And the test its combat actually uses to decide whether it can see the target at all, which goes by
        // what blocks movement rather than what stops rounds.
        return _interaction.InRangeUnobstructed(spot, target, distance + 0.5f, SightBlockers, e => e == shooter);
    }

    /// <summary>
    /// Whether a round fired from <paramref name="from"/> along <paramref name="direction"/> goes through
    /// <paramref name="uid"/> because it's a directional barricade and the shot comes from its protected side.
    /// Same test as <see cref="CrescentBarricadeSystem"/> makes on the actual projectile.
    /// </summary>
    private bool PassesBarricade(EntityUid uid, MapCoordinates from, Vector2 direction)
    {
        if (!_barricadeQuery.TryComp(uid, out var barricade))
            return false;

        var facing = _transform.GetWorldRotation(uid).ToWorldVec();
        if (Vector2.Dot(direction, facing) <= barricade.PassDotThreshold)
            return false;

        var offset = from.Position - _transform.GetWorldPosition(uid);
        return Vector2.Dot(offset, facing) < -barricade.ProtectedSideMargin;
    }

    /// <summary>
    /// Whether the NPC has to wait for its own side to get out of the way. Rounds fired by one with IFF fly
    /// straight through them (<see cref="NpcIffSystem.ShouldPassThrough"/>), so holding fire for a squadmate
    /// only ever cost it shots - a soldier with a friend a step ahead of it would stand there silent.
    /// </summary>
    private bool HoldsFireForFriendlies(EntityUid uid)
    {
        return !HasComp<NpcIffComponent>(uid);
    }

    /// <summary>
    /// Re-checks whether something the round would stop on stands between the NPC and its target, keeping
    /// track of how long it has.
    /// </summary>
    /// <returns>True when the NPC should hold its fire.</returns>
    public bool UpdateLineOfFire(EntityUid uid, EntityUid target, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        var clear = HasClearShot(uid, _transform.GetMapCoordinates(uid), target, _transform.GetMapCoordinates(target),
            HoldsFireForFriendlies(uid));

        if (clear)
        {
            comp.LineOfFireBlockedSince = null;
            return false;
        }

        comp.LineOfFireBlockedSince ??= _timing.CurTime;
        return true;
    }

    /// <summary>
    /// Whether a friendly has been in the way for long enough that the NPC should find another angle.
    /// </summary>
    public bool IsLineOfFireBlocked(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false) || comp.LineOfFireBlockedSince is not { } since)
            return false;

        return _timing.CurTime - since >= comp.BlockedFireTime;
    }

    #endregion

    #region Engagement

    /// <summary>
    /// Points the NPC's ranged combat at <paramref name="target"/>, starting it if it isn't running already.
    /// </summary>
    public NPCRangedCombatComponent StartFiring(EntityUid uid, EntityUid target, float directTargetChance, Angle? rotationSpeed)
    {
        var ranged = EnsureComp<NPCRangedCombatComponent>(uid);
        ranged.Target = target;
        ranged.DirectTargetChance = directTargetChance;

        if (rotationSpeed != null)
            ranged.RotationSpeed = rotationSpeed;

        // Left over from shooting on the way here - a moment with no path, say. Kept, it would fail the next
        // task on its first tick and throw the whole plan away before it fired a shot.
        if (ranged.Status != CombatStatus.Unspecified)
            ranged.Status = CombatStatus.Normal;

        return ranged;
    }

    /// <summary>
    /// Called as the NPC starts shooting from a new position.
    /// </summary>
    /// <param name="peek">Whether it may step out of its position to shoot and duck back - only from cover.</param>
    public void BeginEngagement(EntityUid uid, bool peek = false, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        // Under an attack order it only stops long enough to put some rounds out before pushing on.
        var (min, max) = IsAssaulting(uid)
            ? (comp.AssaultMinHoldTime, comp.AssaultMaxHoldTime)
            : (comp.MinRepositionTime, comp.MaxRepositionTime);

        var spread = (max - min).TotalSeconds;
        comp.EngageUntil = _timing.CurTime + min + TimeSpan.FromSeconds(_random.NextDouble() * spread);
        comp.LostSightSince = null;
        comp.LineOfFireBlockedSince = null;
        ResetPeek(comp, peek);
    }

    /// <summary>
    /// Decides whether the NPC should give up on its current position and replan.
    /// </summary>
    /// <returns>True when it should move.</returns>
    public bool UpdateEngagement(EntityUid uid, bool targetOutOfSight, bool moving, EntityUid? target = null,
        NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        var now = _timing.CurTime;

        // Still on the way to its spot: let it get there before judging it. The clock starts when it gets
        // there, or a long run would use up its whole stay and it would set straight off again on arrival.
        if (moving)
        {
            comp.LostSightSince = null;

            var settle = IsAssaulting(uid) ? comp.AssaultMinHoldTime : comp.MinRepositionTime / 2;
            if (comp.EngageUntil < now + settle)
                comp.EngageUntil = now + settle;

            return false;
        }

        if (targetOutOfSight)
        {
            comp.LostSightSince ??= now;

            if (now - comp.LostSightSince.Value >= comp.LostSightTime)
            {
                MarkReposition(uid, comp);
                return true;
            }
        }
        else
        {
            comp.LostSightSince = null;
        }

        if (IsLineOfFireBlocked(uid, comp))
        {
            MarkReposition(uid, comp);
            return true;
        }

        // Wandered too far from the squad while chasing a good angle.
        if (_squad.TryGetLeash(uid, out var centre, out var range))
        {
            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != centre.MapId || (pos.Position - centre.Position).Length() > range + 3f)
                return true;
        }

        if (now < comp.EngageUntil)
            return false;

        // Dug in behind a barricade facing them: that is the spot, it isn't going anywhere just to keep moving.
        if (target is { } enemy && IsDugInAgainst(uid, enemy))
        {
            comp.EngageUntil = now + comp.MinRepositionTime;
            return false;
        }

        // Been here long enough: somewhere else, so it doesn't just pick the same tile again.
        MarkReposition(uid, comp);
        return true;
    }

    #endregion

    #region Cover

    /// <summary>
    /// Finds a tile to fight <paramref name="target"/> from: next to something that stops rounds from the
    /// target's direction, with a clear shot at it, within the NPC's preferred range band and not too far
    /// from where it stands now.
    /// </summary>
    public bool TryFindCover(EntityUid owner, EntityUid target, float minRange, float maxRange,
        [NotNullWhen(true)] out EntityCoordinates? cover)
    {
        cover = null;

        if (!TryComp<NpcTacticalComponent>(owner, out var comp))
            return false;

        var now = _timing.CurTime;

        if (comp.CachedCoverTarget == target && now - comp.CachedCoverTime < comp.CoverCacheTime)
        {
            cover = comp.CachedCover;
            return cover != null;
        }

        cover = FindCover(owner, comp, target, minRange, maxRange);

        comp.CachedCoverTarget = target;
        comp.CachedCover = cover;
        comp.CachedCoverTime = now;
        comp.ClaimedCover = cover;
        return cover != null;
    }

    private EntityCoordinates? FindCover(EntityUid owner, NpcTacticalComponent comp, EntityUid target, float minRange, float maxRange)
    {
        var xform = Transform(owner);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var ownerMap = _transform.GetMapCoordinates(owner, xform);
        var targetMap = _transform.GetMapCoordinates(target);

        if (ownerMap.MapId != targetMap.MapId)
            return null;

        var invMatrix = _transform.GetInvWorldMatrix(gridUid);
        var targetLocal = Vector2.Transform(targetMap.Position, invMatrix);
        var origin = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var radius = comp.CoverSearchRadius;
        var hasLeash = _squad.TryGetLeash(owner, out var leashCentre, out var leashRange);

        CollectOccupied(owner, comp, gridUid, grid, ownerMap, radius);
        CollectAllyClaims(owner, target, gridUid);
        _candidates.Clear();

        // Moving on from a spot: anywhere near it is marked down, and staying put earns nothing.
        Vector2i? avoid = null;
        if (comp.RepositionFrom is { } from && _timing.CurTime < comp.RepositionAvoidUntil && from.EntityId == gridUid)
            avoid = _map.TileIndicesFor(gridUid, grid, from);

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                var indices = origin + new Vector2i(dx, dy);

                if (_occupied.Contains(indices) || !IsStandable(gridUid, grid, indices))
                    continue;

                var centre = TileCentre(grid, indices);
                var toTarget = targetLocal - centre;
                var range = toTarget.Length();

                if (range > maxRange + 1f || range < minRange * 0.5f)
                    continue;

                if (hasLeash && !InLeash(gridUid, grid, indices, leashCentre, leashRange))
                    continue;

                var coverValue = GetCoverValue(gridUid, grid, indices, toTarget / MathF.Max(range, 0.01f));

                if (coverValue <= 0f)
                    continue;

                var travel = new Vector2(dx, dy).Length();
                var rangePenalty = range < minRange ? minRange - range : range > maxRange ? range - maxRange : 0f;

                var score = coverValue * 3f - travel * 0.35f - rangePenalty * 0.75f;

                // Spread out around the target rather than all shooting from behind the same corner.
                score -= GetFlankPenalty(centre, targetLocal, comp.FlankWeight);

                if (avoid is { } avoidIndices)
                {
                    var off = indices - avoidIndices;
                    if (Math.Abs(off.X) <= 1 && Math.Abs(off.Y) <= 1)
                        score -= 3f;
                }
                // Don't dance between two equally good tiles.
                else if (dx == 0 && dy == 0)
                {
                    score += 1f;
                }

                _candidates.Add((score, indices));
            }
        }

        if (_candidates.Count == 0)
            return null;

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var checkFriendlies = HoldsFireForFriendlies(owner);
        var checks = Math.Min(_candidates.Count, MaxCoverRaycasts);
        for (var i = 0; i < checks; i++)
        {
            var coords = _map.GridTileToLocal(gridUid, grid, _candidates[i].Indices);

            if (HasSolidShot(owner, _transform.ToMapCoordinates(coords), target, targetMap, checkFriendlies))
                return coords;
        }

        return null;
    }

    /// <summary>
    /// Finds the nearest tile none of the NPC's nearby enemies can see, to patch itself up in.
    /// </summary>
    /// <returns>
    /// False only if there are enemies about and nowhere to hide from them. With no enemies around, the
    /// NPC's own tile is as good a place as any.
    /// </returns>
    public bool TryFindHidingSpot(EntityUid owner, float vision, [NotNullWhen(true)] out EntityCoordinates? spot)
    {
        spot = null;

        if (!TryComp<NpcTacticalComponent>(owner, out var comp))
            return false;

        var now = _timing.CurTime;

        if (now - comp.CachedHideTime < comp.CoverCacheTime)
        {
            spot = comp.CachedHide;
            return spot != null;
        }

        spot = FindHidingSpot(owner, comp, vision);
        comp.CachedHide = spot;
        comp.CachedHideTime = now;

        if (spot != null)
            comp.ClaimedCover = spot;

        return spot != null;
    }

    private EntityCoordinates? FindHidingSpot(EntityUid owner, NpcTacticalComponent comp, float vision)
    {
        var xform = Transform(owner);
        var ownerMap = _transform.GetMapCoordinates(owner, xform);

        _threats.Clear();
        foreach (var hostile in _npcFaction.GetNearbyHostiles(owner, vision))
        {
            if (_mobState.IsAlive(hostile))
                _threats.Add(hostile);
        }

        if (_threats.Count == 0)
            return xform.Coordinates;

        _threats.Sort((a, b) =>
            (_transform.GetMapCoordinates(a).Position - ownerMap.Position).LengthSquared()
            .CompareTo((_transform.GetMapCoordinates(b).Position - ownerMap.Position).LengthSquared()));

        if (_threats.Count > MaxHidingThreats)
            _threats.RemoveRange(MaxHidingThreats, _threats.Count - MaxHidingThreats);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var origin = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var radius = comp.CoverSearchRadius;
        var hasLeash = _squad.TryGetLeash(owner, out var leashCentre, out var leashRange);

        CollectOccupied(owner, comp, gridUid, grid, ownerMap, radius);
        _candidates.Clear();

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                var indices = origin + new Vector2i(dx, dy);

                if (_occupied.Contains(indices) || !IsStandable(gridUid, grid, indices))
                    continue;

                if (hasLeash && !InLeash(gridUid, grid, indices, leashCentre, leashRange))
                    continue;

                // Closer is better, and a wall at your back is better still.
                var score = -new Vector2(dx, dy).Length() + GetCoverValue(gridUid, grid, indices, Vector2.Zero) * 0.25f;
                _candidates.Add((score, indices));
            }
        }

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Hiding checks are cheap per threat but there can be a few threats, so look at a few more tiles
        // than cover does before giving up.
        var checks = Math.Min(_candidates.Count, MaxCoverRaycasts * 2);
        for (var i = 0; i < checks; i++)
        {
            var coords = _map.GridTileToLocal(gridUid, grid, _candidates[i].Indices);
            var mapCoords = _transform.ToMapCoordinates(coords);
            var seen = false;

            foreach (var threat in _threats)
            {
                // Friendlies don't hide anyone, so this is purely whether a wall is in the way.
                if (!HasClearShot(threat, _transform.GetMapCoordinates(threat), owner, mapCoords, checkFriendlies: false))
                    continue;

                seen = true;
                break;
            }

            if (!seen)
                return coords;
        }

        return null;
    }

    /// <summary>
    /// Tiles the NPC shouldn't pick: where another mob stands, and what its squadmates have already claimed.
    /// </summary>
    private void CollectOccupied(EntityUid owner, NpcTacticalComponent comp, EntityUid gridUid, MapGridComponent grid,
        MapCoordinates ownerMap, int radius)
    {
        _occupied.Clear();
        _layerCache.Clear();

        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(ownerMap, radius + 2f))
        {
            if (mob.Owner == owner)
                continue;

            _occupied.Add(_map.TileIndicesFor(gridUid, grid, Transform(mob).Coordinates));
        }

        var query = EntityQueryEnumerator<NpcTacticalComponent, TransformComponent>();
        while (query.MoveNext(out var other, out var otherComp, out var otherXform))
        {
            if (other == owner || otherComp.ClaimedCover is not { } claim || otherXform.GridUid != gridUid)
                continue;

            if (claim.EntityId != gridUid)
                continue;

            _occupied.Add(_map.TileIndicesFor(gridUid, grid, claim));
        }
    }

    /// <summary>
    /// Floor something can stand on: not space, nothing solid anchored on it.
    /// </summary>
    private bool IsStandable(EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        if (!_map.TryGetTileRef(gridUid, grid, indices, out var tile) || tile.Tile.IsEmpty)
            return false;

        return (GetAnchoredLayers(gridUid, grid, indices) & (int) CollisionGroup.MobMask) == 0;
    }

    /// <summary>
    /// How well the neighbours of a tile shield it from <paramref name="threatDir"/>. A solid neighbour
    /// straight towards the threat is the best there is; one off to the side still breaks up the silhouette.
    /// With a zero direction this just counts the solid neighbours.
    /// </summary>
    private float GetCoverValue(EntityUid gridUid, MapGridComponent grid, Vector2i indices, Vector2 threatDir)
    {
        var value = 0f;

        for (var nx = -1; nx <= 1; nx++)
        {
            for (var ny = -1; ny <= 1; ny++)
            {
                if (nx == 0 && ny == 0)
                    continue;

                if ((GetAnchoredLayers(gridUid, grid, indices + new Vector2i(nx, ny)) & (int) ShotBlockers) == 0)
                    continue;

                if (threatDir == Vector2.Zero)
                {
                    value += 1f;
                    continue;
                }

                var dir = Vector2.Normalize(new Vector2(nx, ny));
                var dot = Vector2.Dot(dir, threatDir);
                var cardinal = nx == 0 || ny == 0;

                if (dot > 0.5f)
                    value += cardinal ? 2f : 1.5f;
                else if (dot > -0.1f)
                    value += 0.5f;
            }
        }

        return value;
    }

    private int GetAnchoredLayers(EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        if (_layerCache.TryGetValue(indices, out var cached))
            return cached;

        var layers = 0;
        var enumerator = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, indices);

        while (enumerator.MoveNext(out var ent))
        {
            if (!_physicsQuery.TryComp(ent.Value, out var body) || !body.CanCollide || !body.Hard)
                continue;

            layers |= body.CollisionLayer;
        }

        _layerCache[indices] = layers;
        return layers;
    }

    private static Vector2 TileCentre(MapGridComponent grid, Vector2i indices)
    {
        return (indices + new Vector2(0.5f, 0.5f)) * grid.TileSize;
    }

    private bool InLeash(EntityUid gridUid, MapGridComponent grid, Vector2i indices, MapCoordinates centre, float range)
    {
        var tileMap = _transform.ToMapCoordinates(_map.GridTileToLocal(gridUid, grid, indices));
        return tileMap.MapId == centre.MapId && (tileMap.Position - centre.Position).Length() <= range;
    }

    #endregion

    #region Healing

    /// <summary>
    /// Fraction of the way to going critical the NPC is, 0 being unhurt.
    /// </summary>
    public bool TryGetDamageFraction(EntityUid uid, out float fraction)
    {
        fraction = 0f;

        if (!TryComp<DamageableComponent>(uid, out var damageable) ||
            !_thresholds.TryGetThresholdForState(uid, MobState.Critical, out var crit) ||
            crit.Value <= FixedPoint2.Zero)
        {
            return false;
        }

        fraction = (float) (damageable.TotalDamage / crit.Value);
        return true;
    }

    /// <summary>
    /// Whether the NPC is hurt enough to stop and treat itself, and carries something that would help.
    /// </summary>
    public bool NeedsHealing(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false) || !_mobState.IsAlive(uid))
            return false;

        if (!comp.Healing && _timing.CurTime < comp.NextHealAttempt)
            return false;

        if (!TryGetDamageFraction(uid, out var fraction))
            return false;

        var threshold = comp.Healing ? comp.HealStopThreshold : comp.HealThreshold;

        return fraction >= threshold && (TryFindHealingItem(uid, uid, out _) || CanUseMedipen(uid, uid, comp));
    }

    /// <summary>
    /// Starts treating the NPC with the best thing it has on it.
    /// </summary>
    public bool TryStartSelfHeal(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        // Something to take the edge off first if it's bad, then the dressings.
        var injected = TryUseMedipen(uid, uid, comp);

        if (!TryFindHealingItem(uid, uid, out var item) ||
            !TryComp<HealingComponent>(item, out var healing) ||
            !TryTreat(uid, uid, item.Value, healing))
        {
            if (injected)
                return true;

            StopHealing(uid, comp, failed: true);
            return false;
        }

        if (!comp.Healing)
            TryCallout(uid, NpcCalloutType.Heal, comp: comp);

        comp.Healing = true;
        return true;
    }

    /// <summary>
    /// Applies <paramref name="item"/> to <paramref name="patient"/>, aimed at whichever body part it helps most.
    /// </summary>
    /// <remarks>
    /// Treatment lands on the part the healer is targeting. A player picks it; an NPC left on its default
    /// torso would keep bandaging an unhurt chest while the patient bled out of a leg.
    /// </remarks>
    public bool TryTreat(EntityUid healer, EntityUid patient, EntityUid item, HealingComponent healing)
    {
        AimAtWorstPart(healer, patient, healing);
        return _healing.TryHeal(item, healer, patient, healing);
    }

    private void AimAtWorstPart(EntityUid healer, EntityUid patient, HealingComponent healing)
    {
        if (!TryComp<TargetingComponent>(healer, out var targeting))
            return;

        TargetBodyPart? best = null;
        var bestScore = 0f;

        foreach (var (partId, part) in _body.GetBodyChildren(patient))
        {
            if (!TryComp<DamageableComponent>(partId, out var partDamage))
                continue;

            var score = 0f;
            foreach (var (type, amount) in healing.Damage.DamageDict)
            {
                if (amount < FixedPoint2.Zero && partDamage.Damage.DamageDict.TryGetValue(type, out var have))
                    score += (float) FixedPoint2.Min(have, -amount);
            }

            if (score <= bestScore || _body.GetTargetBodyPart(part) is not { } target)
                continue;

            bestScore = score;
            best = target;
        }

        if (best == null || targeting.Target == best.Value)
            return;

        targeting.Target = best.Value;
        Dirty(healer, targeting);
    }

    /// <summary>
    /// NPC healers do one application at a time instead of letting the do-after repeat on its own, so every
    /// application gets re-aimed at the part that needs it most by then.
    /// </summary>
    private void OnHealingDoAfter(Entity<TargetingComponent> ent, ref HealingDoAfterEvent args)
    {
        if (HasComp<NpcTacticalComponent>(args.User))
            args.Repeat = false;
    }

    public bool IsTreating(EntityUid uid)
    {
        return IsBusy(uid);
    }

    /// <summary>
    /// Whether the NPC is in the middle of some do-after - bandaging, injecting, building.
    /// </summary>
    public bool IsBusy(EntityUid uid)
    {
        return HasComp<ActiveDoAfterComponent>(uid);
    }

    public void StopHealing(EntityUid uid, NpcTacticalComponent? comp = null, bool failed = false)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        comp.Healing = false;

        if (failed)
            comp.NextHealAttempt = _timing.CurTime + comp.HealRetryDelay;
    }

    /// <summary>
    /// Picks the medical item <paramref name="carrier"/> has on it that treats the most of what is actually
    /// wrong with <paramref name="patient"/>. Bleeding always comes first, since that one gets worse on its own.
    /// </summary>
    public bool TryFindHealingItem(EntityUid carrier, EntityUid patient, [NotNullWhen(true)] out EntityUid? item)
    {
        item = null;

        if (!TryComp<DamageableComponent>(patient, out var damageable))
            return false;

        var bleeding = TryComp<BloodstreamComponent>(patient, out var bloodstream) && bloodstream.BleedAmount > 0;
        var bestScore = 0f;

        foreach (var candidate in EnumerateCarried(carrier))
        {
            if (!TryComp<HealingComponent>(candidate, out var healing))
                continue;

            if (TryComp<StackComponent>(candidate, out var stack) && _stack.GetCount(candidate, stack) <= 0)
                continue;

            if (healing.DamageContainers is { } containers &&
                damageable.DamageContainerID is { } container &&
                !containers.Contains(container))
            {
                continue;
            }

            var score = 0f;

            foreach (var (type, amount) in healing.Damage.DamageDict)
            {
                if (amount >= FixedPoint2.Zero || !damageable.Damage.DamageDict.TryGetValue(type, out var have))
                    continue;

                score += (float) FixedPoint2.Min(have, -amount);
            }

            if (bleeding && healing.BloodlossModifier < 0)
                score += 100f;

            if (score <= bestScore)
                continue;

            bestScore = score;
            item = candidate;
        }

        return item != null;
    }

    /// <summary>
    /// Everything the NPC carries: hands, worn items, whatever is inside worn storage, and whatever is inside
    /// storage kept in there - a medkit in a backpack. No deeper than that.
    /// </summary>
    public IEnumerable<EntityUid> EnumerateCarried(EntityUid uid)
    {
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            yield return held;
        }

        if (!_inventory.TryGetContainerSlotEnumerator(uid, out var slots))
            yield break;

        while (slots.NextItem(out var worn))
        {
            yield return worn;

            if (!_storageQuery.TryComp(worn, out var storage))
                continue;

            foreach (var stored in storage.Container.ContainedEntities.ToArray())
            {
                yield return stored;

                if (!_storageQuery.TryComp(stored, out var inner))
                    continue;

                foreach (var nested in inner.Container.ContainedEntities.ToArray())
                {
                    yield return nested;
                }
            }
        }
    }

    #endregion
}
