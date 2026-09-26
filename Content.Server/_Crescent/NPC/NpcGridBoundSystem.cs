using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Server.NPC.Systems;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcGridBoundComponent"/>
public sealed class NpcGridBoundSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;

    private EntityQuery<NpcGridBoundComponent> _boundQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<NpcSquadMemberComponent> _squadQuery;

    public override void Initialize()
    {
        base.Initialize();

        _boundQuery = GetEntityQuery<NpcGridBoundComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _squadQuery = GetEntityQuery<NpcSquadMemberComponent>();
    }

    /// <summary>
    /// Marks every steering direction that would take the NPC off its grid as fully dangerous, so steering
    /// never picks it. Called by <see cref="NPCSteeringSystem"/> once it has its blended danger map.
    /// </summary>
    public void AvoidLeavingGrid(
        EntityUid uid,
        TransformComponent xform,
        Vector2 worldPos,
        Angle offsetRot,
        float agentRadius,
        float[] danger)
    {
        if (!_boundQuery.TryComp(uid, out var bound))
            return;

        // Following a player: it goes where they go, and stays wherever they leave it.
        if (_squadQuery.HasComp(uid))
        {
            bound.Grid = null;
            return;
        }

        if (bound.Grid is { } old && TerminatingOrDeleted(old))
            bound.Grid = null;

        bound.Grid ??= xform.GridUid;

        // Already off it - knocked or dragged away. Blocking it here would only pin it in place.
        if (bound.Grid is not { } gridUid ||
            xform.GridUid != gridUid ||
            !_gridQuery.TryComp(gridUid, out var grid))
        {
            return;
        }

        var reach = agentRadius + bound.Lookahead;

        for (var i = 0; i < NPCSteeringSystem.InterestDirections; i++)
        {
            // Steering directions are relative to the grid; take them back to world space.
            var worldDir = (-offsetRot).RotateVec(NPCSteeringSystem.Directions[i]);
            var probe = worldPos + worldDir * reach;

            if (_map.TryGetTileRef(gridUid, grid, probe, out var tile) && !tile.Tile.IsEmpty)
                continue;

            danger[i] = 1f;
        }
    }
}
