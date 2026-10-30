namespace Content.Server._Crescent.Territory;

[ByRefEvent]
public readonly record struct PersistentPoiGridLoadedEvent(EntityUid Grid);

[ByRefEvent]
public readonly record struct PersistentRegionRegisteredEvent(EntityUid Region, EntityUid Grid, string RegionId, bool FromSave);

[ByRefEvent]
public readonly record struct PersistentRegionOwnerChangedEvent(EntityUid Grid, string? Owner);

[ByRefEvent]
public readonly record struct PersistentRegionNeutralizedEvent(EntityUid Region, EntityUid Grid, string RegionId, string PreviousOwner);

[ByRefEvent]
public readonly record struct PersistentPoiDevelopmentChangedEvent(EntityUid Grid);
