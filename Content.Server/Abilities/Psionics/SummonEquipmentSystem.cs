using Content.Server.Actions;
using Content.Server.Hands.Systems;
using Content.Server.Popups;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Content.Shared.Magic;
using Content.Shared.Psionics.Events;
using Robust.Server.GameObjects;

namespace Content.Server.Abilities.Psionics;

/// <summary>
/// Handles <see cref="SummonEquipmentEvent"/>. Lifted out of the blood cult spell system when that was
/// removed; the mantis' Summon Black Blade is the only remaining user, and the behaviour is kept as it
/// was so the power plays exactly the same.
/// </summary>
public sealed class SummonEquipmentSystem : EntitySystem
{
    [Dependency] private readonly ActionsSystem _actions = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedMagicSystem _magic = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SummonEquipmentEvent>(OnSummonEquipment);
    }

    private void OnSummonEquipment(SummonEquipmentEvent ev)
    {
        if (ev.Handled)
            return;

        foreach (var (slot, protoId) in ev.Prototypes)
        {
            var entity = Spawn(protoId, _transform.GetMapCoordinates(ev.Performer));

            if (!_hands.TryPickupAnyHand(ev.Performer, entity) && !ev.Force)
            {
                _popup.PopupEntity(Loc.GetString("summon-equipment-no-empty-hand"), ev.Performer, ev.Performer);
                _actions.SetCooldown(ev.Action, TimeSpan.FromSeconds(1));
                QueueDel(entity);
                return;
            }

            // Only slot names that actually exist reach the wearer; anything else stays in hand.
            if (!TryComp(entity, out ClothingComponent? clothing)
                || !_inventory.TryUnequip(ev.Performer, slot, clothing: clothing)
                || !_inventory.TryEquip(ev.Performer, entity, slot, clothing: clothing, force: true))
                continue;

            if (ev.Speech is not null)
                _magic.Speak(ev.Performer, ev.Speech, ev.InvokeChatType);
        }

        ev.Handled = true;
    }
}
