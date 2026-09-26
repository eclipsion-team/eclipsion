using Content.Server.Body.Components;
using Content.Server.Popups;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.Events;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Strip.Components;
using Robust.Shared.Containers;
using Robust.Shared.Player;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcNoLootComponent"/>
public sealed class NpcNoLootSystem : EntitySystem
{
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// Things that left an NPC's hands or inventory, checked once they've landed wherever they were going:
    /// an item moved from a hand into a backpack leaves the hand first and only then goes in.
    /// </summary>
    private readonly List<(EntityUid Npc, EntityUid Item)> _leaving = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcNoLootComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NpcNoLootComponent, ContainerIsRemovingAttemptEvent>(OnRemovingAttempt);
        SubscribeLocalEvent<NpcNoLootComponent, DidUnequipHandEvent>(OnUnequippedHand);
        SubscribeLocalEvent<NpcNoLootComponent, DidUnequipEvent>(OnUnequipped);
        SubscribeLocalEvent<NpcNoLootComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<NpcNoLootComponent, BeingGibbedEvent>(OnBeingGibbed);
    }

    private void OnMapInit(Entity<NpcNoLootComponent> ent, ref MapInitEvent args)
    {
        // No strip menu, so no pulling anything off it alive or down.
        RemComp<StrippableComponent>(ent);
    }

    /// <summary>
    /// Going down throws whatever is in its hands on the floor. A downed NPC keeps hold of it instead - it
    /// might get patched back up, and there's nobody to take it from it in the meantime.
    /// </summary>
    private void OnRemovingAttempt(Entity<NpcNoLootComponent> ent, ref ContainerIsRemovingAttemptEvent args)
    {
        if (_mobState.IsAlive(ent) || !_hands.TryGetHand(ent, args.Container.ID, out _))
            return;

        args.Cancel();
    }

    private void OnUnequippedHand(Entity<NpcNoLootComponent> ent, ref DidUnequipHandEvent args)
    {
        if (HasComp<VirtualItemComponent>(args.Unequipped))
            return;

        _leaving.Add((ent, args.Unequipped));
    }

    private void OnUnequipped(Entity<NpcNoLootComponent> ent, ref DidUnequipEvent args)
    {
        _leaving.Add((ent, args.Equipment));
    }

    private void OnMobStateChanged(Entity<NpcNoLootComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            Dust(ent);
    }

    /// <summary>
    /// Gibbing spills everything the NPC was wearing and carrying. It all goes, and so do the pieces.
    /// </summary>
    private void OnBeingGibbed(Entity<NpcNoLootComponent> ent, ref BeingGibbedEvent args)
    {
        foreach (var gib in args.GibbedParts)
        {
            QueueDel(gib);
        }

        Dust(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_leaving.Count == 0)
            return;

        foreach (var (npc, item) in _leaving)
        {
            if (TerminatingOrDeleted(item) || IsCarriedBy(item, npc))
                continue;

            // Someone took the body over, so what it drops is theirs to drop.
            if (!TerminatingOrDeleted(npc) && HasComp<ActorComponent>(npc))
                continue;

            QueueDel(item);
        }

        _leaving.Clear();
    }

    /// <summary>
    /// Whether <paramref name="item"/> is still somewhere on <paramref name="npc"/>: in a hand, worn, or in
    /// something it wears.
    /// </summary>
    private bool IsCarriedBy(EntityUid item, EntityUid npc)
    {
        var current = item;

        while (_container.TryGetContainingContainer((current, null, null), out var container))
        {
            if (container.Owner == npc)
                return true;

            current = container.Owner;
        }

        return false;
    }

    /// <summary>
    /// Crumbles the NPC to dust. Everything it carries is inside it, so it goes with it.
    /// </summary>
    private void Dust(Entity<NpcNoLootComponent> ent)
    {
        if (TerminatingOrDeleted(ent) || EntityManager.IsQueuedForDeletion(ent))
            return;

        var coords = _transform.GetMoverCoordinates(ent);

        _popup.PopupCoordinates(Loc.GetString(ent.Comp.DustMessage, ("npc", ent.Owner)), coords, PopupType.MediumCaution);

        if (ent.Comp.Remains is { } remains)
            Spawn(remains, coords);

        QueueDel(ent);
    }
}
