using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared._Crescent.NpcSquad;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: attacking under orders. Instead of finding a good spot at its preferred range and holding it,
/// the soldier bounds forward - from one bit of cover to the next one closer in, shooting on the way and
/// stopping only briefly at each - until it is close enough for the weapon it carries.
/// </summary>
public sealed partial class NpcTacticalSystem
{
    /// <summary>
    /// Whether this NPC has been ordered to attack, so it pushes in instead of holding a position.
    /// </summary>
    public bool IsAssaulting(EntityUid uid)
    {
        return _squad.GetOrder(uid) == NpcSquadOrder.Attack;
    }

    /// <summary>
    /// The range an assault stops pushing at: the NPC's preferred minimum range, but never closer than
    /// <see cref="NpcTacticalComponent.AssaultStopRange"/>.
    /// </summary>
    public float GetAssaultStopRange(EntityUid uid, float combatRangeMin, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return combatRangeMin;

        return MathF.Max(combatRangeMin, comp.AssaultStopRange);
    }

    /// <summary>
    /// Finds the next spot to push up to on the way to <paramref name="target"/>: a good deal closer than the
    /// NPC is now, next to cover if there is any, and with a shot at the target if that can be had. Once the
    /// NPC is within <paramref name="stopRange"/> with a clear shot, that is where it stays.
    /// </summary>
    public bool TryFindAdvanceSpot(EntityUid owner, EntityUid target, float stopRange,
        [NotNullWhen(true)] out EntityCoordinates? spot)
    {
        spot = null;

        if (!TryComp<NpcTacticalComponent>(owner, out var comp))
            return false;

        var now = _timing.CurTime;

        if (comp.CachedAdvanceTarget == target && now - comp.CachedAdvanceTime < comp.CoverCacheTime)
        {
            spot = comp.CachedAdvance;
            return spot != null;
        }

        spot = FindAdvanceSpot(owner, comp, target, stopRange);

        comp.CachedAdvanceTarget = target;
        comp.CachedAdvance = spot;
        comp.CachedAdvanceTime = now;
        comp.ClaimedCover = spot;
        return spot != null;
    }

    private EntityCoordinates? FindAdvanceSpot(EntityUid owner, NpcTacticalComponent comp, EntityUid target, float stopRange)
    {
        var xform = Transform(owner);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var ownerMap = _transform.GetMapCoordinates(owner, xform);
        var targetMap = _transform.GetMapCoordinates(target);

        if (ownerMap.MapId != targetMap.MapId)
            return null;

        var checkFriendlies = HoldsFireForFriendlies(owner);
        var ownerDistance = (targetMap.Position - ownerMap.Position).Length();

        // Close enough already: hold here and fight, as long as there's actually a shot from here.
        if (ownerDistance <= stopRange + 0.5f && HasSolidShot(owner, ownerMap, target, targetMap, checkFriendlies))
            return xform.Coordinates;

        var targetLocal = Vector2.Transform(targetMap.Position, _transform.GetInvWorldMatrix(gridUid));
        var origin = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var radius = comp.AssaultSearchRadius;

        // Each bound has to be worth making, but it doesn't have to go past where the assault stops.
        var minProgress = MathF.Min(comp.AssaultStep, MathF.Max(ownerDistance - stopRange, 0.5f));

        CollectOccupied(owner, comp, gridUid, grid, ownerMap, radius);
        CollectAllyClaims(owner, target, gridUid);
        _candidates.Clear();

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var indices = origin + new Vector2i(dx, dy);

                if (_occupied.Contains(indices) || !IsStandable(gridUid, grid, indices))
                    continue;

                var centre = TileCentre(grid, indices);
                var toTarget = targetLocal - centre;
                var range = toTarget.Length();
                var progress = ownerDistance - range;

                if (progress < minProgress - 0.01f)
                    continue;

                var cover = GetCoverValue(gridUid, grid, indices, toTarget / MathF.Max(range, 0.01f));
                var travel = new Vector2(dx, dy).Length();

                var score = cover * 1.5f + progress * 0.8f - travel * 0.25f;

                // Right in their face is further than it needs to go.
                if (range < stopRange)
                    score -= (stopRange - range) * 1.5f;

                score -= GetFlankPenalty(centre, targetLocal, comp.FlankWeight);
                _candidates.Add((score, indices));
            }
        }

        if (_candidates.Count == 0)
            return null;

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Somewhere to shoot from if there is one. If there isn't, push on anyway: it shoots whenever it gets
        // a line on the way, and the next bound gets it closer still.
        var checks = Math.Min(_candidates.Count, MaxCoverRaycasts);
        for (var i = 0; i < checks; i++)
        {
            var coords = _map.GridTileToLocal(gridUid, grid, _candidates[i].Indices);

            if (HasSolidShot(owner, _transform.ToMapCoordinates(coords), target, targetMap, checkFriendlies))
                return coords;
        }

        return _map.GridTileToLocal(gridUid, grid, _candidates[0].Indices);
    }

    /// <summary>
    /// Called as the NPC sets off on a bound.
    /// </summary>
    public void BeginAdvance(EntityUid uid, EntityCoordinates spot, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        // Holding where it is isn't worth shouting about.
        if (Transform(uid).Coordinates.TryDistance(EntityManager, _transform, spot, out var distance) && distance < 1f)
            return;

        TryCallout(uid, NpcCalloutType.Advance, comp: comp);
    }
}
