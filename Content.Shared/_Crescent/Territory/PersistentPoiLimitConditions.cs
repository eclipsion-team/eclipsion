using Content.Shared.Construction;
using Content.Shared.Construction.Conditions;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Shared._Crescent.Territory;

[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiWithinLimit : IConstructionCondition
{
    [DataField(required: true)]
    public PersistentPoiWorksCategory Category;

    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();

        return entMan.System<SharedTransformSystem>().GetGrid(location) is { } grid &&
               entMan.System<PersistentPoiLevelSystem>().HasRoomFor(grid, Category);
    }

    public ConstructionGuideEntry GenerateGuideEntry()
    {
        return new ConstructionGuideEntry
        {
            Localization = "construction-condition-persistent-poi-within-limit",
        };
    }
}

[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiEnforceLimit : IGraphAction
{
    [DataField(required: true)]
    public PersistentPoiWorksCategory Category;

    public void PerformAction(EntityUid uid, EntityUid? userUid, IEntityManager entityManager)
    {
        var ev = new PersistentPoiBuiltEvent(Category, userUid);
        entityManager.EventBus.RaiseLocalEvent(uid, ref ev);
    }
}

[ByRefEvent]
public readonly record struct PersistentPoiBuiltEvent(PersistentPoiWorksCategory Category, EntityUid? User);

[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiFactionRequired : IConstructionCondition
{
    [DataField(required: true)]
    public string Faction = string.Empty;

    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        return string.Equals(PersistentTerritoryFactions.GetTeam(entMan, user), Faction, StringComparison.Ordinal);
    }

    public ConstructionGuideEntry GenerateGuideEntry()
    {
        return new ConstructionGuideEntry
        {
            Localization = "construction-condition-persistent-poi-faction",
        };
    }
}

[UsedImplicitly]
[DataDefinition]
public sealed partial class PersistentPoiCapabilityRequired : IConstructionCondition
{
    [DataField(required: true)]
    public ProtoId<PersistentPoiCapabilityPrototype> Capability;

    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();

        return entMan.System<SharedTransformSystem>().GetGrid(location) is { } grid &&
               entMan.TryGetComponent(grid, out PersistentPoiGridComponent? territory) &&
               territory.Capability == Capability;
    }

    public ConstructionGuideEntry GenerateGuideEntry()
    {
        return new ConstructionGuideEntry
        {
            Localization = "construction-condition-persistent-poi-capability",
        };
    }
}
