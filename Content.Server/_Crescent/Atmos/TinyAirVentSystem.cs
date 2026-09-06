using System.Diagnostics.CodeAnalysis;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared._Crescent.Atmos;
using Content.Shared.Atmos;
using Content.Shared.Audio;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Examine;
using Robust.Shared.Containers;

namespace Content.Server._Crescent.Atmos;

/// <summary>
/// Drives <see cref="TinyAirVentComponent"/>: a wrench-anchored vent that pressurises the tile it
/// sits on straight out of a slotted gas tank, with no pipe net and no power behind it.
/// </summary>
public sealed class TinyAirVentSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TinyAirVentComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<TinyAirVentComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<TinyAirVentComponent, AtmosDeviceUpdateEvent>(OnDeviceUpdate);
        SubscribeLocalEvent<TinyAirVentComponent, AtmosDeviceDisabledEvent>(OnDeviceDisabled);
        SubscribeLocalEvent<TinyAirVentComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<TinyAirVentComponent, EntInsertedIntoContainerMessage>(OnTankInserted);
        SubscribeLocalEvent<TinyAirVentComponent, EntRemovedFromContainerMessage>(OnTankRemoved);
    }

    private void OnStartup(Entity<TinyAirVentComponent> ent, ref ComponentStartup args)
    {
        _slots.AddItemSlot(ent, ent.Comp.TankSlotId, ent.Comp.TankSlot);
        SetState(ent, TinyAirVentState.Off);
    }

    private void OnShutdown(Entity<TinyAirVentComponent> ent, ref ComponentShutdown args)
    {
        _slots.RemoveItemSlot(ent, ent.Comp.TankSlot);
    }

    private void OnTankInserted(Entity<TinyAirVentComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        // The next atmos tick decides whether it can actually vent; just stop lying about the old tank.
        if (args.Container.ID == ent.Comp.TankSlotId)
            SetState(ent, TinyAirVentState.Off);
    }

    private void OnTankRemoved(Entity<TinyAirVentComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == ent.Comp.TankSlotId)
            SetState(ent, TinyAirVentState.Off);
    }

    private void OnDeviceDisabled(Entity<TinyAirVentComponent> ent, ref AtmosDeviceDisabledEvent args)
    {
        // Unanchored or off-grid, so it stops getting update ticks. Don't leave it hissing.
        SetState(ent, TinyAirVentState.Off);
    }

    private void OnDeviceUpdate(Entity<TinyAirVentComponent> ent, ref AtmosDeviceUpdateEvent args)
    {
        var vent = ent.Comp;

        if (!TryGetTank(ent, out var tank))
        {
            SetState(ent, TinyAirVentState.Off);
            return;
        }

        var environment = _atmos.GetContainingMixture(ent.Owner, args.Grid, args.Map, true, true);

        // Inside a wall or otherwise nowhere to vent into. Immutable mixtures - space, and a map's own
        // atmosphere - are the important case: AtmosphereSystem.Merge silently drops gas handed to one, but
        // tank.Air.Remove() below has already taken it out of the tank, so venting there would drain a full
        // cartridge into nothing while the pressure never moves. That is exactly a wreck or a bare grid.
        if (environment == null || environment.Immutable || environment.Volume <= 0f || environment.Temperature <= 0f)
        {
            SetState(ent, TinyAirVentState.Off);
            return;
        }

        if (tank.Air.TotalMoles <= 0f)
        {
            SetState(ent, TinyAirVentState.Off);
            return;
        }

        // The compressor can only hold the room at some multiple of what is left in the tank, so a
        // near-empty tank tops out below one atmosphere instead of pushing forever.
        var ceiling = MathF.Min(vent.TargetPressure, tank.Air.Pressure * vent.PumpPower);

        // Room's already at what this tank can manage.
        if (environment.Pressure >= ceiling)
        {
            SetState(ent, TinyAirVentState.Off);
            return;
        }

        // Moles needed to raise this tile by the tick's share of the rate, capped so we never overshoot.
        var pressureDelta = MathF.Min(args.dt * vent.PressureRate, ceiling - environment.Pressure);
        var moles = pressureDelta * environment.Volume / (environment.Temperature * Atmospherics.R);

        if (moles <= 0f)
        {
            SetState(ent, TinyAirVentState.Off);
            return;
        }

        _atmos.Merge(environment, tank.Air.Remove(moles));
        SetState(ent, TinyAirVentState.Venting);
    }

    private void OnExamined(Entity<TinyAirVentComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        if (!TryGetTank(ent, out var tank))
        {
            args.PushMarkup(Loc.GetString("tiny-air-vent-examine-empty"));
            return;
        }

        args.PushMarkup(Loc.GetString("tiny-air-vent-examine-tank",
            ("pressure", MathF.Round(tank.Air.Pressure))));
        args.PushMarkup(Loc.GetString(ent.Comp.State == TinyAirVentState.Venting
            ? "tiny-air-vent-examine-venting"
            : "tiny-air-vent-examine-idle"));
    }

    private bool TryGetTank(Entity<TinyAirVentComponent> ent, [NotNullWhen(true)] out GasTankComponent? tank)
    {
        tank = null;
        return ent.Comp.TankSlot.Item is { } item && TryComp(item, out tank);
    }

    private void SetState(Entity<TinyAirVentComponent> ent, TinyAirVentState state)
    {
        if (ent.Comp.State == state)
            return;

        ent.Comp.State = state;
        _appearance.SetData(ent, TinyAirVentVisuals.State, state);
        _ambient.SetAmbience(ent, state == TinyAirVentState.Venting);
    }
}
