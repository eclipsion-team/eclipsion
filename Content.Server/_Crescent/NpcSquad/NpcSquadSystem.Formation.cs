using System.Numerics;
using Content.Server._Crescent.NPC;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: formations. With anything but <see cref="NpcSquadFormation.Loose"/>, every soldier following the
/// leader has a slot of its own, laid out relative to the way the leader is heading, and keeps to it while
/// there is nothing to fight. See <c>NpcKeepFormationOperator</c>.
/// </summary>
public sealed partial class NpcSquadSystem
{
    /// <summary>
    /// Distance between neighbours in a formation.
    /// </summary>
    private const float FormationSpacing = 1.5f;

    /// <summary>
    /// How close to its slot a soldier has to be to count as in it.
    /// </summary>
    private const float FormationArriveRange = 0.35f;

    /// <summary>
    /// How close to the leader a soldier keeps when its slot is inside a wall.
    /// </summary>
    private const float FormationFallbackRange = 2f;

    /// <summary>
    /// How far the leader has to walk before the way they are heading is worked out again. Short enough to
    /// follow them round a corner, long enough that shuffling on the spot doesn't swing the formation round.
    /// </summary>
    private const float HeadingSampleDistance = 0.75f;

    private void UpdateHeadings()
    {
        var query = EntityQueryEnumerator<NpcSquadLeaderComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var leader, out var xform))
        {
            var pos = _transform.GetMapCoordinates(uid, xform);

            if (pos.MapId != leader.HeadingSample.MapId)
            {
                leader.HeadingSample = pos;
                continue;
            }

            var delta = pos.Position - leader.HeadingSample.Position;
            if (delta.LengthSquared() < HeadingSampleDistance * HeadingSampleDistance)
                continue;

            leader.Heading = Vector2.Normalize(delta);
            leader.HeadingSample = pos;
        }
    }

    /// <summary>
    /// Which way the leader is heading, in world terms: the way they last walked, or failing that the way they
    /// face.
    /// </summary>
    private Vector2 GetHeading(EntityUid leaderUid, NpcSquadLeaderComponent leader)
    {
        return leader.Heading ?? _transform.GetWorldRotation(leaderUid).ToWorldVec();
    }

    /// <summary>
    /// Turns an offset given as (right, forward) of <paramref name="forward"/> into world terms.
    /// </summary>
    private static Vector2 ToWorldOffset(Vector2 forward, Vector2 offset)
    {
        var right = new Vector2(forward.Y, -forward.X);
        return right * offset.X + forward * offset.Y;
    }

    /// <summary>
    /// Where the soldier at <paramref name="index"/> of <paramref name="count"/> stands in a formation, as
    /// (right, forward) of the leader.
    /// </summary>
    public static Vector2 GetFormationOffset(NpcSquadFormation formation, int index, int count)
    {
        // Every other one goes to the left, starting with the first.
        var side = index % 2 == 0 ? -1f : 1f;
        var rank = index / 2 + 1;

        return formation switch
        {
            NpcSquadFormation.Column => new Vector2(0f, -FormationSpacing * (index + 1)),
            NpcSquadFormation.Staggered => new Vector2(side * FormationSpacing * 0.5f, -FormationSpacing * 0.75f * (index + 1)),
            NpcSquadFormation.Line => new Vector2(side * FormationSpacing * rank, -0.5f),
            NpcSquadFormation.Wedge => new Vector2(side * FormationSpacing * 0.8f * rank, -FormationSpacing * 0.8f * rank),
            NpcSquadFormation.Circle => GetCircleOffset(index, count, MathF.Max(FormationSpacing, count * 0.4f)),
            _ => Vector2.Zero,
        };
    }

    private static Vector2 GetCircleOffset(int index, int count, float radius)
    {
        // Straight behind the leader first, then on round.
        var angle = MathF.PI + MathF.Tau * index / Math.Max(count, 1);
        return new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * radius;
    }

    /// <summary>
    /// Where this soldier should be in its squad's formation right now.
    /// </summary>
    /// <param name="slot">Relative to the leader, so it moves along with them between updates.</param>
    /// <param name="range">How close to <paramref name="slot"/> counts as in place.</param>
    /// <returns>False when it isn't following anyone in formation.</returns>
    public bool TryGetFormationSlot(EntityUid npc, out EntityCoordinates slot, out float range)
    {
        slot = default;
        range = 0f;

        if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || !KeepsFormation(member))
            return false;

        var leaderUid = member.Leader;

        if (!TryComp<NpcSquadLeaderComponent>(leaderUid, out var leader) ||
            leader.Formation == NpcSquadFormation.Loose ||
            !_mobState.IsAlive(leaderUid))
        {
            return false;
        }

        // Counted among those in formation only, so the gaps close up when one of them drops out or goes
        // off to do something else.
        var index = -1;
        var count = 0;

        foreach (var other in leader.Members)
        {
            if (!TryComp<NpcSquadMemberComponent>(other, out var otherMember) ||
                !KeepsFormation(otherMember) ||
                !_mobState.IsAlive(other))
            {
                continue;
            }

            if (other == npc)
                index = count;

            count++;
        }

        if (index < 0)
            return false;

        var leaderXform = Transform(leaderUid);
        var leaderMap = _transform.GetMapCoordinates(leaderUid, leaderXform);
        var worldOffset = ToWorldOffset(GetHeading(leaderUid, leader), GetFormationOffset(leader.Formation, index, count));

        // A slot inside a wall: closer in, and failing that just keep near the leader.
        if (leaderXform.GridUid is { } gridUid &&
            !_tactical.IsStandableAt(gridUid, new MapCoordinates(leaderMap.Position + worldOffset, leaderMap.MapId)))
        {
            worldOffset *= 0.5f;

            if (!_tactical.IsStandableAt(gridUid, new MapCoordinates(leaderMap.Position + worldOffset, leaderMap.MapId)))
            {
                slot = new EntityCoordinates(leaderUid, Vector2.Zero);
                range = FormationFallbackRange;
                return true;
            }
        }

        // The leader's own rotation is taken back out: the formation goes by where they walk, not where
        // they look.
        var leaderRotation = _transform.GetWorldRotation(leaderXform);
        slot = new EntityCoordinates(leaderUid, (-leaderRotation).RotateVec(worldOffset));
        range = FormationArriveRange;
        return true;
    }

    private static bool KeepsFormation(NpcSquadMemberComponent member)
    {
        return member.Order is NpcSquadOrder.Follow or NpcSquadOrder.Attack;
    }

    /// <summary>
    /// A post in a ring round the leader for the squadmate at <paramref name="index"/> of
    /// <paramref name="count"/>, when they go down. Nobody gets the leader's own tile, or one someone else
    /// already has - see <see cref="_takenTiles"/>, cleared by the caller.
    /// </summary>
    private EntityCoordinates? GetRingPost(Entity<NpcSquadLeaderComponent> leader, int index, int count)
    {
        var xform = Transform(leader);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        _takenTiles.Add(_map.TileIndicesFor(gridUid, grid, xform.Coordinates));

        var centre = _transform.GetMapCoordinates(leader, xform);
        var offset = ToWorldOffset(GetHeading(leader, leader.Comp), GetCircleOffset(index, count, LeaderDownRingRadius));
        var desired = new MapCoordinates(centre.Position + offset, centre.MapId);

        if (!_tactical.TryFindStandableNear(gridUid, desired, 1, _takenTiles, out var spot, out var tile))
            return null;

        _takenTiles.Add(tile);
        return spot;
    }

    private void OnFormationMessage(Entity<NpcSquadLeaderComponent> ent, ref NpcSquadFormationMessage args)
    {
        SetFormation(ent, args.Formation);
    }

    /// <summary>
    /// Changes the formation the squad keeps while following its leader.
    /// </summary>
    public void SetFormation(Entity<NpcSquadLeaderComponent> ent, NpcSquadFormation formation)
    {
        if (ent.Comp.Formation == formation)
            return;

        ent.Comp.Formation = formation;

        var acknowledged = false;
        foreach (var npc in ent.Comp.Members.ToArray())
        {
            if (!TryComp<NpcSquadMemberComponent>(npc, out var member) || !KeepsFormation(member))
                continue;

            // Straight into it, rather than whenever what it's doing now happens to end.
            ForceReplan(npc);

            if (!acknowledged)
                acknowledged = _tactical.TryCallout(npc, NpcCalloutType.Acknowledge, force: true);
        }

        _popup.PopupEntity(Loc.GetString($"npc-squad-formation-given-{formation.ToString().ToLowerInvariant()}"), ent, ent, PopupType.Small);
        UpdateUi(ent, ent.Comp);
    }
}
