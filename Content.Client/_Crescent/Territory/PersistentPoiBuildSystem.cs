using Content.Client.Construction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Crescent.Territory;

public readonly record struct PersistentPoiBuildCost(StackPrototype Material, int Amount);

public sealed class PersistentPoiBuildSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IPlacementManager _placement = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ConstructionSystem _construction = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly TerritoryDecalPainterSystem _painter = default!;

    private static readonly EntProtoId Standard = "PersistentCaptureRegionFlag";

    private static readonly ResPath BannerRsi = new("_Crescent/Structures/medieval_pole_banners.rsi");
    private const string NeutralBanner = "neutral-white";

    private const float MaterialReach = 2f;

    private readonly HashSet<Entity<StackComponent>> _nearby = new();

    public int MaxLevel => _levels.MaxLevel;

    public List<PersistentPoiBuildCategory> GetMenu()
    {
        if (_player.LocalEntity is not { } user ||
            !TryComp<PersistentPoiBuilderComponent>(user, out var builder) ||
            !_proto.TryIndex(builder.Action, out var action) ||
            !action.TryGetComponent<PersistentPoiBuildMenuComponent>(out var menu, _factory))
        {
            return new List<PersistentPoiBuildCategory>();
        }

        return menu.Categories;
    }

    public bool TryGetTerritory(out Entity<PersistentPoiGridComponent> territory)
    {
        territory = default;
        if (_player.LocalEntity is not { } user ||
            Transform(user).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var comp))
        {
            return false;
        }

        territory = (grid, comp);
        return true;
    }

    public PersistentPoiRecipeStatus GetStatus(ConstructionPrototype recipe)
    {
        var faction = _player.LocalEntity is { } user ? PersistentTerritoryFactions.GetTeam(EntityManager, user) : null;
        return TryGetTerritory(out var territory)
            ? _levels.GetRecipeStatus(territory, recipe, faction)
            : new PersistentPoiRecipeStatus(PersistentPoiBuildBlock.NotHeld);
    }

    public SpriteSpecifier? GetBanner(Entity<PersistentPoiGridComponent> territory)
    {
        if (!_proto.TryIndex(Standard, out var proto) ||
            !proto.TryGetComponent<PersistentCaptureRegionComponent>(out var region, _factory))
        {
            return null;
        }

        var state = territory.Comp.Owner is { } owner && region.TeamStates.TryGetValue(owner, out var flown)
            ? flown
            : NeutralBanner;

        return new SpriteSpecifier.Rsi(BannerRsi, state);
    }

    public List<List<PersistentPoiBuildCost>> GetStages(ConstructionPrototype recipe)
    {
        var stages = new List<List<PersistentPoiBuildCost>>();
        if (!_proto.TryIndex(recipe.Graph, out var graph) ||
            !graph.Nodes.TryGetValue(recipe.StartNode, out var node) ||
            graph.Path(recipe.StartNode, recipe.TargetNode) is not { } path)
        {
            return stages;
        }

        foreach (var next in path)
        {
            if (node.GetEdge(next.Name) is not { } edge)
                break;

            var costs = new List<PersistentPoiBuildCost>();
            foreach (var step in edge.Steps)
            {
                if (step is MaterialConstructionGraphStep material && _proto.TryIndex(material.MaterialPrototypeId, out var stack))
                    costs.Add(new PersistentPoiBuildCost(stack, material.Amount));
            }

            if (costs.Count > 0)
                stages.Add(costs);

            node = next;
        }

        return stages;
    }

    public List<PersistentPoiBuildCost> GetCosts(ConstructionPrototype recipe)
    {
        var costs = new List<PersistentPoiBuildCost>();
        foreach (var stage in GetStages(recipe))
        {
            costs.AddRange(stage);
        }

        return costs;
    }

    public (int Total, int LargestStack) CountCarried(string material, int amount)
    {
        if (_player.LocalEntity is not { } user)
            return (0, 0);

        var total = 0;
        var largest = 0;

        void Add(EntityUid item)
        {
            if (!TryComp<StackComponent>(item, out var stack) || stack.StackTypeId != material)
                return;

            total += stack.Count;
            largest = Math.Max(largest, stack.Count);
        }

        void AddWithStorage(EntityUid item)
        {
            if (TryComp<StorageComponent>(item, out var storage))
            {
                foreach (var stored in storage.Container.ContainedEntities)
                {
                    Add(stored);
                }
            }

            Add(item);
        }

        foreach (var held in _hands.EnumerateHeld(user))
        {
            AddWithStorage(held);
        }

        if (_inventory.TryGetContainerSlotEnumerator(user, out var slots))
        {
            while (slots.MoveNext(out var slot))
            {
                if (slot.ContainedEntity is { } worn)
                    AddWithStorage(worn);
            }
        }

        _nearby.Clear();
        _lookup.GetEntitiesInRange(Transform(user).Coordinates, MaterialReach, _nearby, LookupFlags.Uncontained);
        foreach (var stack in _nearby)
        {
            if (!_container.IsEntityInContainer(stack))
                Add(stack);
        }

        return (total, largest);
    }

    public void StartPlacing(ConstructionPrototype recipe)
    {
        if (recipe.Type != ConstructionType.Structure)
            return;

        _placement.BeginPlacing(new PlacementInformation
            {
                IsTile = false,
                PlacementOption = recipe.PlacementMode,
            },
            new ConstructionPlacementHijack(_construction, recipe));
    }

    public void OpenPainter()
    {
        _painter.Open();
    }
}
