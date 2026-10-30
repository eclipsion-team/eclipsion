using System.Linq;
using Content.Client.Clothing;
using Content.Shared._Crescent.Clothing;
using Content.Shared.Clothing;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Item;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Serialization.Manager;

namespace Content.Client._Crescent.Clothing;

/// <summary>
/// Renders clothing in a hardsuit helmet's hat slot over the helmet's own layers.
/// </summary>
public sealed class HardsuitHelmetHatSlotSystem : EntitySystem
{
    private const string LayerPrefix = "hardsuit-helmet-hat-";

    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedItemSystem _itemSystem = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HardsuitHelmetHatSlotComponent, GetEquipmentVisualsEvent>(
            OnGetEquipmentVisuals,
            after: [typeof(ClientClothingSystem)]);
        SubscribeLocalEvent<HardsuitHelmetHatSlotComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<HardsuitHelmetHatSlotComponent, EntRemovedFromContainerMessage>(OnRemoved);
        SubscribeLocalEvent<HardsuitHelmetHatSlotComponent, VisualsChangedEvent>(OnContainedVisualsChanged);
    }

    private void OnGetEquipmentVisuals(
        Entity<HardsuitHelmetHatSlotComponent> helmet,
        ref GetEquipmentVisualsEvent args)
    {
        if (!_itemSlots.TryGetSlot(helmet, helmet.Comp.SlotId, out var slot) ||
            slot.Item is not { } hat)
        {
            return;
        }

        AddContainedClothingLayers(hat, LayerPrefix, args);
    }

    public void AddContainedClothingLayers(EntityUid contained, string layerPrefix, GetEquipmentVisualsEvent args)
    {
        var containedVisuals = new GetEquipmentVisualsEvent(args.Equipee, args.Slot);
        RaiseLocalEvent(contained, containedVisuals);

        TryComp(contained, out SpriteComponent? containedSprite);
        foreach (var (key, layer) in containedVisuals.Layers)
        {
            var copy = _serialization.CreateCopy(layer, notNullableOverride: true);
            if (copy.RsiPath == null && copy.TexturePath == null)
                copy.RsiPath = containedSprite?.BaseRSI?.Path.ToString();

            if (copy.MapKeys != null)
                copy.MapKeys = copy.MapKeys.Select(k => layerPrefix + k).ToHashSet();

            if (copy.CopyToShaderParameters != null)
                copy.CopyToShaderParameters.LayerKey = layerPrefix + copy.CopyToShaderParameters.LayerKey;

            args.Layers.Add((layerPrefix + key, copy));
        }
    }

    private void OnInserted(
        Entity<HardsuitHelmetHatSlotComponent> helmet,
        ref EntInsertedIntoContainerMessage args)
    {
        OnContainerModified(helmet, args);
    }

    private void OnRemoved(
        Entity<HardsuitHelmetHatSlotComponent> helmet,
        ref EntRemovedFromContainerMessage args)
    {
        OnContainerModified(helmet, args);
    }

    private void OnContainerModified(
        Entity<HardsuitHelmetHatSlotComponent> helmet,
        ContainerModifiedMessage args)
    {
        if (args.Container.ID == helmet.Comp.SlotId)
            _itemSystem.VisualsChanged(helmet);
    }

    private void OnContainedVisualsChanged(
        Entity<HardsuitHelmetHatSlotComponent> helmet,
        ref VisualsChangedEvent args)
    {
        if (args.ContainerId == helmet.Comp.SlotId)
            _itemSystem.VisualsChanged(helmet);
    }
}
