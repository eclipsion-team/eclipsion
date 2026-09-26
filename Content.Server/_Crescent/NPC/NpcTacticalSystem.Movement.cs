using System.Linq;
using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Events;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Damage;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: everything that keeps a soldier on its feet between the big decisions - stepping out of cover to
/// shoot and ducking back, actually moving when it repositions, spreading out around a target with its
/// squadmates, following up on where an enemy was last seen or shot from, calling nearby friends over, and
/// looking around instead of staring at a wall while it waits.
/// </summary>
public sealed partial class NpcTacticalSystem
{
    [Dependency] private readonly HTNSystem _htn = default!;

    /// <summary>
    /// How close to the peek tile counts as being out on it.
    /// </summary>
    private const float PeekArriveDistance = 0.15f;

    /// <summary>
    /// How close to a lead the NPC has to get before it stops and looks around.
    /// </summary>
    public const float SearchArriveRange = 1.5f;

    /// <summary>
    /// Tries at reaching one lead before the NPC gives up on it.
    /// </summary>
    private const int MaxSearchAttempts = 3;

    /// <summary>
    /// A lead that moves further than this counts as a new one, and gets its tries back.
    /// </summary>
    private const float NewLeadDistance = 2.5f;

    /// <summary>
    /// Getting shot doesn't update a lead more often than this, so a burst of fire isn't a burst of lookups.
    /// </summary>
    private static readonly TimeSpan DamageLeadCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A friend's lead doesn't replace one of the NPC's own that is younger than this.
    /// </summary>
    private static readonly TimeSpan FreshLeadTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the NPC sits in cover without sight of its target before leaning out to find it.
    /// </summary>
    private static readonly TimeSpan BlindPeekDelay = TimeSpan.FromSeconds(0.4);

    private static readonly TimeSpan MinLookTime = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan MaxLookTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Other soldiers' claimed cover against the target the current cover search is for, grid-local.
    /// </summary>
    private readonly List<Vector2> _allyClaims = new();

    private void InitializeMovement()
    {
        SubscribeLocalEvent<NpcTacticalComponent, NPCSteeringEvent>(OnSteering);
        SubscribeLocalEvent<NpcTacticalComponent, DamageChangedEvent>(OnDamageChanged);
    }

    private bool IsFighting(EntityUid uid)
    {
        return HasComp<NPCRangedCombatComponent>(uid) || HasComp<NPCMeleeCombatComponent>(uid);
    }

    #region Leads

    /// <summary>
    /// Keeps the NPC's idea of where its enemy is up to date while it fights, and forgets old ones.
    /// </summary>
    private void UpdateLead(EntityUid uid, NpcTacticalComponent comp, TimeSpan now)
    {
        EntityUid? target = null;
        var seen = false;

        if (TryComp<NPCRangedCombatComponent>(uid, out var ranged))
        {
            target = ranged.Target;
            seen = ranged.TargetInLOS;
        }
        else if (TryComp<NPCMeleeCombatComponent>(uid, out var melee))
        {
            target = melee.Target;
            seen = true;
        }

        if (target is { } enemy && !TerminatingOrDeleted(enemy) && _mobState.IsAlive(enemy))
        {
            if (seen)
                comp.LastSawTarget = now;

            // Still just round the corner: it heard where they went.
            if (seen || now - comp.LastSawTarget < comp.LeadGraceTime)
                SetLead(comp, _transform.GetMoverCoordinates(enemy), enemy, now);
        }

        if (comp.Lead != null && now - comp.LeadTime > comp.LeadMemory)
            ClearLead(comp);
    }

    private void SetLead(NpcTacticalComponent comp, EntityCoordinates lead, EntityUid? leadTarget, TimeSpan now)
    {
        if (comp.Lead is not { } old ||
            !old.TryDistance(EntityManager, _transform, lead, out var moved) ||
            moved > NewLeadDistance)
        {
            comp.SearchAttempts = 0;
        }

        comp.Lead = lead;
        comp.LeadTarget = leadTarget;
        comp.LeadTime = now;
    }

    public void ClearLead(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (Resolve(uid, ref comp, false))
            ClearLead(comp);
    }

    private static void ClearLead(NpcTacticalComponent comp)
    {
        comp.Lead = null;
        comp.LeadTarget = null;
        comp.SearchAttempts = 0;
    }

    /// <summary>
    /// Passes the NPC's lead on to friendly soldiers nearby with nothing better to do, so they come and help
    /// instead of standing around while it fights alone just out of their sight.
    /// </summary>
    private void AlertAllies(EntityUid uid, NpcTacticalComponent comp, TimeSpan now)
    {
        if (comp.Lead is not { } lead)
            return;

        foreach (var ally in _lookup.GetEntitiesInRange<NpcTacticalComponent>(_transform.GetMapCoordinates(uid), comp.AlertRadius))
        {
            if (ally.Owner == uid ||
                HasComp<ActorComponent>(ally) ||
                !_mobState.IsAlive(ally) ||
                IsFighting(ally) ||
                !_iff.IsFriendly(uid, ally.Owner))
            {
                continue;
            }

            if (ally.Comp.Lead != null && now - ally.Comp.LeadTime < FreshLeadTime)
                continue;

            SetLead(ally.Comp, lead, comp.LeadTarget, now);
            RequestReplan(ally);
        }
    }

    /// <summary>
    /// Getting shot at by something it can't see: turn towards where it came from, go and have a look, and
    /// let the others know.
    /// </summary>
    private void OnDamageChanged(Entity<NpcTacticalComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } origin || origin == ent.Owner)
            return;

        // Busy with it already; the fight keeps the lead up to date.
        if (IsFighting(ent) || HasComp<ActorComponent>(ent) || !_mobState.IsAlive(ent))
            return;

        var now = _timing.CurTime;
        if (ent.Comp.Lead != null && now - ent.Comp.LeadTime < DamageLeadCooldown)
            return;

        if (TerminatingOrDeleted(origin) || !IsHostileTo(ent, origin))
            return;

        var originPos = _transform.GetMapCoordinates(origin);
        if (originPos.MapId != _transform.GetMapCoordinates(ent).MapId)
            return;

        _rotate.TryFaceCoordinates(ent, originPos.Position);
        SetLead(ent.Comp, _transform.GetMoverCoordinates(origin), origin, now);
        AlertAllies(ent, ent.Comp, now);
        RequestReplan(ent);
    }

    /// <summary>
    /// Whether <paramref name="other"/> is someone this NPC would fight: not its own side, and on a side
    /// its own is hostile to or one it holds a grudge against.
    /// </summary>
    private bool IsHostileTo(EntityUid npc, EntityUid other)
    {
        if (_iff.IsFriendly(npc, other))
            return false;

        if (TryComp<FactionExceptionComponent>(npc, out var exception) &&
            _npcFaction.GetHostiles((npc, exception)).Contains(other))
        {
            return true;
        }

        if (!TryComp<NpcFactionMemberComponent>(npc, out var own) ||
            !TryComp<NpcFactionMemberComponent>(other, out var theirs))
        {
            return false;
        }

        foreach (var faction in theirs.Factions)
        {
            if (_npcFaction.IsFactionHostile(faction, (npc, own)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the NPC has somewhere to go looking for an enemy, and is allowed to go there.
    /// </summary>
    public bool HasLeadToSearch(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false) || comp.Lead is not { } lead)
            return false;

        if (_timing.CurTime - comp.LeadTime > comp.LeadMemory ||
            comp.SearchAttempts >= MaxSearchAttempts ||
            !lead.IsValid(EntityManager))
        {
            return false;
        }

        // Whoever it was is down already; nothing to go looking for.
        if (comp.LeadTarget is { } leadTarget && (TerminatingOrDeleted(leadTarget) || !_mobState.IsAlive(leadTarget)))
        {
            ClearLead(comp);
            return false;
        }

        // Not off the squad's leash.
        if (_squad.TryGetLeash(uid, out var centre, out var range))
        {
            var leadMap = _transform.ToMapCoordinates(lead);
            if (leadMap.MapId != centre.MapId || (leadMap.Position - centre.Position).Length() > range + 2f)
                return false;
        }

        return true;
    }

    public bool TryGetSearchSpot(EntityUid uid, out EntityCoordinates spot)
    {
        spot = EntityCoordinates.Invalid;

        if (!TryComp<NpcTacticalComponent>(uid, out var comp) || !HasLeadToSearch(uid, comp))
            return false;

        spot = comp.Lead!.Value;
        return true;
    }

    /// <summary>
    /// Called as the NPC sets off towards its lead.
    /// </summary>
    public void BeginSearch(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        comp.SearchAttempts++;

        if (comp.SearchAttempts == 1)
            TryCallout(uid, NpcCalloutType.Search, comp: comp);
    }

    private void RequestReplan(EntityUid uid)
    {
        if (TryComp<HTNComponent>(uid, out var htn))
            _htn.Replan(htn);
    }

    #endregion

    #region Looking around

    /// <summary>
    /// Starts the NPC looking around for <paramref name="duration"/>.
    /// </summary>
    public void BeginLookAround(EntityUid uid, TimeSpan duration, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        comp.LookAroundUntil = _timing.CurTime + duration;
        comp.NextGuardLook = _timing.CurTime;
    }

    public TimeSpan RandomSearchTime(EntityUid uid)
    {
        if (!TryComp<NpcTacticalComponent>(uid, out var comp))
            return TimeSpan.FromSeconds(4);

        var span = (comp.MaxSearchTime - comp.MinSearchTime).TotalSeconds;
        return comp.MinSearchTime + TimeSpan.FromSeconds(_random.NextDouble() * span);
    }

    /// <summary>
    /// One tick of looking around: turns to face a new direction every second or two, favouring the open
    /// ones - nobody is coming out of the wall.
    /// </summary>
    /// <returns>False once the time is up.</returns>
    public bool UpdateLookAround(EntityUid uid, float frameTime, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        var now = _timing.CurTime;
        if (now >= comp.LookAroundUntil)
            return false;

        var xform = Transform(uid);

        if (now >= comp.NextGuardLook)
        {
            comp.GuardLookAngle = PickOpenDirection(uid, xform);

            var span = (MaxLookTime - MinLookTime).TotalSeconds;
            comp.NextGuardLook = now + MinLookTime + TimeSpan.FromSeconds(_random.NextDouble() * span);
        }

        _rotate.TryRotateTo(uid, comp.GuardLookAngle, frameTime, Angle.FromDegrees(5), comp.GuardTurnSpeed, xform);
        return true;
    }

    /// <summary>
    /// A random direction to look in, out of the eight around the NPC, skipping any with a wall right there.
    /// </summary>
    private Angle PickOpenDirection(EntityUid uid, TransformComponent xform)
    {
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return _random.NextAngle();

        var origin = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var start = _random.Next(8);
        _layerCache.Clear();

        for (var i = 0; i < 8; i++)
        {
            var dir = (Direction) ((start + i) % 8);

            if ((GetAnchoredLayers(gridUid, grid, origin + dir.ToIntVec()) & (int) ShotBlockers) != 0)
                continue;

            // Grid-relative direction, turned into a world one, with a little wobble so it isn't robotic.
            var local = dir.ToAngle() + new Angle(_random.NextFloat(-0.3f, 0.3f));
            return local + _transform.GetWorldRotation(gridUid);
        }

        return _random.NextAngle();
    }

    #endregion

    #region Peeking

    /// <summary>
    /// Called as the NPC starts fighting from a position, and whether it may step out of it to shoot.
    /// </summary>
    private void ResetPeek(NpcTacticalComponent comp, bool allowed)
    {
        comp.PeekTile = null;
        comp.PeekAllowed = allowed;
        comp.NextPeek = _timing.CurTime + RandomPeekInterval(comp);
    }

    private TimeSpan RandomPeekInterval(NpcTacticalComponent comp)
    {
        var span = (comp.MaxPeekInterval - comp.MinPeekInterval).TotalSeconds;
        return comp.MinPeekInterval + TimeSpan.FromSeconds(_random.NextDouble() * span);
    }

    public void EndEngagement(EntityUid uid, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        comp.PeekTile = null;
        comp.PeekFromBlind = false;
        comp.PeekAllowed = false;
    }

    /// <summary>
    /// Every few seconds while fighting from cover, steps out to a neighbouring tile that still has a shot at
    /// the target - off to the side of it rather than towards it - and back again after a moment. Makes the
    /// NPC a harder target and gets it round whatever was half in the way.
    /// </summary>
    /// <param name="targetOutOfSight">
    /// Whether the NPC can't see its target right now. From cover, that gets it leaning out straight away
    /// rather than waiting on its timer, and a lean that found the target stays out while it can see it.
    /// </param>
    public void UpdatePeek(EntityUid uid, EntityUid target, bool moving, bool targetOutOfSight = false,
        NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false) || !comp.PeekAllowed)
            return;

        var now = _timing.CurTime;

        if (comp.PeekTile != null)
        {
            if (now < comp.PeekUntil)
                return;

            // Its cover couldn't see the target and this can: no sense ducking back behind the wall.
            if (comp.PeekFromBlind && !targetOutOfSight)
            {
                comp.PeekUntil = now + comp.PeekHoldTime;
                return;
            }

            // Back into cover; steering takes it there by itself once the peek tile is gone.
            comp.PeekTile = null;
            comp.PeekFromBlind = false;
            comp.NextPeek = now + RandomPeekInterval(comp);
            return;
        }

        // Settle into cover before stepping back out of it.
        if (moving)
        {
            if (comp.NextPeek < now + comp.MinPeekInterval)
                comp.NextPeek = now + comp.MinPeekInterval;

            return;
        }

        var blind = targetOutOfSight &&
                    comp.LostSightSince is { } lostSince &&
                    now - lostSince >= BlindPeekDelay;

        if (!blind && now < comp.NextPeek)
            return;

        comp.NextPeek = now + RandomPeekInterval(comp);

        if (TryFindPeekTile(uid, target, out var tile))
        {
            comp.PeekTile = tile;
            comp.PeekFromBlind = blind;
            comp.PeekUntil = now + comp.PeekHoldTime;
        }
    }

    private bool TryFindPeekTile(EntityUid uid, EntityUid target, out EntityCoordinates tile)
    {
        tile = EntityCoordinates.Invalid;

        var xform = Transform(uid);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var ownerMap = _transform.GetMapCoordinates(uid, xform);
        var targetMap = _transform.GetMapCoordinates(target);

        if (ownerMap.MapId != targetMap.MapId)
            return false;

        var origin = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var targetLocal = Vector2.Transform(targetMap.Position, _transform.GetInvWorldMatrix(gridUid));
        var toTarget = targetLocal - TileCentre(grid, origin);

        if (toTarget.LengthSquared() < 0.01f)
            return false;

        toTarget = Vector2.Normalize(toTarget);

        _layerCache.Clear();
        _occupied.Clear();

        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(ownerMap, 2f))
        {
            if (mob.Owner != uid)
                _occupied.Add(_map.TileIndicesFor(gridUid, grid, Transform(mob).Coordinates));
        }

        _candidates.Clear();

        for (var i = 0; i < 8; i++)
        {
            var offset = ((Direction) i).ToIntVec();
            var indices = origin + offset;

            if (_occupied.Contains(indices) || !IsStandable(gridUid, grid, indices))
                continue;

            // A diagonal step past a corner would cut through the wall.
            if (offset.X != 0 && offset.Y != 0 &&
                (!IsStandable(gridUid, grid, origin + new Vector2i(offset.X, 0)) ||
                 !IsStandable(gridUid, grid, origin + new Vector2i(0, offset.Y))))
            {
                continue;
            }

            var dir = Vector2.Normalize(new Vector2(offset.X, offset.Y));
            var along = Vector2.Dot(dir, toTarget);

            // Sideways is best, and never straight at them.
            if (along > 0.75f)
                continue;

            var score = 1f - MathF.Abs(along) + _random.NextFloat(0f, 0.3f);
            _candidates.Add((score, indices));
        }

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var checkFriendlies = HoldsFireForFriendlies(uid);

        foreach (var (_, indices) in _candidates)
        {
            var coords = _map.GridTileToLocal(gridUid, grid, indices);

            if (!HasSolidShot(uid, _transform.ToMapCoordinates(coords), target, targetMap, checkFriendlies))
                continue;

            tile = coords;
            return true;
        }

        return false;
    }

    /// <summary>
    /// While peeking, steers the NPC to the peek tile instead of back to its cover.
    /// </summary>
    private void OnSteering(Entity<NpcTacticalComponent> ent, ref NPCSteeringEvent args)
    {
        if (ent.Comp.PeekTile is not { } peek || !peek.IsValid(EntityManager))
            return;

        args.Steering.CanSeek = false;

        var peekWorld = _transform.ToMapCoordinates(peek).Position;
        var toPeek = peekWorld - args.WorldPosition;

        // Out on the tile: stand there and shoot.
        if (toPeek.Length() <= PeekArriveDistance)
            return;

        var norm = args.OffsetRotation.RotateVec(toPeek).Normalized();

        for (var i = 0; i < SharedNPCSteeringSystem.InterestDirections; i++)
        {
            var result = Vector2.Dot(norm, NPCSteeringSystem.Directions[i]);

            if (result <= 0f)
                continue;

            args.Steering.Interest[i] = MathF.Max(args.Steering.Interest[i], result);
        }
    }

    #endregion

    #region Repositioning and flanking

    /// <summary>
    /// Marks the NPC's current spot as one to move away from, and throws away the cover it had picked from
    /// there, so the next search actually picks somewhere new.
    /// </summary>
    private void MarkReposition(EntityUid uid, NpcTacticalComponent comp)
    {
        comp.RepositionFrom = Transform(uid).Coordinates;
        comp.RepositionAvoidUntil = _timing.CurTime + comp.RepositionAvoidTime;
        comp.CachedCoverTime = TimeSpan.Zero;
        comp.CachedCoverTarget = null;
        comp.CachedAdvanceTime = TimeSpan.Zero;
        comp.CachedAdvanceTarget = null;
        comp.CachedFiringTime = TimeSpan.Zero;
        comp.CachedFiringTarget = null;
    }

    /// <summary>
    /// Collects the grid-local positions squadmates and other friendly soldiers mean to fight
    /// <paramref name="target"/> from, for <see cref="GetFlankPenalty"/>.
    /// </summary>
    private void CollectAllyClaims(EntityUid owner, EntityUid target, EntityUid gridUid)
    {
        _allyClaims.Clear();

        var query = EntityQueryEnumerator<NpcTacticalComponent, TransformComponent>();
        while (query.MoveNext(out var other, out var otherComp, out var otherXform))
        {
            if (other == owner ||
                otherXform.GridUid != gridUid ||
                otherComp.CachedCoverTarget != target ||
                otherComp.ClaimedCover is not { } claim ||
                claim.EntityId != gridUid)
            {
                continue;
            }

            _allyClaims.Add(claim.Position);
        }
    }

    /// <summary>
    /// How much a spot is marked down for looking at the target from nearly the same direction as a friend
    /// already does.
    /// </summary>
    private float GetFlankPenalty(Vector2 candidate, Vector2 targetLocal, float weight)
    {
        if (_allyClaims.Count == 0 || weight <= 0f)
            return 0f;

        var fromTarget = candidate - targetLocal;
        if (fromTarget.LengthSquared() < 0.01f)
            return 0f;

        const float spread = MathF.PI / 5f; // 36 degrees
        var penalty = 0f;

        foreach (var claim in _allyClaims)
        {
            var claimFromTarget = claim - targetLocal;
            if (claimFromTarget.LengthSquared() < 0.01f)
                continue;

            var angle = MathF.Abs((float) Angle.ShortestDistance(fromTarget.ToAngle(), claimFromTarget.ToAngle()).Theta);

            if (angle < spread)
                penalty += (1f - angle / spread) * weight;
        }

        return penalty;
    }

    #endregion
}
