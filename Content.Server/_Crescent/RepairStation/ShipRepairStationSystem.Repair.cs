using Content.Shared._Crescent.Hardpoints;
using Content.Server.Atmos.Components;
using Content.Shared._Crescent.RepairStation;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Bank.Components;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.Decals;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Popups;
using Content.Shared.Weapons.Ranged.Components;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.RepairStation;

public sealed partial class ShipRepairStationSystem
{
    // ---------------------------------------------------------------------------------------------
    // Contract
    // ---------------------------------------------------------------------------------------------

    private void OnStart(Entity<ShipRepairStationComponent> station, ref ShipRepairStartMessage args)
    {
        var comp = station.Comp;

        if (!CanUse(station, args.Actor))
        {
            Deny(station, args.Actor, "ship-repair-station-popup-admin-only");
            return;
        }

        if (comp.Target != null)
        {
            Deny(station, args.Actor, "ship-repair-station-popup-already-working");
            return;
        }

        var serviceable = GetServiceableGrids(station);
        if (GetSelection(station, serviceable) is not { } ship)
        {
            Deny(station, args.Actor, "ship-repair-station-popup-no-ship");
            return;
        }

        // No blueprint stops a crew slip dead. The override console goes ahead with the half of the
        // survey that needs no file - the damage, the spills, the wreckage, the empty magazines.
        var data = CompOrNull<ShipRepairDataComponent>(ship);

        if (IsShipBusyElsewhere(station, ship))
        {
            Deny(station, args.Actor, "ship-repair-station-popup-busy");
            return;
        }

        // Without a file a crew slip only restocks a carrier's hangar; Survey leaves everything else out.
        var survey = Survey(station, ship, data);
        if (data == null && !comp.RepairUnregistered && survey.Jobs.Count == 0)
        {
            Deny(station, args.Actor, "ship-repair-station-popup-no-blueprint");
            return;
        }

        if (survey.Jobs.Count == 0)
        {
            Deny(station, args.Actor, "ship-repair-station-popup-intact");
            return;
        }

        if (!TryCharge(station, args.Actor, survey.Quote))
        {
            Deny(station, args.Actor, "ship-repair-station-popup-insufficient-funds");
            return;
        }

        comp.Target = ship;
        comp.Jobs = survey.Jobs;
        comp.JobsTotal = survey.Jobs.Count;
        comp.JobsDone = 0;
        comp.Filed = survey.Filed;
        comp.Rebuilt = new List<DrydockPart>();

        // Nothing was taken for a free job, so there is nothing to hand back if it is cut short.
        comp.AmountPaid = comp.Free ? 0 : survey.Quote;
        comp.Payer = args.Actor;

        ScheduleWork(station);

        if (comp.AdminOnly)
        {
            _adminLogger.Add(LogType.Action, LogImpact.High,
                $"{ToPrettyString(args.Actor):actor} authorised a repair of {ToPrettyString(ship):grid} ({survey.Jobs.Count} jobs) from {ToPrettyString(station.Owner):console}");
        }

        _audio.PlayPvs(comp.ConfirmSound, station.Owner);
        _popup.PopupEntity(Loc.GetString("ship-repair-station-popup-started", ("ship", Name(ship))), station.Owner, args.Actor);
        PushUi(station);
    }

    /// <summary>
    /// Spreads the weighted work over fixed batches so the job ends when the quote said it would.
    /// </summary>
    private void ScheduleWork(Entity<ShipRepairStationComponent> station)
    {
        var comp = station.Comp;
        var seconds = MathF.Max(EstimateSeconds(comp, comp.Jobs), 0.05f);
        var ticks = Math.Max(1, (int) MathF.Ceiling(seconds / MathF.Max(comp.BatchInterval, 0.05f)));

        comp.WorkPerTick = TotalWork(comp.Jobs) / ticks;
        comp.WorkBudget = 0f;
        comp.TickInterval = TimeSpan.FromSeconds(seconds / ticks);
        comp.NextTickTime = _timing.CurTime + comp.TickInterval;
        comp.StartTime = _timing.CurTime;
        comp.EndTime = _timing.CurTime + TimeSpan.FromSeconds(seconds);
    }

    private void OnCancel(Entity<ShipRepairStationComponent> station, ref ShipRepairCancelMessage args)
    {
        if (station.Comp.Target == null || !CanUse(station, args.Actor))
            return;

        AbortRepair(station, refund: true);
        _popup.PopupEntity(Loc.GetString("ship-repair-station-popup-cancelled"), station.Owner, args.Actor);
        PushUi(station);
    }

    private bool TryCharge(Entity<ShipRepairStationComponent> station, EntityUid actor, int amount)
    {
        // The override slip does the work for nothing, which it has to: an admin watching from a ghost
        // has no account for the bill to come out of in the first place.
        if (station.Comp.Free || amount <= 0)
            return true;

        if (!TryComp<BankAccountComponent>(actor, out var bank) || bank.Balance < amount)
            return false;

        return _bank.TryBankWithdraw(actor, amount);
    }

    private void Refund(Entity<ShipRepairStationComponent> station, int amount)
    {
        if (amount <= 0)
            return;

        if (station.Comp.Payer is { } payer && !TerminatingOrDeleted(payer) && HasComp<BankAccountComponent>(payer))
            _bank.TryBankDeposit(payer, amount);
    }

    private void Deny(Entity<ShipRepairStationComponent> station, EntityUid actor, string message)
    {
        _audio.PlayPvs(station.Comp.DenySound, station.Owner);
        _popup.PopupEntity(Loc.GetString(message), station.Owner, actor, PopupType.MediumCaution);
    }

    // ---------------------------------------------------------------------------------------------
    // Work
    // ---------------------------------------------------------------------------------------------

    private void RunTick(Entity<ShipRepairStationComponent> station)
    {
        var comp = station.Comp;

        if (comp.Target is not { } ship
            || TerminatingOrDeleted(ship)
            || !TryComp<MapGridComponent>(ship, out var gridComp)
            || !IsServiceable(station, ship))
        {
            // The ship left the clamps or came apart mid-job; the customer keeps the unspent half.
            AbortRepair(station, refund: true);
            return;
        }

        // Null on a hull the override slip is working without a blueprint, and on one whose file was
        // rewritten out from under the job. Neither is a reason to stop: the work that reads off the
        // file is simply not there to do.
        var data = CompOrNull<ShipRepairDataComponent>(ship);

        comp.WorkBudget += comp.WorkPerTick;

        // The last batch takes whatever rounding left over, so the hull is done when the bar says so.
        var final = _timing.CurTime >= comp.EndTime - comp.TickInterval / 2;

        var worked = 0;
        Vector2i? lastIndices = null;
        while (comp.Jobs.Count > 0)
        {
            var job = comp.Jobs[^1];

            // Out of budget, but a tile already started is finished in the same batch.
            if (!final && comp.WorkBudget <= 0f && job.Indices != lastIndices)
                break;

            comp.Jobs.RemoveAt(comp.Jobs.Count - 1);
            comp.JobsDone++;
            comp.WorkBudget -= JobWeight(job.Kind);
            worked++;

            if (job.Indices != lastIndices)
                Spawn(comp.ConstructEffect, new EntityCoordinates(ship, _map.TileCenterToVector(ship, gridComp, job.Indices)));

            lastIndices = job.Indices;
            PerformJob(station, ship, data, gridComp, job);
        }

        // One welding note per tick rather than one per part, or a big batch turns into a wall of noise.
        if (worked > 0 && lastIndices is { } soundTile)
            _audio.PlayPvs(comp.RepairSound, new EntityCoordinates(ship, _map.TileCenterToVector(ship, gridComp, soundTile)));

        if (comp.Jobs.Count > 0)
            return;

        CompleteRepair(station, ship);
    }

    private void PerformJob(
        Entity<ShipRepairStationComponent> station,
        EntityUid ship,
        ShipRepairDataComponent? data,
        MapGridComponent gridComp,
        ShipRepairJob job)
    {
        switch (job.Kind)
        {
            // The two kinds of work read back out of the hand device's file. A hull being worked
            // without one never queues them in the first place.
            case ShipRepairJobKind.Tile when data != null:
                LayTile(station, (ship, data), gridComp, job);
                break;

            case ShipRepairJobKind.ToolPart when data != null:
                SeedRefill(station, job.Indices, ReinstatePart(station, (ship, data), gridComp, job));
                break;

            case ShipRepairJobKind.DrydockPart when job.Part is { } part:
                SeedRefill(station, part.Tile, ReinstateDrydockPart(station, ship, gridComp, part));
                break;

            case ShipRepairJobKind.Heal:
                HealStructure(ship, job.Target);
                break;

            case ShipRepairJobKind.Decal when job.Decal is { } decal:
                RepaintDecal(ship, decal);
                break;

            case ShipRepairJobKind.Clean:
                if (!TerminatingOrDeleted(job.Target) && HasComp<PuddleComponent>(job.Target))
                    QueueDel(job.Target);
                break;

            case ShipRepairJobKind.Sweep:
                SweepDebris(ship, job.Target);
                break;

            case ShipRepairJobKind.Restock:
                Restock(ship, job.Target);
                break;

            case ShipRepairJobKind.DroneRestock:
                _autoDrone.RestockHangars(ship);
                break;
        }
    }

    /// <summary>
    /// Lays one plating tile back down. What is on the tile is judged again here rather than taken from
    /// the survey, because a job runs for minutes and a crewman may have patched the hole himself in the
    /// meantime - or laid something of his own choosing over it, which is not the slip's to overwrite.
    /// </summary>
    private void LayTile(
        Entity<ShipRepairStationComponent> station,
        Entity<ShipRepairDataComponent> ship,
        MapGridComponent gridComp,
        ShipRepairJob job)
    {
        if (!_shipRepair.TryGetChunk(ship.Comp, job.Indices, out var chunk))
            return;

        var relative = _shipRepair.GetRelativeIndices(job.Indices, ship.Comp.ChunkSize);
        var stored = chunk.Tiles[relative.X + relative.Y * ship.Comp.ChunkSize];
        if (stored == Tile.Empty.TypeId)
            return;

        var current = _map.GetTileRef(ship.Owner, gridComp, job.Indices).Tile.TypeId;

        // Still the hole the survey found, or the bare layer under it: lay the decking back. Anything
        // else standing there is a tile somebody chose, whether the original put back by hand or a
        // replacement he preferred, and either way the yard leaves it alone.
        if (current != stored && (current == Tile.Empty.TypeId || IsWornDownTo(stored, current)))
        {
            _shipRepair.TryRepairTileTile(ship, job.Indices);
            current = _map.GetTileRef(ship.Owner, gridComp, job.Indices).Tile.TypeId;
        }

        // The compartment behind a hole that is now shut - by the yard or by the crew - is owed its air.
        if (job.Breach && current != Tile.Empty.TypeId)
            station.Comp.RefillSeeds.Add(job.Indices);
    }

    /// <summary>
    /// Marks the compartments either side of a wall, window or door the yard has just put back. Plating
    /// is not the only thing a hull loses: a blown-out wall vents the room behind it just as surely, and
    /// the tile under it was never missing to begin with.
    /// </summary>
    /// <remarks>
    /// The seeds are the neighbouring tiles rather than the part's own, since the part itself holds air
    /// back and filling would get no further than the tile it stands on.
    /// </remarks>
    private void SeedRefill(Entity<ShipRepairStationComponent> station, Vector2i tile, EntityUid? rebuilt)
    {
        if (rebuilt is not { } part || !HasComp<AirtightComponent>(part))
            return;

        foreach (var (offset, _) in Cardinals)
        {
            station.Comp.RefillSeeds.Add(tile + offset);
        }
    }

    /// <summary>
    /// Bins one piece of wreckage. Judged again at the moment of work, because a crewman who picked
    /// the thing up between the survey and now should not have it vanish out of his hands.
    /// </summary>
    private void SweepDebris(EntityUid grid, EntityUid target)
    {
        if (TerminatingOrDeleted(target))
            return;

        var xform = Transform(target);
        if (xform.ParentUid != grid || xform.Anchored)
            return;

        if (!_drydock.IsDebris(target))
            return;

        QueueDel(target);
    }

    /// <summary>
    /// Lays one deck marking back down. Skipped if a crewman has already repainted it, and refused by
    /// the decal system itself if the tile under it is still open to space.
    /// </summary>
    private void RepaintDecal(EntityUid grid, Decal decal)
    {
        if (HasDecal(grid, decal))
            return;

        // A copy, so the file the slip keeps stays its own and can be laid again after the next hit.
        var fresh = new Decal(decal.Coordinates, decal.Id, decal.Color, decal.Angle, decal.ZIndex, decal.Cleanable);
        _decals.TryAddDecal(fresh, new EntityCoordinates(grid, fresh.Coordinates), out _);
    }

    /// <summary>
    /// Whether the slip may still work on something it surveyed. The survey judged a structure standing
    /// on the hull, and between then and now a crewman may have unbolted it and carried it off - out of
    /// the slip's reach, and in the case of a magazine or a fuel tank, somewhere it would be filled for
    /// a fee already paid on a much emptier one.
    /// </summary>
    private bool StillAboard(EntityUid grid, EntityUid target)
    {
        if (TerminatingOrDeleted(target))
            return false;

        var xform = Transform(target);
        return xform.Anchored && xform.GridUid == grid;
    }

    private void HealStructure(EntityUid grid, EntityUid target)
    {
        if (!StillAboard(grid, target) || !TryComp<DamageableComponent>(target, out var damage))
            return;

        _damageable.SetAllDamage(target, damage, FixedPoint2.Zero);
    }

    /// <summary>
    /// Tops a gun back up to capacity and a generator back up to a full tank.
    /// </summary>
    private void Restock(EntityUid grid, EntityUid target)
    {
        if (!StillAboard(grid, target))
            return;

        if (TryComp<BasicEntityAmmoProviderComponent>(target, out var basic) && basic.Capacity is { } basicCap)
            _gun.UpdateBasicEntityAmmoCount(target, basicCap, basic);

        if (TryComp<BallisticAmmoProviderComponent>(target, out var ballistic) && ballistic.Count < ballistic.Capacity)
        {
            ballistic.UnspawnedCount += ballistic.Capacity - ballistic.Count;
            _gun.UpdateBallisticAppearance(target, ballistic);
            Dirty(target, ballistic);
        }

        if (TryGetFuelDeficit(target, out var solution, out var deficit, out var reagent))
            _solution.TryAddReagent(solution, reagent, deficit, out _);

        if (NeedsBulb(target, out var bulbProto))
        {
            if (_light.GetBulb(target) is { } old)
                Del(old);

            var bulb = Spawn(bulbProto, Transform(target).Coordinates);
            if (!_light.InsertBulb(target, bulb))
                Del(bulb);
        }
    }

    /// <summary>
    /// Rebuilds one structure off the hand device's snapshot. Mirrors what the device itself does on
    /// doafter completion, including patching client-side snapshot state without dirtying the whole
    /// chunk map, so the two never disagree about what is still missing.
    /// </summary>
    private EntityUid? ReinstatePart(
        Entity<ShipRepairStationComponent> station,
        Entity<ShipRepairDataComponent> ship,
        MapGridComponent gridComp,
        ShipRepairJob job)
    {
        if (!_shipRepair.TryGetChunk(ship.Comp, job.Indices, out var chunk)
            || job.SpecId is not { } specId
            || !chunk.Entities.TryGetValue(specId, out var spec))
        {
            return null;
        }

        if (spec.ProtoIndex < 0 || spec.ProtoIndex >= ship.Comp.EntityPalette.Count)
            return null;

        var protoId = ship.Comp.EntityPalette[spec.ProtoIndex];
        if (!_proto.HasIndex(protoId))
            return null;

        // A crewman may have welded this back himself between the survey and now.
        if (!IsSpecMissing(spec, protoId))
            return null;

        // Or he may have built a fresh one rather than reviving the original, which the spec cannot see,
        // or put up a wall of another kind in its place.
        if (TileHolds(ship.Owner, gridComp, job.Indices, protoId)
            || SlotTaken(ship.Owner, gridComp, job.Indices, protoId, spec.Rotation, station.Comp.Filed))
        {
            return null;
        }

        ClearRemnants(ship.Owner, gridComp, job.Indices, protoId);

        // Whatever is left of the old part is floating debris; it goes with the replacement. Checked
        // against the prototype because a handle saved with a persistent grid can point at anything.
        if (spec.OriginalEntity is { } net
            && TryGetEntity(net, out var original)
            && !TerminatingOrDeleted(original.Value)
            && IsOriginalOf(original.Value, protoId))
        {
            QueueDel(original.Value);
        }

        var spawned = SpawnPart(protoId, (ship.Owner, gridComp), spec.LocalPosition, spec.Rotation);
        station.Comp.Filed.Add(spawned);

        spec.OriginalEntity = GetNetEntity(spawned);
        RaiseNetworkEvent(new RepairEntityMessage(GetNetEntity(ship.Owner), job.Indices, specId, spec));
        return spawned;
    }

    /// <summary>
    /// Rebuilds a structure only the slip tracks. Nothing here touches the hand device's snapshot,
    /// which never knew about this structure in the first place.
    /// </summary>
    private EntityUid? ReinstateDrydockPart(
        Entity<ShipRepairStationComponent> station,
        EntityUid grid,
        MapGridComponent gridComp,
        DrydockPart part)
    {
        // Check the protected original again at execution time as it may have been moved after the
        // quote was accepted. A live shield, console or weapon must never yield a second copy.
        if (StillStanding(part, grid) || _drydock.OriginalPreventsReplacement(part))
            return null;

        // A crewman may have rebuilt this between the survey and now. What the slip filed or just
        // built is not his, e.g. the other window of a corner.
        if (TileHolds(grid, gridComp, part.Tile, part.Proto, out var present, station.Comp.Filed))
        {
            part.Original = present;
            return null;
        }

        if (!_proto.HasIndex(part.Proto)
            || SlotTaken(grid, gridComp, part.Tile, part.Proto, part.Rotation, station.Comp.Filed))
        {
            return null;
        }

        ClearRemnants(grid, gridComp, part.Tile, part.Proto);

        var spawned = SpawnPart(part.Proto, (grid, gridComp), part.LocalPosition, part.Rotation);

        part.Original = spawned;
        station.Comp.Filed.Add(spawned);
        station.Comp.Rebuilt.Add(part);

        AdoptUnmountedWeapon(grid, gridComp, part, spawned);
        return spawned;
    }

    /// <summary>
    /// A gun hunts for its hardpoint only as it spawns, and a hardpoint arriving afterwards never looks
    /// down at what is standing on it. Ordering normally spares us that, but a gun the slip did not
    /// rebuild - one a crewman put back by hand, or one that outlived its mount - would stay a dead
    /// prop otherwise, so a freshly seated mount claims whatever is sitting unmounted on its tile.
    /// </summary>
    private void AdoptUnmountedWeapon(EntityUid grid, MapGridComponent gridComp, DrydockPart part, EntityUid mount)
    {
        if (!part.IsMount)
            return;

        foreach (var ent in _map.GetAnchoredEntities(grid, gridComp, part.Tile))
        {
            if (ent == mount || !TryComp<HardpointAnchorableOnlyComponent>(ent, out var weapon) || weapon.anchoredTo != null)
                continue;

            if (_hardpoint.TryAnchorToHardpoint(ent, weapon))
                break;
        }
    }

    /// <summary>
    /// A snapshotted part counts as missing when nothing is left of it, or when what is left is
    /// floating off the grid as debris.
    /// </summary>
    private bool IsSpecMissing(ShipRepairEntitySpecifier spec, string protoId)
    {
        if (spec.OriginalEntity is not { } net || !TryGetEntity(net, out var original) || TerminatingOrDeleted(original.Value))
            return true;

        // A stale handle off a reloaded grid that now names some unrelated entity.
        if (!IsOriginalOf(original.Value, protoId))
            return true;

        // The hand-tool snapshot normally replaces a live entity floating in map space and deletes
        // the old one. Protected equipment instead keeps its identity everywhere and is only
        // eligible for replacement once the original has actually been destroyed.
        if (_drydock.ReplacementRequiresDestruction(original.Value))
            return false;

        var ev = new ShipRepairReinstateQueryEvent(true);
        RaiseLocalEvent(original.Value, ref ev);

        if (ev.Handled)
            return ev.Repairable;

        return Transform(original.Value).GridUid == null;
    }

    /// <summary>
    /// Everything on file was anchored when it was filed. Wall fittings such as buttons do not anchor
    /// themselves on spawn, so they are bolted down here or they come back loose and dead.
    /// </summary>
    private EntityUid SpawnPart(string protoId, Entity<MapGridComponent> grid, Vector2 localPosition, Angle rotation)
    {
        var spawned = Spawn(protoId, new EntityCoordinates(grid, localPosition));
        var xform = Transform(spawned);
        _transform.SetLocalRotation(spawned, rotation, xform);

        if (!xform.Anchored)
            _transform.AnchorEntity((spawned, xform), grid);

        _dynamicCodes.ApplyGridKeys(grid, spawned);
        return spawned;
    }

    private bool IsOriginalOf(EntityUid uid, string protoId)
    {
        if (MetaData(uid).EntityPrototype?.ID == protoId)
            return true;

        return TryComp<ShipRepairableComponent>(uid, out var repairable) && repairable.RepairTo == protoId;
    }

    /// <summary>
    /// Puts back the signal links and device-list entries the rebuilt parts had with the rest of the hull.
    /// A spawned button or vent comes up linked to nothing otherwise.
    /// </summary>
    private void RestoreConnections(ShipRepairStationComponent comp, EntityUid ship)
    {
        if (comp.Rebuilt.Count == 0 || !TryComp<ShipDrydockSnapshotComponent>(ship, out var drydock))
            return;

        var rebuilt = new HashSet<DrydockPart>(comp.Rebuilt);

        foreach (var part in drydock.Parts)
        {
            if (!IsAboard(part, ship, out var uid))
                continue;

            foreach (var (sink, ports) in part.Links)
            {
                if ((rebuilt.Contains(part) || rebuilt.Contains(sink)) && IsAboard(sink, ship, out var sinkUid))
                    _deviceLink.SaveLinks(null, uid, sinkUid, ports);
            }

            if (part.ListedDevices.Count == 0 || !HasComp<DeviceListComponent>(uid))
                continue;

            var devices = new List<EntityUid>();
            foreach (var device in part.ListedDevices)
            {
                if ((rebuilt.Contains(part) || rebuilt.Contains(device)) && IsAboard(device, ship, out var deviceUid))
                    devices.Add(deviceUid);
            }

            if (devices.Count > 0)
                _deviceList.UpdateDeviceList(uid, devices, merge: true);
        }
    }

    private bool IsAboard(DrydockPart part, EntityUid ship, out EntityUid uid)
    {
        uid = part.Original ?? EntityUid.Invalid;
        return part.Original is { } original
               && !TerminatingOrDeleted(original)
               && Transform(original).GridUid == ship;
    }

    private void CompleteRepair(Entity<ShipRepairStationComponent> station, EntityUid ship)
    {
        // Reinstated guns announced themselves before they were seated on their mounts, so the
        // targeting consoles drew their groups without them. Re-derive now the hull is whole.
        _pointCannons.InvalidateGridCannons(ship);

        RestoreConnections(station.Comp, ship);
        ScheduleAtmosRefresh(station, ship);

        _audio.PlayPvs(station.Comp.ConfirmSound, station.Owner);
        _popup.PopupEntity(Loc.GetString("ship-repair-station-popup-finished", ("ship", Name(ship))), station.Owner);

        ClearJob(station.Comp);
        PushUi(station);
    }

    /// <summary>
    /// Stops a running job, handing back the share of the fee covering work never done.
    /// </summary>
    private void AbortRepair(Entity<ShipRepairStationComponent> station, bool refund)
    {
        var comp = station.Comp;
        if (comp.Target is not { } target)
            return;

        if (!TerminatingOrDeleted(target))
            RestoreConnections(comp, target);

        // Refunded on what is left undone rather than on how much of the list is left. The yard works
        // the expensive framing first and the mopping last, so counting entries would hand most of the
        // fee back to a customer who has already had his guns and his hull plating welded on.
        if (refund && comp.Jobs.Count > 0)
        {
            var outstanding = 0f;
            foreach (var job in comp.Jobs)
            {
                // The drone restock was billed flat, so it is handed back flat too.
                outstanding += job.Kind == ShipRepairJobKind.DroneRestock ? job.Cost : job.Cost * comp.PriceMarkup;
            }

            Refund(station, (int) Math.Min(comp.AmountPaid, MathF.Ceiling(outstanding)));
        }

        ClearJob(comp);
    }

    private static void ClearJob(ShipRepairStationComponent comp)
    {
        comp.RefillSeeds = new HashSet<Vector2i>();
        comp.Target = null;
        comp.Jobs = new List<ShipRepairJob>();
        comp.JobsTotal = 0;
        comp.JobsDone = 0;
        comp.WorkPerTick = 1f;
        comp.WorkBudget = 0f;
        comp.Filed = new HashSet<EntityUid>();
        comp.Rebuilt = new List<DrydockPart>();
        comp.AmountPaid = 0;
        comp.Payer = null;
        comp.StartTime = TimeSpan.Zero;
        comp.EndTime = TimeSpan.Zero;
    }
}
