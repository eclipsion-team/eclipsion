using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._Crescent.Territory;

[DataDefinition]
public sealed partial class PersistentPoiBuildCategory
{
    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public SpriteSpecifier Icon = SpriteSpecifier.Invalid;

    [DataField(required: true)]
    public List<ProtoId<ConstructionPrototype>> Recipes = new();
}

public enum PersistentPoiBuildBlock : byte
{
    None,

    NotHeld,

    OtherFaction,

    WrongLine,

    NeedsLevel,

    LimitReached,
}

public readonly record struct PersistentPoiRecipeStatus(
    PersistentPoiBuildBlock Block,
    PersistentPoiWorksCategory? Category = null,
    int Count = 0,
    int? Limit = null,
    int? Level = null,
    ProtoId<PersistentPoiCapabilityPrototype>? Line = null);
