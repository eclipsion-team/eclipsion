using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared.Mobs.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: finding somewhere to shoot from when there's no cover to be had near the NPC - the target is
/// in another room, seen through a window, or on a ship docked alongside. The search is centred on the target
/// and done on the target's own grid, so the spot can be on the far side of a wall, an airlock or a dock,
/// and the pathfinder takes the NPC round to it.
/// </summary>
public sealed partial class NpcTacticalSystem
{
    /// <summary>
    /// How far round the target, in tiles, a firing spot is looked for at most.
    /// </summary>
    private const int MaxFiringSpotRadius = 10;

    /// <summary>
    /// Finds the closest tile to the NPC, somewhere in its preferred range band round <paramref name="target"/>,
    /// that it could actually see and hit the target from.
    /// </summary>
    public bool TryFindFiringSpot(EntityUid owner, EntityUid target, float minRange, float maxRange,
        [NotNullWhen(true)] out EntityCoordinates? spot)
    {
        spot = null;

        if (!TryComp<NpcTacticalComponent>(owner, out var comp))
            return false;

        var now = _timing.CurTime;

        if (comp.CachedFiringTarget == target && now - comp.CachedFiringTime < comp.CoverCacheTime)
        {
            spot = comp.CachedFiring;
            return spot != null;
        }

        spot = FindFiringSpot(owner, comp, target, minRange, maxRange);

        comp.CachedFiringTarget = target;
        comp.CachedFiring = spot;
        comp.CachedFiringTime = now;

        if (spot != null)
            comp.ClaimedCover = spot;

        return spot != null;
    }

    private EntityCoordinates? FindFiringSpot(EntityUid owner, NpcTacticalComponent comp, EntityUid target,
        float minRange, float maxRange)
    {
        var targetXform = Transform(target);

        // The spot goes on whatever the target stands on; off a grid there are no tiles to pick from.
        if (targetXform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var ownerMap = _transform.GetMapCoordinates(owner);
        var targetMap = _transform.GetMapCoordinates(target, targetXform);

        if (ownerMap.MapId != targetMap.MapId)
            return null;

        var radius = Math.Min((int) MathF.Ceiling(maxRange), MaxFiringSpotRadius);
        var origin = _map.TileIndicesFor(gridUid, grid, targetXform.Coordinates);
        var targetLocal = Vector2.Transform(targetMap.Position, _transform.GetInvWorldMatrix(gridUid));
        var hasLeash = _squad.TryGetLeash(owner, out var leashCentre, out var leashRange);

        _layerCache.Clear();
        _occupied.Clear();

        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(targetMap, radius + 1f))
        {
            if (mob.Owner != owner)
                _occupied.Add(_map.TileIndicesFor(gridUid, grid, Transform(mob).Coordinates));
        }

        CollectAllyClaims(owner, target, gridUid);
        _candidates.Clear();

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                var indices = origin + new Vector2i(dx, dy);

                if (_occupied.Contains(indices) || !IsStandable(gridUid, grid, indices))
                    continue;

                var centre = TileCentre(grid, indices);
                var range = (centre - targetLocal).Length();

                if (range > maxRange + 0.5f || range < MathF.Max(minRange * 0.5f, 1f))
                    continue;

                if (hasLeash && !InLeash(gridUid, grid, indices, leashCentre, leashRange))
                    continue;

                var tileMap = _transform.ToMapCoordinates(_map.GridTileToLocal(gridUid, grid, indices));
                var travel = (tileMap.Position - ownerMap.Position).Length();
                var rangePenalty = range < minRange ? minRange - range : 0f;
                var cover = GetCoverValue(gridUid, grid, indices, (targetLocal - centre) / MathF.Max(range, 0.01f));

                var score = -travel - rangePenalty * 0.75f + cover * 0.5f - GetFlankPenalty(centre, targetLocal, comp.FlankWeight);
                _candidates.Add((score, indices));
            }
        }

        if (_candidates.Count == 0)
            return null;

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var checkFriendlies = HoldsFireForFriendlies(owner);
        var checks = Math.Min(_candidates.Count, MaxCoverRaycasts * 2);

        for (var i = 0; i < checks; i++)
        {
            var coords = _map.GridTileToLocal(gridUid, grid, _candidates[i].Indices);

            if (HasSolidShot(owner, _transform.ToMapCoordinates(coords), target, targetMap, checkFriendlies))
                return coords;
        }

        return null;
    }
}
