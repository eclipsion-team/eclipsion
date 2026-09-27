using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Server._Crescent.NPC;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: the barricade ring. On the leader's word the squad closes in the 3x3 square of floor round them
/// with barricades on its outer edges - twelve of them, two on each corner tile - bar one in the middle of a
/// side, left open as the way in. Each soldier takes the nearest edge nobody else is on and builds it out of
/// its own steel, and once there is nothing left it can build, takes a post inside and defends from there.
/// </summary>
public sealed partial class NpcSquadSystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;

    /// <summary>
    /// How long a soldier's claim on an edge holds unless it renews it. Its planner renews it every replan
    /// while it's still on it.
    /// </summary>
    private static readonly TimeSpan FortClaimTime = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tries at one edge that came to nothing before the squad gives up on it.
    /// </summary>
    private const int MaxFortFailures = 5;

    /// <summary>
    /// How far in from the edge being built the builder stands: inside the ring, clear of the barricade.
    /// </summary>
    private const float FortStandInset = 0.35f;

    private static readonly Vector2i[] FortSides =
    [
        new(0, 1),
        new(1, 0),
        new(0, -1),
        new(-1, 0),
    ];

    private readonly HashSet<Vector2i> _takenTiles = new();

    private void OnBuildFortMessage(Entity<NpcSquadLeaderComponent> ent, ref NpcSquadBuildFortMessage args)
    {
        BuildFort(ent);
    }

    /// <summary>
    /// Has the squad barricade in the 3x3 square round its leader.
    /// </summary>
    /// <returns>False if the leader isn't in a state to order it, or isn't standing on a grid.</returns>
    public bool BuildFort(Entity<NpcSquadLeaderComponent> ent)
    {
        if (!_mobState.IsAlive(ent))
            return false;

        var xform = Transform(ent);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            _popup.PopupEntity(Loc.GetString("npc-squad-fort-no-floor"), ent, ent, PopupType.SmallCaution);
            return false;
        }

        var centre = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var fort = new NpcSquadFort
        {
            Grid = gridUid,
            Centre = centre,
            Entrance = PickFortEntrance(ent, gridUid, grid, centre),
        };

        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                var tile = centre + new Vector2i(x, y);

                if (y != 0)
                    TryAddFortSlot(fort, grid, tile, new Vector2i(0, y));

                if (x != 0)
                    TryAddFortSlot(fort, grid, tile, new Vector2i(x, 0));
            }
        }

        ent.Comp.Fort = fort;

        // Enough steel between them for the lot? Tell the leader now rather than leave them wondering.
        var toBuild = 0;
        foreach (var slot in fort.Slots)
        {
            if (!_tactical.HasFortBarricade(gridUid, grid, slot.Tile, slot.Facing))
                toBuild++;
        }

        var canBuild = 0;
        var acknowledged = false;

        foreach (var npc in ent.Comp.Members.ToArray())
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || !_mobState.IsAlive(npc))
                continue;

            if (TryComp<NpcTacticalComponent>(npc, out var tactical) && tactical.BarricadeCost > 0)
                canBuild += _tactical.CountMaterial(npc, tactical.BarricadeStackType) / tactical.BarricadeCost;

            member.OrderBeforeLeaderDown = null;
            member.DefendPointBeforeLeaderDown = null;
            SetOrder(npc, member, NpcSquadOrder.Fortify);

            if (!acknowledged)
                acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Fortify, force: true);
        }

        _popup.PopupEntity(Loc.GetString("npc-squad-order-given-fortify"), ent, ent);

        if (canBuild < toBuild)
        {
            _popup.PopupEntity(Loc.GetString("npc-squad-fort-short", ("have", canBuild), ("need", toBuild)),
                ent, ent, PopupType.SmallCaution);
        }

        UpdateUi(ent, ent.Comp);
        return true;
    }

    private void TryAddFortSlot(NpcSquadFort fort, MapGridComponent grid, Vector2i tile, Vector2i facing)
    {
        // The way in.
        if (tile == fort.Centre + fort.Entrance && facing == fort.Entrance)
            return;

        // A wall there closes that side already; one standing there already counts.
        if (!_tactical.IsFloorClear(fort.Grid, grid, tile))
            return;

        fort.Slots.Add(new NpcSquadFortSlot { Tile = tile, Facing = facing });
    }

    /// <summary>
    /// Which side of the square to leave open: the one facing away from the nearest enemy, or failing that the
    /// one behind the leader - and one that opens onto floor, not a wall.
    /// </summary>
    private Vector2i PickFortEntrance(Entity<NpcSquadLeaderComponent> leader, EntityUid gridUid, MapGridComponent grid,
        Vector2i centre)
    {
        var away = -GetHeading(leader, leader.Comp);

        foreach (var npc in leader.Comp.Members)
        {
            if (!_mobState.IsAlive(npc) || !_tactical.TryGetNearestThreat(npc, 14f, out var threat))
                continue;

            away = _transform.GetWorldPosition(leader) - _transform.GetWorldPosition(threat);
            break;
        }

        // Into the grid's own terms, since the square lines up with its tiles.
        away = (-_transform.GetWorldRotation(gridUid)).RotateVec(away);

        var best = FortSides[2];
        var bestScore = float.MinValue;

        foreach (var side in FortSides)
        {
            var score = Vector2.Dot(new Vector2(side.X, side.Y), away);

            // Opening onto a wall is no way in at all.
            if (!_tactical.IsFloorClear(gridUid, grid, centre + new Vector2i(side.X * 2, side.Y * 2)))
                score -= 100f;

            if (score <= bestScore)
                continue;

            bestScore = score;
            best = side;
        }

        return best;
    }

    /// <summary>
    /// Claims the edge of the ring this soldier should build next, see <see cref="TryFindFortSlot"/>.
    /// </summary>
    /// <param name="spot">The tile the barricade goes on.</param>
    /// <param name="stand">Where to stand to build it: on the same tile, back from the edge.</param>
    public bool TryClaimFortSlot(EntityUid npc, [NotNullWhen(true)] out EntityCoordinates? spot,
        [NotNullWhen(true)] out EntityCoordinates? stand, out Angle rotation)
    {
        spot = null;
        stand = null;
        rotation = Angle.Zero;

        if (!TryFindFortSlot(npc, out var slot, out var fort, out var grid))
            return false;

        slot.Builder = npc;
        slot.ClaimedUntil = _timing.CurTime + FortClaimTime;

        spot = _map.GridTileToLocal(fort.Grid, grid, slot.Tile);
        stand = spot.Value.Offset(new Vector2(slot.Facing.X, slot.Facing.Y) * -FortStandInset);
        rotation = new Vector2(slot.Facing.X, slot.Facing.Y).ToWorldAngle();
        return true;
    }

    /// <summary>
    /// The edge of the ring this soldier should build next: the one it's already on, or else the nearest one
    /// still open that nobody else is on - as long as it has the steel for it.
    /// </summary>
    private bool TryFindFortSlot(EntityUid npc,
        [NotNullWhen(true)] out NpcSquadFortSlot? slot,
        [NotNullWhen(true)] out NpcSquadFort? fort,
        [NotNullWhen(true)] out MapGridComponent? grid)
    {
        slot = null;
        fort = null;
        grid = null;

        if (!TryComp<NpcSquadMemberComponent>(npc, out var member) ||
            member.Order != NpcSquadOrder.Fortify ||
            !TryComp<NpcSquadLeaderComponent>(member.Leader, out var leader) ||
            leader.Fort is not { } candidate ||
            !TryComp(candidate.Grid, out grid) ||
            !TryComp<NpcTacticalComponent>(npc, out var tactical))
        {
            return false;
        }

        fort = candidate;

        var xform = Transform(npc);
        if (xform.GridUid != fort.Grid ||
            _tactical.CountMaterial(npc, tactical.BarricadeStackType) < tactical.BarricadeCost)
        {
            return false;
        }

        var now = _timing.CurTime;
        var ownTile = _map.TileIndicesFor(fort.Grid, grid, xform.Coordinates);
        var bestDistance = int.MaxValue;

        foreach (var candidateSlot in fort.Slots)
        {
            if (candidateSlot.Failures >= MaxFortFailures ||
                !_tactical.CanBuildFortBarricade(fort.Grid, grid, candidateSlot.Tile, candidateSlot.Facing))
            {
                continue;
            }

            if (candidateSlot.Builder == npc)
            {
                slot = candidateSlot;
                return true;
            }

            if (candidateSlot.Builder is { } other && now < candidateSlot.ClaimedUntil &&
                IsFortBuilder(other, member.Leader))
            {
                continue;
            }

            var offset = candidateSlot.Tile - ownTile;
            var distance = offset.X * offset.X + offset.Y * offset.Y;
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            slot = candidateSlot;
        }

        return slot != null;
    }

    private bool IsFortBuilder(EntityUid npc, EntityUid leader)
    {
        return _mobState.IsAlive(npc) &&
               TryComp<NpcSquadMemberComponent>(npc, out var member) &&
               member.Leader == leader &&
               member.Order == NpcSquadOrder.Fortify;
    }

    /// <summary>
    /// The soldier's go at its edge came to nothing. It lets go of it, and after a few such the squad gives
    /// up on that edge altogether.
    /// </summary>
    public void ReportFortFailure(EntityUid npc)
    {
        if (!TryComp<NpcSquadMemberComponent>(npc, out var member) ||
            !TryComp<NpcSquadLeaderComponent>(member.Leader, out var leader) ||
            leader.Fort is not { } fort)
        {
            return;
        }

        foreach (var slot in fort.Slots)
        {
            if (slot.Builder != npc)
                continue;

            slot.Builder = null;
            slot.Failures++;
        }
    }

    private static void ReleaseFortClaim(NpcSquadLeaderComponent leader, EntityUid npc)
    {
        if (leader.Fort is not { } fort)
            return;

        foreach (var slot in fort.Slots)
        {
            if (slot.Builder == npc)
                slot.Builder = null;
        }
    }

    /// <summary>
    /// Sends every soldier with nothing left to build into the ring to defend it, and tells the leader how it
    /// went once they all have.
    /// </summary>
    private void UpdateFort(EntityUid leaderUid, NpcSquadLeaderComponent leader)
    {
        var fort = leader.Fort;
        var grid = fort != null ? CompOrNull<MapGridComponent>(fort.Grid) : null;
        var anyBuilding = false;

        // The posts squadmates already hold, so nobody doubles up.
        _takenTiles.Clear();
        if (fort != null && grid != null)
        {
            foreach (var npc in leader.Members)
            {
                if (TryComp<NpcSquadMemberComponent>(npc, out var member) &&
                    member.Order == NpcSquadOrder.Defend &&
                    member.DefendPoint is { } post &&
                    post.EntityId == fort.Grid)
                {
                    _takenTiles.Add(_map.TileIndicesFor(fort.Grid, grid, post));
                }
            }
        }

        foreach (var npc in leader.Members.ToArray())
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || member.Order != NpcSquadOrder.Fortify)
                continue;

            if (_tactical.IsBusy(npc) || TryFindFortSlot(npc, out _, out _, out _))
            {
                anyBuilding = true;
                continue;
            }

            // Nothing left it can put up: into the square, and hold.
            var post = fort != null && grid != null ? GetFortPost(fort, grid, npc) : null;
            SetOrder(npc, member, NpcSquadOrder.Defend, post);
            _tactical.TryCallout(npc, NpcCalloutType.Acknowledge);
        }

        if (fort == null || grid == null || anyBuilding || fort.Reported)
            return;

        fort.Reported = true;

        var built = 0;
        foreach (var slot in fort.Slots)
        {
            if (_tactical.HasFortBarricade(fort.Grid, grid, slot.Tile, slot.Facing))
                built++;
        }

        _popup.PopupEntity(Loc.GetString("npc-squad-fort-done", ("built", built), ("total", fort.Slots.Count)),
            leaderUid, leaderUid, built < fort.Slots.Count ? PopupType.SmallCaution : PopupType.Small);
    }

    /// <summary>
    /// The free tile inside the ring nearest <paramref name="npc"/>, to defend it from.
    /// </summary>
    private EntityCoordinates? GetFortPost(NpcSquadFort fort, MapGridComponent grid, EntityUid npc)
    {
        var ownTile = _map.TileIndicesFor(fort.Grid, grid, Transform(npc).Coordinates);
        Vector2i? best = null;
        var bestDistance = int.MaxValue;

        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                var tile = fort.Centre + new Vector2i(x, y);

                if (_takenTiles.Contains(tile) || !_tactical.IsFloorClear(fort.Grid, grid, tile))
                    continue;

                var offset = tile - ownTile;
                var distance = offset.X * offset.X + offset.Y * offset.Y;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = tile;
            }
        }

        if (best is not { } chosen)
            return null;

        _takenTiles.Add(chosen);
        return _map.GridTileToLocal(fort.Grid, grid, chosen);
    }
}
