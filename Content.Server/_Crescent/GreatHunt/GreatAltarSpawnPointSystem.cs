using System.Linq;
using Robust.Shared.Random;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Resolves <see cref="GreatAltarSpawnPointComponent"/> markers: one weighted pick per grid, one altar, no markers left.
/// </summary>
/// <remarks>
/// MapInit fires marker by marker while the grid is still loading, so a marker cannot see its siblings yet. Grids
/// are only queued there and resolved on the next update, once every marker on them exists.
/// </remarks>
public sealed class GreatAltarSpawnPointSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly HashSet<EntityUid> _pendingGrids = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GreatAltarSpawnPointComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<GreatAltarSpawnPointComponent> ent, ref MapInitEvent args)
    {
        // A marker loose in space has no siblings to compete with; it simply resolves on its own.
        _pendingGrids.Add(Transform(ent).GridUid ?? ent.Owner);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pendingGrids.Count == 0)
            return;

        // Spawning the altar runs MapInit, which could queue another grid; never enumerate the live set.
        var grids = _pendingGrids.ToArray();
        _pendingGrids.Clear();

        foreach (var grid in grids)
        {
            ResolveGrid(grid);
        }
    }

    private void ResolveGrid(EntityUid grid)
    {
        var candidates = new List<(EntityUid Uid, GreatAltarSpawnPointComponent Comp, TransformComponent Xform)>();
        var totalWeight = 0f;

        var query = EntityQueryEnumerator<GreatAltarSpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if ((xform.GridUid ?? uid) != grid || TerminatingOrDeleted(uid))
                continue;

            candidates.Add((uid, comp, xform));
            totalWeight += MathF.Max(comp.Weight, 0f);
        }

        if (candidates.Count == 0)
            return;

        var chosen = candidates[^1];
        if (totalWeight > 0f)
        {
            var roll = _random.NextFloat(totalWeight);
            foreach (var candidate in candidates)
            {
                roll -= MathF.Max(candidate.Comp.Weight, 0f);
                if (roll >= 0f)
                    continue;

                chosen = candidate;
                break;
            }
        }
        else
        {
            chosen = _random.Pick(candidates);
        }

        var altar = Spawn(chosen.Comp.Prototype, chosen.Xform.Coordinates);
        _transform.SetLocalRotation(altar, chosen.Xform.LocalRotation);

        foreach (var candidate in candidates)
        {
            QueueDel(candidate.Uid);
        }
    }
}
