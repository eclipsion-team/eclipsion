using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.Clothing;

[RegisterComponent, NetworkedComponent]
public sealed partial class ArmorJacketSlotComponent : Component
{
    public const string SlotId = "armor_jacket";

    [ViewVariables]
    public ItemSlot Slot = new();
}
