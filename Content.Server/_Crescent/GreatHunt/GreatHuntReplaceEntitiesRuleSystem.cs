using Content.Server.GameTicking.Rules;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Runs <see cref="GreatHuntReplaceEntitiesRuleComponent"/>: on the rule's first tick every entity whose prototype
/// is a key of <see cref="GreatHuntReplaceEntitiesRuleComponent.Replacements"/> is deleted and its replacement
/// spawned on the same spot, facing the same way and anchored the same.
/// </summary>
public sealed class GreatHuntReplaceEntitiesRuleSystem : GameRuleSystem<GreatHuntReplaceEntitiesRuleComponent>
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    protected override void ActiveTick(EntityUid uid, GreatHuntReplaceEntitiesRuleComponent rule,
        GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, rule, gameRule, frameTime);

        // Run on the first tick rather than in Started: the maps are loaded by another rule of the same preset, and
        // by now every rule has started and the maps are initialised.
        if (rule.Done)
            return;

        rule.Done = true;

        var found = new List<(EntityUid Uid, EntProtoId Replacement)>();
        var query = EntityQueryEnumerator<MetaDataComponent>();
        while (query.MoveNext(out var ent, out var meta))
        {
            if (meta.EntityPrototype is { } proto && rule.Replacements.TryGetValue(proto.ID, out var replacement))
                found.Add((ent, replacement));
        }

        foreach (var (ent, replacement) in found)
        {
            var xform = Transform(ent);
            var coords = xform.Coordinates;
            var rotation = xform.LocalRotation;
            var anchored = xform.Anchored;

            // Gone first, so the replacement does not anchor on top of it.
            Del(ent);

            var spawned = Spawn(replacement, coords);
            _transform.SetLocalRotation(spawned, rotation);

            var spawnedXform = Transform(spawned);
            if (anchored && !spawnedXform.Anchored)
                _transform.AnchorEntity(spawned, spawnedXform);
            else if (!anchored && spawnedXform.Anchored)
                _transform.Unanchor(spawned, spawnedXform);
        }

        Log.Info($"Great Hunt rule {ToPrettyString(uid)} replaced {found.Count} mapped entities.");
    }
}
