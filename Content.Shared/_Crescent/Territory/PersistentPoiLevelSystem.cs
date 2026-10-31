using Content.Shared.Construction.Prototypes;
using Content.Shared.PointCannons;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared._Crescent.Territory;

public sealed class PersistentPoiLevelSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private readonly Dictionary<int, PersistentPoiLevelPrototype> _levels = new();

    public int MaxLevel { get; private set; } = 1;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        LoadLevels();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<PersistentPoiLevelPrototype>())
            LoadLevels();
    }

    private void LoadLevels()
    {
        _levels.Clear();
        MaxLevel = 1;

        foreach (var level in _proto.EnumeratePrototypes<PersistentPoiLevelPrototype>())
        {
            _levels[level.Level] = level;
            MaxLevel = Math.Max(MaxLevel, level.Level);
        }
    }

    public PersistentPoiLevelPrototype? GetLevel(int level)
    {
        return _levels.GetValueOrDefault(Math.Clamp(level, 1, MaxLevel));
    }

    public PersistentPoiLevelPrototype? GetNextLevel(int level)
    {
        return level >= MaxLevel ? null : _levels.GetValueOrDefault(level + 1);
    }

    public int? GetLimit(PersistentPoiGridComponent territory, PersistentPoiWorksCategory category)
    {
        return GetLevel(territory.Level) is { } level && level.Limits.TryGetValue(category, out var limit)
            ? limit
            : null;
    }

    public int CountWorks(EntityUid grid, PersistentPoiWorksCategory category, PersistentPoiGridComponent? territory = null)
    {
        if (_net.IsClient)
            return Resolve(grid, ref territory, false) ? territory.WorksCounts.GetValueOrDefault(category) : 0;

        return CountWorksLive(grid, category);
    }

    public int CountWorksLive(EntityUid grid, PersistentPoiWorksCategory category)
    {
        var count = 0;

        if (category == PersistentPoiWorksCategory.PointDefence)
        {
            var guns = AllEntityQuery<PointCannonComponent, TransformComponent>();
            while (guns.MoveNext(out _, out _, out var xform))
            {
                if (xform.GridUid == grid && xform.Anchored)
                    count++;
            }

            return count;
        }

        var works = AllEntityQuery<PersistentPoiWorksComponent, TransformComponent>();
        while (works.MoveNext(out _, out var comp, out var xform))
        {
            if (comp.Category == category && xform.GridUid == grid)
                count++;
        }

        return count;
    }

    /// <summary>
    /// <see cref="CountWorksLive"/> for every grid keyed in <paramref name="counts"/> and every category at once,
    /// in one pass over the guns and one over the works.
    /// </summary>
    public void CountAllWorksLive(Dictionary<EntityUid, Dictionary<PersistentPoiWorksCategory, int>> counts)
    {
        foreach (var perGrid in counts.Values)
        {
            perGrid.Clear();
        }

        var guns = AllEntityQuery<PointCannonComponent, TransformComponent>();
        while (guns.MoveNext(out _, out _, out var xform))
        {
            if (xform.Anchored && xform.GridUid is { } grid && counts.TryGetValue(grid, out var perGrid))
                perGrid[PersistentPoiWorksCategory.PointDefence] = perGrid.GetValueOrDefault(PersistentPoiWorksCategory.PointDefence) + 1;
        }

        var works = AllEntityQuery<PersistentPoiWorksComponent, TransformComponent>();
        while (works.MoveNext(out _, out var comp, out var xform))
        {
            // point defence goes by the guns themselves
            if (comp.Category != PersistentPoiWorksCategory.PointDefence &&
                xform.GridUid is { } grid &&
                counts.TryGetValue(grid, out var perGrid))
            {
                perGrid[comp.Category] = perGrid.GetValueOrDefault(comp.Category) + 1;
            }
        }
    }

    public bool HasRoomFor(EntityUid grid, PersistentPoiWorksCategory category)
    {
        if (!TryComp<PersistentPoiGridComponent>(grid, out var territory))
            return false;

        return GetLimit(territory, category) is not { } limit || CountWorks(grid, category, territory) < limit;
    }

    public int? FirstLevelAllowing(PersistentPoiWorksCategory category, int count)
    {
        for (var level = 1; level <= MaxLevel; level++)
        {
            if (_levels.TryGetValue(level, out var proto) &&
                (!proto.Limits.TryGetValue(category, out var limit) || limit >= count))
            {
                return level;
            }
        }

        return null;
    }

    public PersistentPoiRecipeStatus GetRecipeStatus(
        Entity<PersistentPoiGridComponent> territory,
        ConstructionPrototype recipe,
        string? faction)
    {
        PersistentPoiWorksCategory? category = null;
        ProtoId<PersistentPoiCapabilityPrototype>? line = null;

        foreach (var condition in recipe.Conditions)
        {
            switch (condition)
            {
                case PersistentPoiFactionRequired required when !string.Equals(required.Faction, faction, StringComparison.Ordinal):
                    return new PersistentPoiRecipeStatus(PersistentPoiBuildBlock.OtherFaction);
                case PersistentPoiCapabilityRequired capability:
                    line = capability.Capability;
                    break;
                case PersistentPoiWithinLimit within:
                    category = within.Category;
                    break;
            }
        }

        var count = category is { } counted ? CountWorks(territory, counted, territory.Comp) : 0;
        var limit = category is { } limited ? GetLimit(territory.Comp, limited) : null;

        if (territory.Comp.Owner == null || !string.Equals(territory.Comp.Owner, faction, StringComparison.Ordinal))
            return new PersistentPoiRecipeStatus(PersistentPoiBuildBlock.NotHeld, category, count, limit, null, line);

        if (line != null && territory.Comp.Capability != line)
            return new PersistentPoiRecipeStatus(PersistentPoiBuildBlock.WrongLine, category, count, limit, null, line);

        if (category is { } full && limit is { } reached && count >= reached)
        {
            return new PersistentPoiRecipeStatus(
                reached == 0 ? PersistentPoiBuildBlock.NeedsLevel : PersistentPoiBuildBlock.LimitReached,
                category,
                count,
                limit,
                FirstLevelAllowing(full, count + 1),
                line);
        }

        return new PersistentPoiRecipeStatus(PersistentPoiBuildBlock.None, category, count, limit, null, line);
    }
}
