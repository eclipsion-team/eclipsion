using Content.Shared.Clothing.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Inventory;

namespace Content.Shared._Crescent.Clothing;

public sealed class HardsuitHelmetHatRestrictionSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HardsuitHelmetHatSlotComponent, ItemSlotInsertAttemptEvent>(OnInsertAttempt);
    }

    private void OnInsertAttempt(Entity<HardsuitHelmetHatSlotComponent> helmet, ref ItemSlotInsertAttemptEvent args)
    {
        if (args.Cancelled || args.Slot.ID != helmet.Comp.SlotId)
            return;

        if (!TryComp<ClothingComponent>(args.Item, out var clothing) || (clothing.Slots & SlotFlags.HEAD) == 0)
            args.Cancelled = true;
    }
}
