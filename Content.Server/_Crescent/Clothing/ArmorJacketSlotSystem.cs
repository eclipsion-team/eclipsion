using Content.Server.Atmos.Components;
using Content.Shared._Crescent.Clothing;
using Content.Shared._Crescent.DegradeableArmor;
using Content.Shared.Armor;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;

namespace Content.Server._Crescent.Clothing;

public sealed class ArmorJacketSlotSystem : SharedArmorJacketSlotSystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorComponent, ComponentStartup>(OnArmorStartup);
        SubscribeLocalEvent<DegradeableArmorComponent, ComponentStartup>(OnArmorStartup);
        SubscribeLocalEvent<PressureProtectionComponent, ComponentStartup>(OnArmorStartup);
    }

    private void OnArmorStartup<T>(Entity<T> armor, ref ComponentStartup args) where T : IComponent
    {
        if (HasComp<OverArmorJacketComponent>(armor)
            || !TryComp(armor, out ClothingComponent? clothing)
            || (clothing.Slots & SlotFlags.OUTERCLOTHING) == 0)
        {
            return;
        }

        EnsureComp<ArmorJacketSlotComponent>(armor);
    }
}
