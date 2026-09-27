using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Server.Popups;
using Content.Shared._Crescent.Barricades;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.DoAfter;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Stacks;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NPC;

public sealed partial class NpcTacticalSystem
{
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;

    /// <summary>
    /// Only sides that face the threat at least this squarely get a barricade.
    /// </summary>
    private const float BarricadeFacingDot = 0.3f;

    /// <summary>
    /// A barricade already standing next to the anchor covers it if it faces the threat this squarely.
    /// </summary>
    private const float CoveredFacingDot = 0.5f;

    private static readonly Vector2i[] CardinalOffsets =
    [
        new(0, 1),
        new(1, 0),
        new(0, -1),
        new(-1, 0),
    ];

    /// <summary>
    /// Whether this NPC should dig in right now: it's in a squad, carries enough material, there are
    /// enemies about, and either it was told to defend or its leader is hurt.
    /// </summary>
    public bool WantsToFortify(EntityUid npc, float vision, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(npc, ref comp, false) || !_mobState.IsAlive(npc))
            return false;

        if (_timing.CurTime < comp.NextBarricade)
            return false;

        if (!_squad.TryGetLeader(npc, out var leader) || _squad.GetOrder(npc) is not { } order)
            return false;

        if (order == NpcSquadOrder.HoldFire)
            return false;

        if (order != NpcSquadOrder.Defend && !IsLeaderInTrouble(leader, comp))
            return false;

        if (CountMaterial(npc, comp.BarricadeStackType) < comp.BarricadeCost)
            return false;

        return TryGetNearestThreat(npc, vision, out _);
    }

    private bool IsLeaderInTrouble(EntityUid leader, NpcTacticalComponent comp)
    {
        if (_mobState.IsDead(leader))
            return false;

        return _mobState.IsCritical(leader) ||
               TryGetDamageFraction(leader, out var fraction) && fraction >= comp.LeaderFortifyThreshold;
    }

    /// <summary>
    /// The nearest living enemy this NPC would go after within <paramref name="vision"/>.
    /// </summary>
    public bool TryGetNearestThreat(EntityUid npc, float vision, out EntityUid threat)
    {
        threat = EntityUid.Invalid;
        var ownPos = _transform.GetMapCoordinates(npc).Position;
        var best = float.MaxValue;

        foreach (var hostile in _npcFaction.GetNearbyHostiles(npc, vision))
        {
            if (!_mobState.IsAlive(hostile) || _passiveTarget.IsLeftAlone(npc, hostile))
                continue;

            var distance = (_transform.GetMapCoordinates(hostile).Position - ownPos).LengthSquared();
            if (distance >= best)
                continue;

            best = distance;
            threat = hostile;
        }

        return threat.IsValid();
    }

    /// <summary>
    /// Picks where to put a barricade: on the side of the anchor facing the nearest enemy, where the anchor
    /// is the leader if they're hurt - so the squad rings them in - and the NPC itself otherwise.
    /// </summary>
    /// <returns>False if there's no free tile on a side facing the enemy, or the anchor is covered already.</returns>
    public bool TryPickBarricadeSpot(EntityUid npc, float vision,
        [NotNullWhen(true)] out EntityCoordinates? spot, out Angle rotation)
    {
        spot = null;
        rotation = Angle.Zero;

        if (!TryComp<NpcTacticalComponent>(npc, out var comp) ||
            !_squad.TryGetLeader(npc, out var leader) ||
            !TryGetNearestThreat(npc, vision, out var threat))
        {
            return false;
        }

        // Holding a post, it only leaves it to ring in a leader who is actually down.
        var ringLeader = IsLeaderInTrouble(leader, comp) &&
                         (_squad.GetOrder(npc) != NpcSquadOrder.Defend || _mobState.IsCritical(leader));
        var anchor = ringLeader ? leader : npc;
        var anchorXform = Transform(anchor);

        if (anchorXform.GridUid is not { } gridUid ||
            Transform(npc).GridUid != gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            return false;
        }

        var anchorTile = _map.TileIndicesFor(gridUid, grid, anchorXform.Coordinates);
        var threatLocal = Vector2.Transform(_transform.GetWorldPosition(threat), _transform.GetInvWorldMatrix(gridUid));
        var toThreat = threatLocal - TileCentre(grid, anchorTile);

        if (toThreat.LengthSquared() < 0.01f)
            return false;

        toThreat = Vector2.Normalize(toThreat);

        // Tiles with someone standing on them and tiles squadmates are already building on are out.
        CollectOccupied(npc, comp, gridUid, grid, _transform.GetMapCoordinates(npc), comp.CoverSearchRadius);
        _occupied.Add(_map.TileIndicesFor(gridUid, grid, Transform(npc).Coordinates));
        CollectPendingBarricades(npc, gridUid, grid);

        _candidates.Clear();

        foreach (var offset in CardinalOffsets)
        {
            var dot = Vector2.Dot(new Vector2(offset.X, offset.Y), toThreat);
            if (dot < BarricadeFacingDot)
                continue;

            var tile = anchorTile + offset;

            // Somebody already put one up on that side: that side is done.
            if (HasBarricadeFacing(gridUid, grid, tile, toThreat) || HasBarricadeFacing(gridUid, grid, anchorTile, toThreat))
            {
                // Defending alone, one covered side is all the NPC needs. Ringing the leader, try the others.
                if (!ringLeader)
                    return false;

                continue;
            }

            if (_occupied.Contains(tile) || !IsStandable(gridUid, grid, tile))
                continue;

            _candidates.Add((dot, tile));
        }

        if (_candidates.Count == 0)
            return false;

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var (_, chosen) = _candidates[0];
        var facing = chosen - anchorTile;

        spot = _map.GridTileToLocal(gridUid, grid, chosen);
        rotation = new Vector2(facing.X, facing.Y).ToWorldAngle();
        return true;
    }

    private void CollectPendingBarricades(EntityUid npc, EntityUid gridUid, MapGridComponent grid)
    {
        var query = EntityQueryEnumerator<NpcTacticalComponent>();
        while (query.MoveNext(out var other, out var otherComp))
        {
            if (other == npc || otherComp.PendingBarricade is not { } pending || pending.EntityId != gridUid)
                continue;

            _occupied.Add(_map.TileIndicesFor(gridUid, grid, pending));
        }
    }

    /// <summary>
    /// Whether the NPC stands right behind a barricade - on its tile or the next one over towards
    /// <paramref name="threat"/> - that faces the threat.
    /// </summary>
    public bool IsDugInAgainst(EntityUid npc, EntityUid threat)
    {
        var xform = Transform(npc);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var threatLocal = Vector2.Transform(_transform.GetWorldPosition(threat), _transform.GetInvWorldMatrix(gridUid));
        var toThreat = threatLocal - TileCentre(grid, tile);

        if (toThreat.LengthSquared() < 0.01f)
            return false;

        toThreat = Vector2.Normalize(toThreat);

        if (HasBarricadeFacing(gridUid, grid, tile, toThreat))
            return true;

        foreach (var offset in CardinalOffsets)
        {
            if (Vector2.Dot(new Vector2(offset.X, offset.Y), toThreat) >= BarricadeFacingDot &&
                HasBarricadeFacing(gridUid, grid, tile + offset, toThreat))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a barricade on <paramref name="indices"/> already faces roughly towards <paramref name="toThreat"/>.
    /// </summary>
    private bool HasBarricadeFacing(EntityUid gridUid, MapGridComponent grid, Vector2i indices, Vector2 toThreat)
    {
        var enumerator = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, indices);

        while (enumerator.MoveNext(out var ent))
        {
            if (!HasComp<DirectionalBarricadeComponent>(ent.Value))
                continue;

            var facing = Transform(ent.Value).LocalRotation.ToWorldVec();
            if (Vector2.Dot(facing, toThreat) >= CoveredFacingDot)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Starts putting up a barricade at <paramref name="spot"/>. The NPC has to stand still until it's done.
    /// </summary>
    /// <param name="fort">Whether it is one edge of the squad's barricade ring, see <see cref="NpcSquadSystem"/>.</param>
    public bool TryStartBarricade(EntityUid npc, EntityCoordinates spot, Angle rotation, bool fort = false,
        NpcTacticalComponent? comp = null)
    {
        if (!Resolve(npc, ref comp, false))
            return false;

        if (CountMaterial(npc, comp.BarricadeStackType) < comp.BarricadeCost)
            return false;

        comp.PendingBarricade = spot;
        comp.PendingBarricadeRotation = rotation;
        comp.PendingBarricadeFort = fort;
        comp.BarricadeFinished = false;

        var args = new DoAfterArgs(EntityManager, npc, comp.BarricadeBuildTime, new NpcBuildBarricadeDoAfterEvent(), npc)
        {
            BreakOnMove = true,
            NeedHand = false,
        };

        if (!_doAfter.TryStartDoAfter(args))
        {
            comp.PendingBarricade = null;
            return false;
        }

        TryCallout(npc, NpcCalloutType.Fortify, comp: comp);
        return true;
    }

    /// <summary>
    /// Clears the NPC's claim on its barricade spot, whether it got built or not.
    /// </summary>
    public void EndBarricade(EntityUid npc, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(npc, ref comp, false))
            return;

        // Interrupted: back off for a bit rather than restarting on the very next plan.
        if (!comp.BarricadeFinished && comp.PendingBarricade != null)
        {
            comp.NextBarricade = _timing.CurTime + TimeSpan.FromSeconds(5);

            if (comp.PendingBarricadeFort)
                _squad.ReportFortFailure(npc);
        }

        comp.PendingBarricade = null;
        comp.PendingBarricadeFort = false;
    }

    public bool IsBarricadeFinished(EntityUid npc, NpcTacticalComponent? comp = null)
    {
        return Resolve(npc, ref comp, false) && comp.BarricadeFinished;
    }

    private void OnBarricadeDoAfter(Entity<NpcTacticalComponent> ent, ref NpcBuildBarricadeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || ent.Comp.PendingBarricade is not { } spot)
            return;

        args.Handled = true;

        if (spot.EntityId is not { Valid: true } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        // Someone may have walked onto it, or built on it, while the NPC worked.
        var tile = _map.TileIndicesFor(gridUid, grid, spot);

        if (ent.Comp.PendingBarricadeFort)
        {
            if (!CanBuildFortBarricade(gridUid, grid, tile, ToGridDirection(ent.Comp.PendingBarricadeRotation), checkMobs: true))
                return;
        }
        else
        {
            _layerCache.Clear();
            if (!IsStandable(gridUid, grid, tile))
                return;

            foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(_transform.ToMapCoordinates(spot), 0.4f))
            {
                if (_mobState.IsAlive(mob))
                    return;
            }
        }

        if (!ConsumeMaterial(ent, ent.Comp.BarricadeStackType, ent.Comp.BarricadeCost))
            return;

        var barricade = Spawn(ent.Comp.BarricadePrototype, spot);
        _transform.SetLocalRotation(barricade, ent.Comp.PendingBarricadeRotation);

        ent.Comp.BarricadeFinished = true;
        ent.Comp.NextBarricade = _timing.CurTime + ent.Comp.BarricadeCooldown;

        _popup.PopupEntity(Loc.GetString("npc-soldier-barricade-built", ("npc", ent.Owner)), barricade);
    }

    #region Barricade ring

    /// <summary>
    /// How close to the edge a barricade goes on someone has to stand to be in the way of it.
    /// </summary>
    private const float FortEdgeClearance = 0.4f;

    /// <summary>
    /// Whether a ring barricade facing <paramref name="facing"/> can go on <paramref name="indices"/>: floor,
    /// no wall or window on it, and not already one facing that way. The tile may hold a barricade facing
    /// another way - the corners of the ring take two. With <paramref name="checkMobs"/>, also that nobody
    /// stands on that edge of the tile.
    /// </summary>
    public bool CanBuildFortBarricade(EntityUid gridUid, MapGridComponent grid, Vector2i indices, Vector2i facing,
        bool checkMobs = false)
    {
        if (!IsFloorClear(gridUid, grid, indices) || HasFortBarricade(gridUid, grid, indices, facing))
            return false;

        if (!checkMobs)
            return true;

        var edge = TileCentre(grid, indices) + new Vector2(facing.X, facing.Y) * 0.4f;
        var edgeMap = _transform.ToMapCoordinates(new EntityCoordinates(gridUid, edge));

        // By where they stand, not by their outline: whoever builds it stands right by it.
        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(edgeMap, FortEdgeClearance + 0.5f))
        {
            if (!_mobState.IsAlive(mob))
                continue;

            var at = _transform.GetMapCoordinates(mob);
            if ((at.Position - edgeMap.Position).Length() <= FortEdgeClearance)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether there is a barricade on <paramref name="indices"/> facing <paramref name="facing"/> already.
    /// </summary>
    public bool HasFortBarricade(EntityUid gridUid, MapGridComponent grid, Vector2i indices, Vector2i facing)
    {
        var enumerator = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, indices);
        while (enumerator.MoveNext(out var anchored))
        {
            if (_barricadeQuery.HasComp(anchored.Value) && FacesExactly(anchored.Value, facing))
                return true;
        }

        return false;
    }

    private bool FacesExactly(EntityUid barricade, Vector2i facing)
    {
        var vec = Transform(barricade).LocalRotation.ToWorldVec();
        return Vector2.Dot(vec, new Vector2(facing.X, facing.Y)) >= 0.9f;
    }

    private static Vector2i ToGridDirection(Angle rotation)
    {
        var vec = rotation.ToWorldVec();
        return MathF.Abs(vec.X) >= MathF.Abs(vec.Y)
            ? new Vector2i(MathF.Sign(vec.X), 0)
            : new Vector2i(0, MathF.Sign(vec.Y));
    }

    /// <summary>
    /// Whether a soldier could stand at <paramref name="coords"/> on <paramref name="gridUid"/>: floor with
    /// nothing solid on it.
    /// </summary>
    public bool IsStandableAt(EntityUid gridUid, MapCoordinates coords)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        _layerCache.Clear();
        return IsStandable(gridUid, grid, WorldToTile(gridUid, grid, coords.Position));
    }

    /// <summary>
    /// The standable tile nearest <paramref name="desired"/> within <paramref name="radius"/> tiles of it,
    /// skipping <paramref name="taken"/>.
    /// </summary>
    public bool TryFindStandableNear(EntityUid gridUid, MapCoordinates desired, int radius, ICollection<Vector2i> taken,
        [NotNullWhen(true)] out EntityCoordinates? spot, out Vector2i chosen)
    {
        spot = null;
        chosen = default;

        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var local = Vector2.Transform(desired.Position, _transform.GetInvWorldMatrix(gridUid));
        var origin = WorldToTile(gridUid, grid, desired.Position);
        var best = float.MaxValue;

        _layerCache.Clear();

        for (var x = -radius; x <= radius; x++)
        {
            for (var y = -radius; y <= radius; y++)
            {
                var indices = origin + new Vector2i(x, y);
                if (taken.Contains(indices) || !IsStandable(gridUid, grid, indices))
                    continue;

                var distance = (TileCentre(grid, indices) - local).LengthSquared();
                if (distance >= best)
                    continue;

                best = distance;
                chosen = indices;
            }
        }

        if (best == float.MaxValue)
            return false;

        spot = _map.GridTileToLocal(gridUid, grid, chosen);
        return true;
    }

    /// <summary>
    /// Floor with no wall, window or shut door on it. Barricades and the like don't count, so this is the
    /// test for somewhere to stand inside the ring.
    /// </summary>
    public bool IsFloorClear(EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        if (!_map.TryGetTileRef(gridUid, grid, indices, out var tile) || tile.Tile.IsEmpty)
            return false;

        var enumerator = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, indices);
        while (enumerator.MoveNext(out var anchored))
        {
            if (_physicsQuery.TryComp(anchored.Value, out var body) && body.CanCollide && body.Hard &&
                (body.CollisionLayer & (int) CollisionGroup.Impassable) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private Vector2i WorldToTile(EntityUid gridUid, MapGridComponent grid, Vector2 world)
    {
        var local = Vector2.Transform(world, _transform.GetInvWorldMatrix(gridUid));
        return new Vector2i((int) MathF.Floor(local.X / grid.TileSize), (int) MathF.Floor(local.Y / grid.TileSize));
    }

    #endregion

    /// <summary>
    /// How much of a stack type the NPC carries in total.
    /// </summary>
    public int CountMaterial(EntityUid npc, string stackType)
    {
        var total = 0;

        foreach (var carried in EnumerateCarried(npc))
        {
            if (TryComp<StackComponent>(carried, out var stack) && stack.StackTypeId.Id == stackType)
                total += _stack.GetCount(carried, stack);
        }

        return total;
    }

    private bool ConsumeMaterial(EntityUid npc, string stackType, int amount)
    {
        if (CountMaterial(npc, stackType) < amount)
            return false;

        foreach (var carried in EnumerateCarried(npc))
        {
            if (amount <= 0)
                break;

            if (!TryComp<StackComponent>(carried, out var stack) || stack.StackTypeId.Id != stackType)
                continue;

            var take = Math.Min(amount, _stack.GetCount(carried, stack));
            if (_stack.Use(carried, take, stack))
                amount -= take;
        }

        return amount <= 0;
    }
}
