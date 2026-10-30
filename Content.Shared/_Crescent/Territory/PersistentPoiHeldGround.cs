using Content.Shared.Construction;
using Content.Shared.Construction.Conditions;
using JetBrains.Annotations;
using Robust.Shared.Map;

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
