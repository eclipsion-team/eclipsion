using Content.Shared.Construction;
using Content.Shared.Construction.Conditions;
using Content.Shared.Tag;
using Content.Shared.Wall;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Shared._Crescent.Territory;

[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiHeldGround : IConstructionCondition
{
    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var xforms = entMan.System<SharedTransformSystem>();

        if (xforms.GetGrid(location) is not { } grid ||
            !entMan.TryGetComponent(grid, out PersistentPoiGridComponent? territory) ||
            territory.Owner == null)
        {
            return false;
        }

        if (!entMan.TryGetComponent(user, out TransformComponent? userXform) || userXform.GridUid != grid)
            return false;

        return string.Equals(PersistentTerritoryFactions.GetTeam(entMan, user), territory.Owner, StringComparison.Ordinal);
    }

    public ConstructionGuideEntry GenerateGuideEntry()
    {
        return new ConstructionGuideEntry
        {
            Localization = "construction-condition-persistent-poi-held-ground",
        };
    }
}

// posters: on a wall tile, one wallmount per tile. no facing check so it works from the construction console too
[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiOnWall : IConstructionCondition
{
    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var maps = entMan.System<SharedMapSystem>();
        var tags = entMan.System<TagSystem>();

        if (entMan.System<SharedTransformSystem>().GetGrid(location) is not { } grid ||
            !entMan.TryGetComponent(grid, out MapGridComponent? mapGrid))
        {
            return false;
        }

        var wall = false;
        foreach (var anchored in maps.GetAnchoredEntities(grid, mapGrid, maps.TileIndicesFor(grid, mapGrid, location)))
        {
            if (entMan.HasComponent<WallMountComponent>(anchored))
                return false;

            if (tags.HasTag(anchored, "Wall"))
                wall = true;
        }

        return wall;
    }

    public ConstructionGuideEntry GenerateGuideEntry()
    {
        return new ConstructionGuideEntry
        {
            Localization = "construction-condition-persistent-poi-on-wall",
        };
    }
}
