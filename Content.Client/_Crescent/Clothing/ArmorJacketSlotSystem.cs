using Content.Client.Clothing;
using Content.Shared._Crescent.Clothing;
using Content.Shared.Clothing;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Item;
using Robust.Shared.Containers;

namespace Content.Client._Crescent.Clothing;

public sealed class ArmorJacketSlotSystem : SharedArmorJacketSlotSystem
{
    private const string LayerPrefix = "armor-jacket-";

    [Dependency] private readonly HardsuitHelmetHatSlotSystem _hatSlot = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedItemSystem _itemSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorJacketSlotComponent, GetEquipmentVisualsEvent>(
            OnGetEquipmentVisuals,
            after: [typeof(ClientClothingSystem)]);
        SubscribeLocalEvent<ArmorJacketSlotComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<ArmorJacketSlotComponent, EntRemovedFromContainerMessage>(OnRemoved);
        SubscribeLocalEvent<ArmorJacketSlotComponent, VisualsChangedEvent>(OnContainedVisualsChanged);
    }

    private void OnGetEquipmentVisuals(Entity<ArmorJacketSlotComponent> armor, ref GetEquipmentVisualsEvent args)
    {
        if (_itemSlots.GetItemOrNull(armor, ArmorJacketSlotComponent.SlotId) is { } jacket)
            _hatSlot.AddContainedClothingLayers(jacket, LayerPrefix, args);
    }

    private void OnInserted(Entity<ArmorJacketSlotComponent> armor, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == ArmorJacketSlotComponent.SlotId)
            _itemSystem.VisualsChanged(armor);
    }

    private void OnRemoved(Entity<ArmorJacketSlotComponent> armor, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == ArmorJacketSlotComponent.SlotId)
            _itemSystem.VisualsChanged(armor);
    }

    private void OnContainedVisualsChanged(Entity<ArmorJacketSlotComponent> armor, ref VisualsChangedEvent args)
    {
        if (args.ContainerId == ArmorJacketSlotComponent.SlotId)
            _itemSystem.VisualsChanged(armor);
    }
}
