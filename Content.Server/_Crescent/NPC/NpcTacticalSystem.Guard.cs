using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NPC;

public sealed partial class NpcTacticalSystem
{
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;

    /// <summary>
    /// Once the leader is further than this from a guard, it goes and takes up a new spot around them.
    /// </summary>
    private const float GuardBreakDistance = 4.5f;

    /// <summary>
    /// How far around its slot in the ring a guard looks for floor to stand on.
    /// </summary>
    private const int GuardSpotSearch = 2;

    /// <summary>
    /// Candidate tiles checked for a clear line back to the leader before the search gives up.
    /// </summary>
    private const int MaxGuardSpotChecks = 8;

    /// <summary>
    /// Whether this NPC should stand watch: another squadmate is patching its leader up.
    /// </summary>
    public bool ShouldGuardLeader(EntityUid npc, NpcTacticalComponent? comp = null)
    {
        return Resolve(npc, ref comp, false) && TryGetGuardedLeader(npc, out _, out _);
    }

    private bool TryGetGuardedLeader(EntityUid npc, out EntityUid leader, out EntityUid medic)
    {
        leader = EntityUid.Invalid;
        medic = EntityUid.Invalid;

        if (!_mobState.IsAlive(npc) || !_squad.TryGetLeader(npc, out leader) || _mobState.IsDead(leader))
            return false;

        return _squad.TryGetActiveMedic(leader, out medic) && medic != npc;
    }

    /// <summary>
    /// Picks where this NPC stands while a squadmate patches the leader up. The guards share out a ring around
    /// the leader, the first slot straight across from the medic, so between them they watch every way in
    /// and nobody stands in the medic's way.
    /// </summary>
    public bool TryPickGuardSpot(EntityUid npc, [NotNullWhen(true)] out EntityCoordinates? spot)
    {
        spot = null;

        if (!TryComp<NpcTacticalComponent>(npc, out var comp) || !TryGetGuardedLeader(npc, out var leader, out var medic))
            return false;

        var guards = 0;
        var slot = -1;

        foreach (var member in _squad.GetActiveMembers(leader))
        {
            if (member == medic)
                continue;

            if (member == npc)
                slot = guards;

            guards++;
        }

        if (slot < 0)
            return false;

        var leaderXform = Transform(leader);

        // Nowhere to spread out to off a grid; the NPC just keeps following.
        if (leaderXform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var leaderMap = _transform.GetMapCoordinates(leader, leaderXform);
        var awayFromMedic = leaderMap.Position - _transform.GetMapCoordinates(medic).Position;
        var baseAngle = awayFromMedic.LengthSquared() > 0.01f ? awayFromMedic.ToWorldAngle() : Angle.Zero;
        var angle = baseAngle + new Angle(MathF.Tau * slot / guards);

        var ideal = leaderMap.Position + angle.ToWorldVec() * comp.GuardRadius;
        var idealLocal = Vector2.Transform(ideal, _transform.GetInvWorldMatrix(gridUid));
        var origin = _map.LocalToTile(gridUid, grid, new EntityCoordinates(gridUid, idealLocal));
        var leaderTile = _map.TileIndicesFor(gridUid, grid, leaderXform.Coordinates);
        var medicTile = _map.TileIndicesFor(gridUid, grid, Transform(medic).Coordinates);

        _layerCache.Clear();
        _candidates.Clear();

        for (var dx = -GuardSpotSearch; dx <= GuardSpotSearch; dx++)
        {
            for (var dy = -GuardSpotSearch; dy <= GuardSpotSearch; dy++)
            {
                var indices = origin + new Vector2i(dx, dy);

                if (indices == leaderTile || indices == medicTile || !IsStandable(gridUid, grid, indices))
                    continue;

                _candidates.Add((-(TileCentre(grid, indices) - idealLocal).Length(), indices));
            }
        }

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // No use watching the leader's back from the far side of a wall.
        var checks = Math.Min(_candidates.Count, MaxGuardSpotChecks);
        for (var i = 0; i < checks; i++)
        {
            var coords = _map.GridTileToLocal(gridUid, grid, _candidates[i].Indices);

            if (!HasClearShot(npc, _transform.ToMapCoordinates(coords), leader, leaderMap, checkFriendlies: false))
                continue;

            spot = coords;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Called as the NPC takes up its guard spot.
    /// </summary>
    public void BeginGuard(EntityUid npc, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(npc, ref comp, false))
            return;

        // Look out straight away rather than finishing whatever it was facing.
        comp.NextGuardLook = _timing.CurTime;
        TryCallout(npc, NpcCalloutType.Guard, comp: comp);
    }

    /// <summary>
    /// One tick of standing watch: turns the NPC to look out and away from the leader, a different way every
    /// few seconds. Spotting anything is up to the NPC's combat, which takes over from guarding on its own.
    /// </summary>
    /// <returns>False once there's nothing left to guard, or the leader has moved off without it.</returns>
    public bool UpdateGuard(EntityUid npc, float frameTime, NpcTacticalComponent? comp = null)
    {
        if (!Resolve(npc, ref comp, false) || !TryGetGuardedLeader(npc, out var leader, out _))
            return false;

        var xform = Transform(npc);
        var ownPos = _transform.GetMapCoordinates(npc, xform);
        var leaderPos = _transform.GetMapCoordinates(leader);

        if (ownPos.MapId != leaderPos.MapId)
            return false;

        var fromLeader = ownPos.Position - leaderPos.Position;

        if (fromLeader.Length() > GuardBreakDistance)
            return false;

        var now = _timing.CurTime;

        if (now >= comp.NextGuardLook)
        {
            var outward = fromLeader.LengthSquared() > 0.01f ? fromLeader.ToWorldAngle() : _random.NextAngle();
            var spread = (float) comp.GuardLookSpread.Theta;
            comp.GuardLookAngle = outward + new Angle(_random.NextFloat(-spread, spread));

            var span = (comp.MaxGuardLookTime - comp.MinGuardLookTime).TotalSeconds;
            comp.NextGuardLook = now + comp.MinGuardLookTime + TimeSpan.FromSeconds(_random.NextDouble() * span);
        }

        _rotate.TryRotateTo(npc, comp.GuardLookAngle, frameTime, Angle.FromDegrees(5), comp.GuardTurnSpeed, xform);
        return true;
    }
}
