using Content.Shared.Construction;
using Content.Shared.Construction.Conditions;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Content.Shared.Stacks;
using JetBrains.Annotations;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PersistentPoiMaterialSiloComponent : Component
{
    // not a DataField on purpose: the round-end save drops it, so the silo starts every round empty
    [ViewVariables, AutoNetworkedField]
    public Dictionary<ProtoId<StackPrototype>, int> Stored = new();
}

[Serializable, NetSerializable]
public sealed class PersistentPoiSiloBuildRequestEvent : EntityEventArgs
{
    public ProtoId<ConstructionPrototype> Recipe;
    public NetCoordinates Location;
    public Direction Direction;

    public PersistentPoiSiloBuildRequestEvent(ProtoId<ConstructionPrototype> recipe, NetCoordinates location, Direction direction)
    {
        Recipe = recipe;
        Location = location;
        Direction = direction;
    }
}

public static class PersistentPoiSilo
{
    /// <summary>
    /// Every material the recipe takes from start to finish, and whether that is all it takes.
    /// </summary>
    public static Dictionary<ProtoId<StackPrototype>, int> GetCosts(
        IPrototypeManager proto,
        ConstructionPrototype recipe,
        out bool materialsOnly,
        out float doAfter)
    {
        var costs = new Dictionary<ProtoId<StackPrototype>, int>();
        materialsOnly = false;
        doAfter = 0f;

        if (!proto.TryIndex(recipe.Graph, out var graph) ||
            !graph.Nodes.TryGetValue(recipe.StartNode, out var node) ||
            graph.Path(recipe.StartNode, recipe.TargetNode) is not { Length: > 0 } path)
        {
            return costs;
        }

        materialsOnly = true;
        foreach (var next in path)
        {
            if (node.GetEdge(next.Name) is not { } edge)
            {
                materialsOnly = false;
                break;
            }

            if (edge.Conditions.Count > 0)
                materialsOnly = false;

            foreach (var step in edge.Steps)
            {
                doAfter += step.DoAfter;
                if (step is MaterialConstructionGraphStep material)
                    costs[material.MaterialPrototypeId] = costs.GetValueOrDefault(material.MaterialPrototypeId) + material.Amount;
                else
                    materialsOnly = false;
            }

            node = next;
        }

        return costs;
    }

    public static bool TryGetSilo(IEntityManager entMan, EntityUid grid, out Entity<PersistentPoiMaterialSiloComponent> silo)
    {
        var query = entMan.EntityQueryEnumerator<PersistentPoiMaterialSiloComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.GridUid != grid || !xform.Anchored)
                continue;

            silo = (uid, comp);
            return true;
        }

        silo = default;
        return false;
    }

    public static bool Covers(PersistentPoiMaterialSiloComponent silo, Dictionary<ProtoId<StackPrototype>, int> costs)
    {
        foreach (var (material, amount) in costs)
        {
            if (silo.Stored.GetValueOrDefault(material) < amount)
                return false;
        }

        return true;
    }
}

[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiNoPrototypeInTile : IConstructionCondition
{
    [DataField(required: true)]
    public EntProtoId Prototype;

    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var proto = IoCManager.Resolve<IPrototypeManager>();
        var maps = entMan.System<SharedMapSystem>();

        if (entMan.System<SharedTransformSystem>().GetGrid(location) is not { } grid ||
            !entMan.TryGetComponent(grid, out MapGridComponent? mapGrid))
        {
            return false;
        }

        var tile = maps.TileIndicesFor(grid, mapGrid, location);
        foreach (var anchored in maps.GetAnchoredEntities(grid, mapGrid, tile))
        {
            if (entMan.GetComponent<MetaDataComponent>(anchored).EntityPrototype is not { } entProto)
                continue;

            if (entProto.ID == Prototype)
                return false;

            foreach (var parent in proto.EnumerateParents<EntityPrototype>(entProto.ID))
            {
                if (parent.ID == Prototype)
                    return false;
            }
        }

        return true;
    }

    public ConstructionGuideEntry GenerateGuideEntry()
    {
        return new ConstructionGuideEntry
        {
            Localization = "construction-condition-persistent-poi-no-duplicate",
        };
    }
}
