using Content.Shared.Containers.ItemSlots;
using Content.Shared.Whitelist;

namespace Content.Shared._Crescent.Clothing;

public abstract class SharedArmorJacketSlotSystem : EntitySystem
{
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorJacketSlotComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<ArmorJacketSlotComponent, ComponentRemove>(OnRemove);
    }

    private void OnInit(Entity<ArmorJacketSlotComponent> armor, ref ComponentInit args)
    {
        var slot = armor.Comp.Slot;
        slot.Name = "armor-jacket-slot-name";
        slot.Whitelist = new EntityWhitelist { Components = ["OverArmorJacket"] };

        _itemSlots.AddItemSlot(armor, ArmorJacketSlotComponent.SlotId, slot);
    }

    private void OnRemove(Entity<ArmorJacketSlotComponent> armor, ref ComponentRemove args)
    {
        _itemSlots.RemoveItemSlot(armor, armor.Comp.Slot);
    }
}
