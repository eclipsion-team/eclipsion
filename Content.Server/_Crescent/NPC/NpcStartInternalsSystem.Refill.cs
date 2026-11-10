using System.Diagnostics.CodeAnalysis;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.VendingMachines;
using Content.Shared._Shitmed.Body.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Movement.Components;
using Content.Shared.VendingMachines;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Fetching a fresh tank once every one it carries is spent: a loose one lying nearby, or one out of a vending
/// machine that stocks them. The HTN does the walking (NpcPickAirSourceOperator, NpcTakeAirOperator).
/// </summary>
public sealed partial class NpcStartInternalsSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly NpcGunHandlingSystem _npcGun = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly VendingMachineSystem _vending = default!;

    /// <summary>
    /// How close it has to be to take the tank: interaction range, which equipping it off the floor checks.
    /// </summary>
    public const float SourceReach = SharedInteractionSystem.InteractionRange;

    private static readonly string[] TankSlots = ["pocket1", "pocket2", "suitstorage", "belt"];

    private readonly HashSet<Entity<GasTankComponent>> _looseTanks = new();
    private readonly HashSet<Entity<VendingMachineComponent>> _vendors = new();
    private readonly List<EntityUid> _expired = new();

    private void UpdateAirSource(
        Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent,
        bool breathable,
        TimeSpan now)
    {
        var comp = ent.Comp1;
        comp.AirNeed = GetAirNeed(ent, breathable);

        if (comp.AirNeed == NpcAirNeed.None)
        {
            ClearAirSource(comp);
            return;
        }

        if (comp.AirSource is { } source && IsUsableSource(ent, source, comp.AirSourceItem))
            return;

        ClearAirSource(comp);

        if (now < comp.NextSourceSearch)
            return;

        comp.NextSourceSearch = now + comp.SourceSearchInterval;

        foreach (var (ignored, until) in comp.IgnoredSources)
        {
            if (now >= until)
                _expired.Add(ignored);
        }

        foreach (var ignored in _expired)
        {
            comp.IgnoredSources.Remove(ignored);
        }

        _expired.Clear();

        FindAirSource(ent);
    }

    private NpcAirNeed GetAirNeed(Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent, bool breathable)
    {
        // Someone playing it fetches their own; a dormant garrison doesn't breathe.
        if (HasComp<ActorComponent>(ent) || HasComp<BreathingImmunityComponent>(ent))
            return NpcAirNeed.None;

        foreach (var item in _tactical.EnumerateCarried(ent))
        {
            if (IsGoodCarriedTank(ent, item, out _))
                return NpcAirNeed.None;
        }

        return !breathable && FindFullestTank(ent) == null
            ? NpcAirNeed.Urgent
            : NpcAirNeed.Routine;
    }

    /// <summary>
    /// A breathable tank it carries that isn't its jetpack and still has more than <see cref="NpcStartInternalsComponent.RefillPressure"/> in it.
    /// </summary>
    private bool IsGoodCarriedTank(
        Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent,
        EntityUid item,
        [NotNullWhen(true)] out GasTankComponent? tank)
    {
        return TryComp(item, out tank)
               && tank.IsInternals
               && !HasComp<JetpackComponent>(item)
               && tank.Air.Pressure >= ent.Comp1.RefillPressure
               && _respirator.CanMetabolizeGas((ent.Owner, ent.Comp3), tank.Air);
    }

    private void FindAirSource(Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent)
    {
        var comp = ent.Comp1;
        var xform = Transform(ent);
        if (xform.GridUid == null)
            return;

        var mapCoords = _transform.GetMapCoordinates(ent, xform);
        var bestDistance = float.MaxValue;

        _looseTanks.Clear();
        _lookup.GetEntitiesInRange(mapCoords, comp.SourceRange, _looseTanks);

        foreach (var tank in _looseTanks)
        {
            if (!IsUsableSource(ent, tank, null))
                continue;

            var distance = DistanceSquared(mapCoords, tank);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            comp.AirSource = tank;
            comp.AirSourceItem = null;
        }

        _vendors.Clear();
        _lookup.GetEntitiesInRange(mapCoords, comp.SourceRange, _vendors);

        foreach (var vendor in _vendors)
        {
            if (TryGetVendorTank(ent, vendor) is not { } item || !IsUsableSource(ent, vendor, item))
                continue;

            var distance = DistanceSquared(mapCoords, vendor);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            comp.AirSource = vendor;
            comp.AirSourceItem = item;
        }
    }

    private float DistanceSquared(MapCoordinates from, EntityUid to)
    {
        return (_transform.GetWorldPosition(to) - from.Position).LengthSquared();
    }

    private bool IsUsableSource(
        Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent,
        EntityUid source,
        string? item)
    {
        var comp = ent.Comp1;

        if (TerminatingOrDeleted(source) || comp.IgnoredSources.ContainsKey(source))
            return false;

        var xform = Transform(source);
        var npcXform = Transform(ent);

        // It never leaves its grid (NpcGridBound), so nothing on another one is any use.
        if (xform.GridUid == null || xform.GridUid != npcXform.GridUid ||
            !_transform.InRange(xform.Coordinates, npcXform.Coordinates, comp.SourceRange + 1f))
        {
            return false;
        }

        if (item != null)
        {
            return TryComp<VendingMachineComponent>(source, out var vendor)
                   && !vendor.Broken
                   && this.IsPowered(source, EntityManager)
                   && vendor.Inventory.TryGetValue(item, out var entry)
                   && entry.Amount > 0;
        }

        // Lying about loose: not worn, carried, in a locker or bolted down.
        return TryComp<GasTankComponent>(source, out var tank)
               && tank.IsInternals
               && !xform.Anchored
               && !HasComp<JetpackComponent>(source)
               && !_container.IsEntityOrParentInContainer(source, xform: xform)
               && tank.Air.Pressure >= comp.SourceMinPressure
               && _respirator.CanMetabolizeGas((ent.Owner, ent.Comp3), tank.Air);
    }

    /// <summary>
    /// The breathable tank the vending machine has the most air in, if it stocks any.
    /// </summary>
    private string? TryGetVendorTank(
        Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent,
        Entity<VendingMachineComponent> vendor)
    {
        string? best = null;
        var bestMoles = 0f;

        foreach (var (id, entry) in vendor.Comp.Inventory)
        {
            if (entry.Amount == 0 ||
                !_proto.TryIndex<EntityPrototype>(id, out var proto) ||
                proto.HasComp<JetpackComponent>(EntityManager.ComponentFactory) ||
                !proto.TryGetComponent<GasTankComponent>(out var tank, EntityManager.ComponentFactory) ||
                !tank.IsInternals ||
                tank.Air.TotalMoles <= bestMoles ||
                tank.Air.Pressure < ent.Comp1.SourceMinPressure ||
                !_respirator.CanMetabolizeGas((ent.Owner, ent.Comp3), tank.Air))
            {
                continue;
            }

            best = id;
            bestMoles = tank.Air.TotalMoles;
        }

        return best;
    }

    private static void ClearAirSource(NpcStartInternalsComponent comp)
    {
        comp.AirSource = null;
        comp.AirSourceItem = null;
        comp.CheckedSource = null;
        comp.SourceAttempts = 0;
    }

    private void IgnoreAirSource(NpcStartInternalsComponent comp)
    {
        if (comp.AirSource is { } source)
            comp.IgnoredSources[source] = _timing.CurTime + comp.SourceIgnoreTime;

        ClearAirSource(comp);
        comp.NextSourceSearch = _timing.CurTime;
    }

    /// <summary>
    /// Whether it needs a fresh tank and knows where to get one - with nothing at all left to breathe if
    /// <paramref name="urgent"/>.
    /// </summary>
    public bool WantsAir(EntityUid uid, bool urgent)
    {
        return TryComp<NpcStartInternalsComponent>(uid, out var comp)
               && comp.AirNeed >= (urgent ? NpcAirNeed.Urgent : NpcAirNeed.Routine)
               && comp.AirSource != null;
    }

    /// <summary>
    /// Where it means to get air from, if that is still there to be had. <paramref name="reachable"/> is
    /// whether it has already been found reachable.
    /// </summary>
    public bool TryGetAirSource(EntityUid uid, out EntityUid source, out bool reachable)
    {
        source = default;
        reachable = false;

        if (!TryGetRefillEntity(uid, out var ent) || ent.Value.Comp1.AirSource is not { } current)
            return false;

        if (!IsUsableSource(ent.Value, current, ent.Value.Comp1.AirSourceItem))
        {
            ClearAirSource(ent.Value.Comp1);
            return false;
        }

        source = current;
        reachable = ent.Value.Comp1.CheckedSource == current;
        return true;
    }

    public void SetAirSourceReachable(EntityUid uid, bool reachable)
    {
        if (!TryComp<NpcStartInternalsComponent>(uid, out var comp))
            return;

        if (reachable)
            comp.CheckedSource = comp.AirSource;
        else
            CountAirSourceAttempt(comp);
    }

    /// <summary>
    /// Counts a trip to the source, and gives up on it after too many.
    /// </summary>
    public void StartAirSourceTrip(EntityUid uid)
    {
        if (TryComp<NpcStartInternalsComponent>(uid, out var comp))
            CountAirSourceAttempt(comp);
    }

    private void CountAirSourceAttempt(NpcStartInternalsComponent comp)
    {
        if (comp.AirSource != null && ++comp.SourceAttempts > comp.SourceMaxAttempts)
            IgnoreAirSource(comp);
    }

    /// <summary>
    /// Takes the tank - off the floor, or out of the vending machine - puts it on in place of the spent one,
    /// which it drops, and gets back on internals if it needs them.
    /// </summary>
    public bool TryTakeAir(EntityUid uid)
    {
        if (!TryGetRefillEntity(uid, out var refill))
            return false;

        var ent = refill.Value;
        var comp = ent.Comp1;

        if (comp.AirSource is not { } source || !IsUsableSource(ent, source, comp.AirSourceItem))
        {
            ClearAirSource(comp);
            return false;
        }

        if (!_transform.InRange(Transform(uid).Coordinates, Transform(source).Coordinates, SourceReach))
            return false;

        var vendItem = comp.AirSourceItem;
        var tank = vendItem != null ? Spawn(vendItem, Transform(uid).Coordinates) : source;

        if (!TryPutOnTank(ent, tank))
        {
            if (vendItem != null)
                Del(tank);

            IgnoreAirSource(comp);
            return false;
        }

        if (vendItem != null && TryComp<VendingMachineComponent>(source, out var vendor) &&
            vendor.Inventory.TryGetValue(vendItem, out var entry))
        {
            entry.Amount--;
            _vending.UpdateVendingMachineInterfaceState(source, vendor);
            _vending.TryUpdateVisualState(source, vendor);
            _audio.PlayPvs(vendor.SoundVend, source);
        }

        ClearAirSource(comp);
        comp.AirNeed = NpcAirNeed.None;
        comp.NextCheck = _timing.CurTime;
        return true;
    }

    private bool TryGetRefillEntity(
        EntityUid uid,
        [NotNullWhen(true)] out Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent>? ent)
    {
        ent = null;

        if (!TryComp<NpcStartInternalsComponent>(uid, out var comp) ||
            !TryComp<InternalsComponent>(uid, out var internals) ||
            !TryComp<RespiratorComponent>(uid, out var respirator))
        {
            return false;
        }

        ent = (uid, comp, internals, respirator);
        return true;
    }

    /// <summary>
    /// Swaps the emptiest spent tank it carries for <paramref name="tank"/>: the spent one goes on the floor
    /// and the new one into its slot, any free slot a tank fits, or its bag. With room for neither, the spent
    /// one goes back where it was.
    /// </summary>
    private bool TryPutOnTank(Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent, EntityUid tank)
    {
        var uid = ent.Owner;
        EntityUid? spent = null;
        var spentPressure = float.MaxValue;

        foreach (var item in _tactical.EnumerateCarried(uid))
        {
            if (!TryComp<GasTankComponent>(item, out var carried) || !carried.IsInternals ||
                HasComp<JetpackComponent>(item) || IsGoodCarriedTank(ent, item, out _))
            {
                continue;
            }

            if (carried.Air.Pressure >= spentPressure)
                continue;

            spent = item;
            spentPressure = carried.Air.Pressure;
        }

        string? spentSlot = null;

        if (spent != null)
        {
            if (ent.Comp2.GasTankEntity == spent)
                _internals.DisconnectTank(ent.Comp2);

            if (_inventory.TryGetContainingSlot(spent.Value, out var slot) && Transform(spent.Value).ParentUid == uid)
            {
                spentSlot = slot.Name;
                _inventory.TryUnequip(uid, spentSlot, silent: true, force: true);
            }
            else
            {
                _transform.DropNextTo(spent.Value, uid);
            }
        }

        if (spentSlot != null && _inventory.TryEquip(uid, tank, spentSlot, silent: true))
            return true;

        foreach (var slot in TankSlots)
        {
            if (_inventory.TryGetSlotEntity(uid, slot, out _))
                continue;

            if (_inventory.TryEquip(uid, tank, slot, silent: true))
                return true;
        }

        if (_npcGun.TryStow(uid, tank))
            return true;

        if (spent != null)
        {
            if (spentSlot != null)
                _inventory.TryEquip(uid, spent.Value, spentSlot, silent: true, force: true);
            else
                _npcGun.TryStow(uid, spent.Value);
        }

        return false;
    }
}
