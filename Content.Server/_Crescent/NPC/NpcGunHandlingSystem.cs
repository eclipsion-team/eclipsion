using System.Diagnostics.CodeAnalysis;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Clothing.Loadouts.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: gets a held gun into a state an NPC can actually fire it from.
/// </summary>
/// <remarks>
/// Hullrot guns are not point-and-click the way upstream's are. A rifle spawns unwielded with the bolt
/// open, a pump shotgun has to be worked between shots, and the HTN has no operator for any of it - so an
/// NPC handed a faction primary walks up to its target, aims, and then fails every shot silently, because
/// GunRequiresWield cancels ShotAttemptedEvent and an open bolt refuses to feed ammo. This does those
/// steps on the NPC's behalf while it is in ranged combat.
/// </remarks>
public sealed class NpcGunHandlingSystem : EntitySystem
{
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly NpcTacticalSystem _tactical = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly WieldableSystem _wieldable = default!;

    private const string MagazineSlot = "gun_magazine";

    private static readonly string[] Pockets = ["pocket1", "pocket2"];

    /// <summary>
    /// Gap between handling steps.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan ResupplyRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Which ammo box prototype holds a given round, worked out once per round.
    /// </summary>
    private readonly Dictionary<string, EntProtoId?> _ammoBoxCache = new();

    public override void Initialize()
    {
        base.Initialize();

        // The loadout is what puts the gun in their hands, and the spares are for that gun.
        SubscribeLocalEvent<NpcGunHandlingComponent, MapInitEvent>(OnMapInit,
            after: [typeof(SharedLoadoutSystem)]);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => _ammoBoxCache.Clear());
    }

    /// <summary>
    /// Hands the NPC its spare ammunition for the gun it spawned holding: magazines if it takes them, boxes
    /// of loose rounds if it doesn't.
    /// </summary>
    private void OnMapInit(Entity<NpcGunHandlingComponent> ent, ref MapInitEvent args)
    {
        foreach (var held in _hands.EnumerateHeld(ent))
        {
            EntProtoId? proto;
            int count;

            if (TryGetMagazineSlot(held, out var slot))
            {
                proto = slot.StartingItem;
                count = ent.Comp.SpareMagazines;
            }
            else if (TryComp<BallisticAmmoProviderComponent>(held, out var ballistic) && ballistic.Proto is { } round)
            {
                proto = FindAmmoBoxPrototype(round);
                count = ent.Comp.SpareAmmoBoxes;
            }
            else
            {
                continue;
            }

            if (proto == null)
                continue;

            for (var i = 0; i < count; i++)
            {
                var spare = Spawn(proto.Value, Transform(ent).Coordinates);

                if (TryStow(ent, spare))
                    continue;

                // Nowhere left to put it. Better a soldier short a magazine than one standing on a pile.
                Del(spare);
                break;
            }

            break;
        }
    }

    /// <summary>
    /// The best ammo box for a round: an actual box of that ammunition if there is one, and something else
    /// holding it - another gun's magazine, a speedloader - only if there isn't.
    /// </summary>
    private EntProtoId? FindAmmoBoxPrototype(string round)
    {
        if (_ammoBoxCache.TryGetValue(round, out var cached))
            return cached;

        EntProtoId? best = null;
        var bestScore = int.MinValue;

        foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.Abstract ||
                proto.Components.ContainsKey("Gun") ||
                !proto.Components.ContainsKey("Item") ||
                !proto.TryGetComponent<BallisticAmmoProviderComponent>(out var provider, _factory) ||
                provider.Proto != round ||
                provider.Capacity <= 0)
            {
                continue;
            }

            // A provider with a round prototype fills itself up to capacity on spawn, so capacity is what
            // the NPC actually gets.
            var score = provider.Capacity;

            if (proto.ID.Contains("Box"))
                score += 1000;
            else if (proto.ID.Contains("Magazine"))
                score -= 1000;

            if (score <= bestScore)
                continue;

            bestScore = score;
            best = proto.ID;
        }

        _ammoBoxCache[round] = best;
        return best;
    }

    /// <summary>
    /// Crescent: puts an item into the first worn storage with room for it, then the pockets.
    /// </summary>
    public bool TryStow(EntityUid npc, EntityUid item)
    {
        if (_inventory.TryGetContainerSlotEnumerator(npc, out var slots))
        {
            while (slots.NextItem(out var worn))
            {
                if (TryComp<StorageComponent>(worn, out var storage) &&
                    _storage.Insert(worn, item, out _, storageComp: storage, playSound: false))
                {
                    return true;
                }
            }
        }

        foreach (var pocket in Pockets)
        {
            if (_inventory.TryEquip(npc, item, pocket, silent: true, force: true))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The gun's magazine well, if it has one. ItemSlots logs an error when asked about an entity that has no
    /// slots at all, which every pump shotgun and bolt-action rifle would otherwise trip constantly.
    /// </summary>
    private bool TryGetMagazineSlot(EntityUid gunUid, [NotNullWhen(true)] out ItemSlot? slot)
    {
        slot = null;
        return TryComp<ItemSlotsComponent>(gunUid, out var slots) &&
               _itemSlots.TryGetSlot(gunUid, MagazineSlot, out slot, slots);
    }

    /// <summary>
    /// Readies <paramref name="gunUid"/> for <paramref name="npc"/>, doing at most one step per call.
    /// </summary>
    /// <returns>True when the gun is ready to be fired this tick.</returns>
    public bool TryReadyGun(EntityUid npc, EntityUid gunUid)
    {
        // Doesn't apply to a creature that *is* the gun, or to one holding something with no handling
        // steps at all, and those shouldn't pick up the bookkeeping component either.
        if (npc == gunUid || !NeedsHandling(gunUid))
            return true;

        var comp = EnsureComp<NpcGunHandlingComponent>(npc);
        var curTime = _timing.CurTime;

        // A reload in progress blocks everything else until it lands.
        if (comp.ResupplyEnd is { } end)
        {
            if (curTime < end)
                return false;

            comp.ResupplyEnd = null;

            // Crescent: a real magazine or box of rounds off the NPC if it still has one - it may have been
            // stripped off it mid-reload - and the stand-in refill otherwise.
            var reloaded = TryFindSpareMagazine(npc, gunUid, out var spare) && SwapMagazine(npc, gunUid, spare.Value)
                           || TryFindAmmoBox(npc, gunUid, out var box) && RefillFromBox(gunUid, box.Value);

            if (!reloaded && comp.Resupply)
                Resupply(gunUid, comp);
        }

        if (comp.NextAttempt > curTime)
            return false;

        // One step per tick, rate limited, so a gun that can't be readied doesn't spam wield/rack
        // popups at everyone standing nearby.
        if (comp.Wield && NeedsWield(gunUid, out var wieldable))
        {
            comp.NextAttempt = curTime + RetryDelay;

            // If the wield itself cannot happen - no free hand, something blocking it - there is nothing
            // further to try, so let the shot go ahead and be refused the way it was before rather than
            // freezing the NPC here forever.
            if (_wieldable.TryWield(gunUid, wieldable, npc))
                return false;
        }
        else if (TryStartReload(npc, gunUid, comp, curTime))
        {
            comp.NextAttempt = curTime + RetryDelay;
            return false;
        }
        else if (comp.Cycle && TryCycle(npc, gunUid))
        {
            comp.NextAttempt = curTime + RetryDelay;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Cheap check so the common case - a mob whose "gun" is its own innate attack - costs nothing.
    /// </summary>
    private bool NeedsHandling(EntityUid gunUid)
    {
        // ChamberMagazine derives from Magazine but is its own registered component, so HasComp on the
        // base type does not match it - it has to be asked for by name.
        return HasComp<WieldableComponent>(gunUid)
               || HasComp<MagazineAmmoProviderComponent>(gunUid)
               || HasComp<ChamberMagazineAmmoProviderComponent>(gunUid)
               || HasComp<BallisticAmmoProviderComponent>(gunUid);
    }

    private bool NeedsWield(EntityUid gunUid, [NotNullWhen(true)] out WieldableComponent? wieldable)
    {
        return TryComp(gunUid, out wieldable)
               && !wieldable.Wielded
               && HasComp<GunRequiresWieldComponent>(gunUid);
    }

    /// <summary>
    /// Closes an open bolt, or works the action on a gun that doesn't eject for itself.
    /// </summary>
    private bool TryCycle(EntityUid npc, EntityUid gunUid)
    {
        if (TryComp<ChamberMagazineAmmoProviderComponent>(gunUid, out var chamber) &&
            chamber.BoltClosed == false)
        {
            _gun.SetBoltClosed(gunUid, chamber, true, npc);
            return true;
        }

        if (TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic) &&
            !ballistic.AutoCycle &&
            !ballistic.Cycled)
        {
            _gun.ManualCycle(gunUid, ballistic, _transform.GetMapCoordinates(gunUid), npc);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Crescent: starts a reload if the gun is dry and the NPC has something to reload it with - one of its
    /// own magazines first, the stand-in resupply after that.
    /// </summary>
    private bool TryStartReload(EntityUid npc, EntityUid gunUid, NpcGunHandlingComponent comp, TimeSpan curTime)
    {
        if (!IsDry(gunUid))
            return false;

        if (!TryFindSpareMagazine(npc, gunUid, out _) && !TryFindAmmoBox(npc, gunUid, out _))
        {
            if (!comp.Resupply || comp.ResupplyCount is <= 0)
                return false;

            if (!CanResupply(gunUid))
            {
                comp.NextAttempt = curTime + ResupplyRetryDelay;
                return false;
            }
        }

        comp.ResupplyEnd = curTime + comp.ResupplyDelay;

        var ev = new NpcGunReloadStartedEvent(gunUid);
        RaiseLocalEvent(npc, ref ev);
        return true;
    }

    /// <summary>
    /// Crescent: whether the NPC could get <paramref name="gunUid"/> firing again by reloading it.
    /// </summary>
    public bool CanReload(EntityUid npc, EntityUid gunUid)
    {
        if (!IsDry(gunUid))
            return false;

        if (TryFindSpareMagazine(npc, gunUid, out _) || TryFindAmmoBox(npc, gunUid, out _))
            return true;

        return TryComp<NpcGunHandlingComponent>(npc, out var comp)
               && comp.Resupply
               && comp.ResupplyCount is not <= 0
               && CanResupply(gunUid);
    }

    /// <summary>
    /// Crescent: loaded magazines the NPC carries that fit <paramref name="gunUid"/> - or for a gun that
    /// loads loose rounds, boxes of its ammunition - or null if it takes neither.
    /// </summary>
    public int? CountSpareMagazines(EntityUid npc, EntityUid gunUid)
    {
        if (!TryGetMagazineSlot(gunUid, out var slot))
            return CountAmmoBoxes(npc, gunUid);

        var count = 0;

        foreach (var carried in _tactical.EnumerateCarried(npc))
        {
            if (IsUsableMagazine(gunUid, slot, carried))
                count++;
        }

        return count;
    }

    private bool TryFindSpareMagazine(EntityUid npc, EntityUid gunUid, [NotNullWhen(true)] out EntityUid? magazine)
    {
        magazine = null;

        if (!TryGetMagazineSlot(gunUid, out var slot))
            return false;

        foreach (var carried in _tactical.EnumerateCarried(npc))
        {
            if (!IsUsableMagazine(gunUid, slot, carried))
                continue;

            magazine = carried;
            return true;
        }

        return false;
    }

    private bool IsUsableMagazine(EntityUid gunUid, ItemSlot slot, EntityUid candidate)
    {
        if (candidate == gunUid || candidate == slot.Item)
            return false;

        if (!HasComp<BallisticAmmoProviderComponent>(candidate) && !HasComp<MagazineAmmoProviderComponent>(candidate))
            return false;

        if (!_itemSlots.CanInsert(gunUid, candidate, null, slot, swap: true))
            return false;

        var ammoEv = new GetAmmoCountEvent();
        RaiseLocalEvent(candidate, ref ammoEv);
        return ammoEv.Count > 0;
    }

    private int? CountAmmoBoxes(EntityUid npc, EntityUid gunUid)
    {
        if (!TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic) || ballistic.Proto is not { } round)
            return null;

        var count = 0;

        foreach (var carried in _tactical.EnumerateCarried(npc))
        {
            if (IsAmmoBoxFor(carried, gunUid, round, out _))
                count++;
        }

        return count;
    }

    private bool TryFindAmmoBox(EntityUid npc, EntityUid gunUid,
        [NotNullWhen(true)] out Entity<BallisticAmmoProviderComponent>? box)
    {
        box = null;

        // A magazine-fed gun reloads with magazines. Loose rounds are for guns with an internal magazine.
        if (TryGetMagazineSlot(gunUid, out _) ||
            !TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic) ||
            ballistic.Proto is not { } round)
        {
            return false;
        }

        foreach (var carried in _tactical.EnumerateCarried(npc))
        {
            if (!IsAmmoBoxFor(carried, gunUid, round, out var provider))
                continue;

            box = (carried, provider);
            return true;
        }

        return false;
    }

    private bool IsAmmoBoxFor(EntityUid candidate, EntityUid gunUid, string round,
        [NotNullWhen(true)] out BallisticAmmoProviderComponent? provider)
    {
        provider = null;

        if (candidate == gunUid || HasComp<GunComponent>(candidate) ||
            !TryComp(candidate, out provider) || provider.Proto != round)
        {
            return false;
        }

        return CountUnspent(provider) > 0;
    }

    private int CountUnspent(BallisticAmmoProviderComponent provider)
    {
        var count = provider.UnspawnedCount;

        foreach (var ent in provider.Entities)
        {
            if (TryComp<CartridgeAmmoComponent>(ent, out var cartridge) && !cartridge.Spent)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Crescent: loads rounds out of <paramref name="box"/> into an internal-magazine gun until it's full or
    /// the box is empty.
    /// </summary>
    private bool RefillFromBox(EntityUid gunUid, Entity<BallisticAmmoProviderComponent> box)
    {
        if (!TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic))
            return false;

        ClearSpent(ballistic);

        var free = ballistic.Capacity - ballistic.Entities.Count - ballistic.UnspawnedCount;
        var taken = 0;

        while (taken < free && box.Comp.UnspawnedCount > 0)
        {
            box.Comp.UnspawnedCount--;
            taken++;
        }

        foreach (var ent in box.Comp.Entities.ToArray())
        {
            if (taken >= free)
                break;

            if (!TryComp<CartridgeAmmoComponent>(ent, out var cartridge) || cartridge.Spent)
                continue;

            box.Comp.Entities.Remove(ent);
            _container.Remove(ent, box.Comp.Container, force: true);
            QueueDel(ent);
            taken++;
        }

        if (taken == 0)
            return false;

        ballistic.UnspawnedCount += taken;
        ballistic.Cycled = true;

        _gun.UpdateBallisticAppearance(gunUid, ballistic);
        _gun.UpdateBallisticAppearance(box, box.Comp);
        Dirty(gunUid, ballistic);
        Dirty(box);
        _gun.UpdateAmmoCount(gunUid);
        return true;
    }

    /// <summary>
    /// Crescent: drops the empty magazine out of the gun and seats <paramref name="magazine"/> in its place.
    /// </summary>
    private bool SwapMagazine(EntityUid npc, EntityUid gunUid, EntityUid magazine)
    {
        if (!TryGetMagazineSlot(gunUid, out var slot))
            return false;

        EntityUid? spent = null;

        // The empty goes on the floor, same as anyone else's speed reload.
        if (slot.Item != null && !_itemSlots.TryEject(gunUid, slot, null, out spent, excludeUserAudio: true))
            return false;

        if (!_itemSlots.TryInsert(gunUid, slot, magazine, null, excludeUserAudio: true))
        {
            if (spent != null)
                _itemSlots.TryInsert(gunUid, slot, spent.Value, null, excludeUserAudio: true);

            return false;
        }

        // Crescent: an NPC that leaves nothing behind doesn't leave its empties either.
        if (spent != null && HasComp<NpcNoLootComponent>(npc))
            QueueDel(spent.Value);

        _gun.UpdateAmmoCount(gunUid);
        return true;
    }

    /// <summary>
    /// Whether the gun has nothing left to fire. A chambered round still counts, so this waits for the
    /// gun to run properly dry rather than reloading on the last shot.
    /// </summary>
    /// <remarks>
    /// Spent cases left in a gun that doesn't eject for itself are part of <see cref="GetAmmoCountEvent"/>,
    /// so counting that alone would leave such a gun looking loaded forever and it would never resupply.
    /// </remarks>
    public bool IsDry(EntityUid gunUid)
    {
        if (TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic))
        {
            if (ballistic.UnspawnedCount > 0)
                return false;

            foreach (var ent in ballistic.Entities)
            {
                if (!TryComp<CartridgeAmmoComponent>(ent, out var cartridge) || !cartridge.Spent)
                    return false;
            }

            return true;
        }

        var ammoEv = new GetAmmoCountEvent();
        RaiseLocalEvent(gunUid, ref ammoEv);
        return ammoEv.Count == 0;
    }

    private bool CanResupply(EntityUid gunUid)
    {
        if (TryGetMagazineSlot(gunUid, out var slot))
            return slot.StartingItem != null;

        return TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic) && ballistic.Proto != null;
    }

    /// <summary>
    /// Stands in for the spare magazines an NPC would be carrying: a fresh magazine in the well, or a
    /// topped-up internal magazine for guns that load loose rounds.
    /// </summary>
    private void Resupply(EntityUid gunUid, NpcGunHandlingComponent comp)
    {
        var refilled = TryGetMagazineSlot(gunUid, out var slot)
            ? ReplaceMagazine(gunUid, slot)
            : RefillInternal(gunUid);

        if (!refilled)
        {
            // Whatever stopped it isn't going to change, and retrying forever would leave the NPC
            // permanently reloading. Let it fall back to dropping the gun and finding another.
            comp.Resupply = false;
            return;
        }

        if (comp.ResupplyCount is { } remaining)
            comp.ResupplyCount = remaining - 1;

        _gun.UpdateAmmoCount(gunUid);
    }

    private bool ReplaceMagazine(EntityUid gunUid, ItemSlot slot)
    {
        if (slot.StartingItem is not { } proto)
            return false;

        var spent = slot.Item;

        if (spent != null && !_itemSlots.TryEject(gunUid, slot, null, out _, excludeUserAudio: true))
            return false;

        var mag = Spawn(proto, _transform.GetMapCoordinates(gunUid));

        if (_itemSlots.TryInsert(gunUid, slot, mag, null, excludeUserAudio: true))
        {
            if (spent != null)
                QueueDel(spent.Value);

            return true;
        }

        // The fresh magazine wouldn't go in, so put the empty one back rather than leaving the NPC
        // holding a gun with no magazine at all.
        QueueDel(mag);

        if (spent != null)
            _itemSlots.TryInsert(gunUid, slot, spent.Value, null, excludeUserAudio: true);

        return false;
    }

    private bool RefillInternal(EntityUid gunUid)
    {
        if (!TryComp<BallisticAmmoProviderComponent>(gunUid, out var ballistic) || ballistic.Proto == null)
            return false;

        ClearSpent(ballistic);

        var free = ballistic.Capacity - ballistic.Entities.Count;

        if (free <= 0)
            return false;

        ballistic.UnspawnedCount = free;
        ballistic.Cycled = true;
        _gun.UpdateBallisticAppearance(gunUid, ballistic);
        Dirty(gunUid, ballistic);
        return true;
    }

    /// <summary>
    /// Guns that don't auto-eject leave their spent case sitting in the container; clear those out first or
    /// a refill has nowhere to go.
    /// </summary>
    private void ClearSpent(BallisticAmmoProviderComponent ballistic)
    {
        foreach (var ent in ballistic.Entities.ToArray())
        {
            if (!TryComp<CartridgeAmmoComponent>(ent, out var cartridge) || !cartridge.Spent)
                continue;

            ballistic.Entities.Remove(ent);
            _container.Remove(ent, ballistic.Container, force: true);
            QueueDel(ent);
        }
    }
}

/// <summary>
/// Crescent: raised on an NPC as it starts reloading its gun.
/// </summary>
[ByRefEvent]
public readonly record struct NpcGunReloadStartedEvent(EntityUid Gun);
