using System.Linq;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Emp;
using Content.Server.Mech.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Emp;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Mech;
using Content.Shared.Mech.Components;
using Content.Shared.Mech.EntitySystems;
using Content.Shared.Movement.Events;
using Content.Shared.Popups;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Tools.Components;
using Content.Shared.Verbs;
using Content.Shared.Wires;
using Content.Server.Body.Systems;
using Content.Shared.Tools.Systems;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Content.Shared.Whitelist;

namespace Content.Server.Mech.Systems;

/// <inheritdoc/>
public sealed partial class MechSystem : SharedMechSystem
{
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly ContainerSystem _container = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] private readonly SharedToolSystem _toolSystem = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MechComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<MechComponent, EntInsertedIntoContainerMessage>(OnInsertBattery);
        SubscribeLocalEvent<MechComponent, EntRemovedFromContainerMessage>(OnBatteryRemovedFromContainer);
        SubscribeLocalEvent<MechComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<MechComponent, GetVerbsEvent<AlternativeVerb>>(OnAlternativeVerb);
        SubscribeLocalEvent<MechComponent, MechOpenUiEvent>(OnOpenUi);
        SubscribeLocalEvent<MechComponent, MechOpenRadarEvent>(OnOpenRadar);
        SubscribeLocalEvent<MechComponent, RemoveBatteryEvent>(OnRemoveBattery);
        SubscribeLocalEvent<MechComponent, MechEntryEvent>(OnMechEntry);
        SubscribeLocalEvent<MechComponent, MechExitEvent>(OnMechExit);

        SubscribeLocalEvent<MechComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<MechComponent, MechEquipmentRemoveMessage>(OnRemoveEquipmentMessage);
        SubscribeLocalEvent<MechComponent, EmpPulseEvent>(OnEmpPulse);
        SubscribeLocalEvent<MechComponent, EmpDisabledRemoved>(OnEmpDisabledRemoved);

        SubscribeLocalEvent<MechComponent, UpdateCanMoveEvent>(OnMechCanMoveEvent);
        SubscribeLocalEvent<BatteryComponent, ChargeChangedEvent>(OnBatteryChargeChanged);
        SubscribeLocalEvent<EmpDisabledComponent, ComponentAdd>(OnEmpDisabledAdded);


        SubscribeLocalEvent<MechPilotComponent, ToolUserAttemptUseEvent>(OnToolUseAttempt);
        SubscribeLocalEvent<MechPilotComponent, InhaleLocationEvent>(OnInhale);
        SubscribeLocalEvent<MechPilotComponent, ExhaleLocationEvent>(OnExhale);
        SubscribeLocalEvent<MechPilotComponent, AtmosExposedGetAirEvent>(OnExpose);

        SubscribeLocalEvent<MechAirComponent, GetFilterAirEvent>(OnGetFilterAir);

        #region Equipment UI message relays
        SubscribeLocalEvent<MechComponent, MechGrabberEjectMessage>(ReceiveEquipmentUiMesssages);
        SubscribeLocalEvent<MechComponent, MechSoundboardPlayMessage>(ReceiveEquipmentUiMesssages);
        #endregion
    }

    private void OnMechCanMoveEvent(EntityUid uid, MechComponent component, UpdateCanMoveEvent args)
    {
        if (component.Broken ||
            component.Integrity <= 0 ||
            component.Energy <= 0 ||
            HasComp<EmpDisabledComponent>(uid))
            args.Cancel();
    }

    private void OnEmpPulse(EntityUid uid, MechComponent component, ref EmpPulseEvent args)
    {
        args.Affected = true;
        args.Disabled = true;
    }

    private void OnEmpDisabledAdded(EntityUid uid, EmpDisabledComponent component, ComponentAdd args)
    {
        if (!TryComp<MechComponent>(uid, out var mech))
            return;

        _actionBlocker.UpdateCanMove(uid);
        UpdateUserInterface(uid, mech);
        UpdateAppearance(uid, mech);
    }

    private void OnEmpDisabledRemoved(EntityUid uid, MechComponent component, EmpDisabledRemoved args)
    {
        _actionBlocker.UpdateCanMove(uid);
        UpdateUserInterface(uid, component);
        UpdateAppearance(uid, component);
    }

    private void OnBatteryChargeChanged(EntityUid uid, BatteryComponent component, ref ChargeChangedEvent args)
    {
        var parent = Transform(uid).ParentUid;
        if (!TryComp<MechComponent>(parent, out var mech) ||
            mech.BatterySlot == null ||
            mech.BatterySlot.ContainedEntity != uid)
            return;

        mech.Energy = args.Charge;
        mech.MaxEnergy = args.MaxCharge;

        Dirty(parent, mech);
        _actionBlocker.UpdateCanMove(parent);
        UpdateUserInterface(parent, mech);
        UpdateAppearance(parent, mech);
    }

    private void OnInteractUsing(EntityUid uid, MechComponent component, InteractUsingEvent args)
    {
        if (TryComp<WiresPanelComponent>(uid, out var panel) && !panel.Open)
            return;

        if (component.BatterySlot.ContainedEntity == null && TryComp<BatteryComponent>(args.Used, out var battery))
        {
            InsertBattery(uid, args.Used, component, battery);
            _actionBlocker.UpdateCanMove(uid);
            return;
        }

        if (_toolSystem.HasQuality(args.Used, "Prying") && component.BatterySlot.ContainedEntity != null)
        {
            var doAfterEventArgs = new DoAfterArgs(EntityManager, args.User, component.BatteryRemovalDelay,
                new RemoveBatteryEvent(), uid, target: uid, used: args.Target)
            {
                BreakOnMove = true
            };

            _doAfter.TryStartDoAfter(doAfterEventArgs);
        }
    }

    private void OnInsertBattery(EntityUid uid, MechComponent component, EntInsertedIntoContainerMessage args)
    {
        if (args.Container != component.BatterySlot || !TryComp<BatteryComponent>(args.Entity, out var battery))
            return;

        component.Energy = battery.CurrentCharge;
        component.MaxEnergy = battery.MaxCharge;

        Dirty(uid, component);
        _actionBlocker.UpdateCanMove(uid);
        UpdateAppearance(uid, component);
        UpdateUserInterface(uid, component);
    }

    private void OnBatteryRemovedFromContainer(EntityUid uid, MechComponent component, EntRemovedFromContainerMessage args)
    {
        if (args.Container != component.BatterySlot)
            return;

        component.Energy = 0;
        component.MaxEnergy = 0;
        Dirty(uid, component);
        _actionBlocker.UpdateCanMove(uid);
        UpdateAppearance(uid, component);
        UpdateUserInterface(uid, component);
    }

    private void OnRemoveBattery(EntityUid uid, MechComponent component, RemoveBatteryEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        RemoveBattery(uid, component);
        _actionBlocker.UpdateCanMove(uid);

        args.Handled = true;
    }

    private void OnMapInit(EntityUid uid, MechComponent component, MapInitEvent args)
    {
        var xform = Transform(uid);
        // TODO: this should use containerfill?
        foreach (var equipment in component.StartingEquipment)
        {
            var ent = Spawn(equipment, xform.Coordinates);
            InsertEquipment(uid, ent, component);
        }

        // TODO: this should just be damage and battery
        component.Integrity = component.MaxIntegrity;
        // ContainerFill may already have inserted a partially charged battery.
        // Read the cell instead of assuming a full charge at map initialization.
        var battery = component.BatterySlot.ContainedEntity;
        component.Energy = TryComp<BatteryComponent>(battery, out var cell) ? cell.CurrentCharge : 0;
        component.MaxEnergy = cell?.MaxCharge ?? 0;

        _actionBlocker.UpdateCanMove(uid);
        Dirty(uid, component);
        UpdateAppearance(uid, component);
    }

    private void OnRemoveEquipmentMessage(EntityUid uid, MechComponent component, MechEquipmentRemoveMessage args)
    {
        var equip = GetEntity(args.Equipment);

        if (!Exists(equip) || Deleted(equip))
            return;

        if (!component.EquipmentContainer.ContainedEntities.Contains(equip))
            return;

        RemoveEquipment(uid, equip, component);
    }

    private void OnOpenUi(EntityUid uid, MechComponent component, MechOpenUiEvent args)
    {
        args.Handled = true;
        ToggleMechUi(uid, component);
    }

    private void OnOpenRadar(EntityUid uid, MechComponent component, MechOpenRadarEvent args)
    {
        var pilot = GetEntity(args.Pilot);
        if (!TryComp<ActorComponent>(pilot, out var actor))
            return;

        _ui.TryToggleUi(uid, RadarConsoleUiKey.Key, actor.PlayerSession);
    }

    private void OnToolUseAttempt(EntityUid uid, MechPilotComponent component, ref ToolUserAttemptUseEvent args)
    {
        if (args.Target == component.Mech)
            args.Cancelled = true;
    }

    private void OnAlternativeVerb(EntityUid uid, MechComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || component.Broken)
            return;

        if (CanInsert(uid, args.User, component))
        {
            var enterVerb = new AlternativeVerb
            {
                Text = Loc.GetString("mech-verb-enter"),
                Act = () =>
                {
                    var doAfterEventArgs = new DoAfterArgs(EntityManager, args.User, component.EntryDelay, new MechEntryEvent(), uid, target: uid)
                    {
                        BreakOnMove = true,
                    };

                    _doAfter.TryStartDoAfter(doAfterEventArgs);
                }
            };
            var openUiVerb = new AlternativeVerb //can't hijack someone else's mech
            {
                Act = () => ToggleMechUi(uid, component, args.User),
                Text = Loc.GetString("mech-ui-open-verb")
            };
            args.Verbs.Add(enterVerb);
            args.Verbs.Add(openUiVerb);
        }
        else if (!IsEmpty(component))
        {
            var ejectVerb = new AlternativeVerb
            {
                Text = Loc.GetString("mech-verb-exit"),
                Priority = 1, // Promote to top to make ejecting the ALT-click action
                Act = () =>
                {
                    if (args.User == uid || args.User == component.PilotSlot.ContainedEntity)
                    {
                        TryEject(uid, component);
                        return;
                    }

                    var doAfterEventArgs = new DoAfterArgs(EntityManager, args.User, component.ExitDelay,
                        new MechExitEvent(), uid, target: uid);

                    _doAfter.TryStartDoAfter(doAfterEventArgs);
                }
            };
            args.Verbs.Add(ejectVerb);
        }
    }

    private void OnMechEntry(EntityUid uid, MechComponent component, MechEntryEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (_whitelistSystem.IsWhitelistFail(component.PilotWhitelist, args.User))
        {
            _popup.PopupEntity(Loc.GetString("mech-no-enter", ("item", uid)), args.User);
            return;
        }

        TryInsert(uid, args.Args.User, component);
        _actionBlocker.UpdateCanMove(uid);
        args.Handled = true;
    }

    private void OnMechExit(EntityUid uid, MechComponent component, MechExitEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        TryEject(uid, component);
        args.Handled = true;
    }

    private void OnDamageChanged(EntityUid uid, MechComponent component, DamageChangedEvent args)
    {
        var integrity = component.MaxIntegrity - args.Damageable.TotalDamage;
        SetIntegrity(uid, integrity, component);

        if (args.DamageIncreased &&
            args.DamageDelta != null &&
            component.PilotSlot.ContainedEntity != null)
        {
            var damage = args.DamageDelta * component.MechToPilotDamageMultiplier;
            _damageable.TryChangeDamage(component.PilotSlot.ContainedEntity, damage);
        }
    }

    private void ToggleMechUi(EntityUid uid, MechComponent? component = null, EntityUid? user = null)
    {
        if (!Resolve(uid, ref component))
            return;
        user ??= component.PilotSlot.ContainedEntity;
        if (user == null)
            return;

        if (!TryComp<ActorComponent>(user, out var actor))
            return;

        _ui.TryToggleUi(uid, MechUiKey.Key, actor.PlayerSession);
        UpdateUserInterface(uid, component);
    }

    private void ReceiveEquipmentUiMesssages<T>(EntityUid uid, MechComponent component, T args) where T : MechEquipmentUiMessage
    {
        var ev = new MechEquipmentUiMessageRelayEvent(args);
        var allEquipment = new List<EntityUid>(component.EquipmentContainer.ContainedEntities);
        var argEquip = GetEntity(args.Equipment);

        foreach (var equipment in allEquipment)
        {
            if (argEquip == equipment)
                RaiseLocalEvent(equipment, ev);
        }
    }

    public override void UpdateUserInterface(EntityUid uid, MechComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        base.UpdateUserInterface(uid, component);

        var ev = new MechEquipmentUiStateReadyEvent();
        foreach (var ent in component.EquipmentContainer.ContainedEntities)
        {
            RaiseLocalEvent(ent, ev);
        }

        var state = new MechBoundUiState
        {
            EquipmentStates = ev.States
        };
        _ui.SetUiState(uid, MechUiKey.Key, state);
    }

    public override void BreakMech(EntityUid uid, MechComponent? component = null)
    {
        base.BreakMech(uid, component);

        _ui.CloseUi(uid, MechUiKey.Key);
        _actionBlocker.UpdateCanMove(uid);
    }

    public override bool TryChangeEnergy(EntityUid uid, FixedPoint2 delta, MechComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (!base.TryChangeEnergy(uid, delta, component))
            return false;

        var battery = component.BatterySlot.ContainedEntity;
        if (battery == null)
            return false;

        if (!TryComp<BatteryComponent>(battery, out var batteryComp))
            return false;

        _battery.SetCharge(battery!.Value, batteryComp.CurrentCharge + delta.Float(), batteryComp);
        if (batteryComp.CurrentCharge != component.Energy) //if there's a discrepency, we have to resync them
        {
            Log.Debug($"Battery charge was not equal to mech charge. Battery {batteryComp.CurrentCharge}. Mech {component.Energy}");
            component.Energy = batteryComp.CurrentCharge;
            Dirty(uid, component);
        }
        _actionBlocker.UpdateCanMove(uid);
        return true;
    }

    public void InsertBattery(EntityUid uid, EntityUid toInsert, MechComponent? component = null, BatteryComponent? battery = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        if (!Resolve(toInsert, ref battery, false))
            return;

        _container.Insert(toInsert, component.BatterySlot);
        component.Energy = battery.CurrentCharge;
        component.MaxEnergy = battery.MaxCharge;

        _actionBlocker.UpdateCanMove(uid);

        Dirty(uid, component);
        UpdateUserInterface(uid, component);
    }

    public void RemoveBattery(EntityUid uid, MechComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        _container.EmptyContainer(component.BatterySlot);
        component.Energy = 0;
        component.MaxEnergy = 0;

        _actionBlocker.UpdateCanMove(uid);

        Dirty(uid, component);
        UpdateUserInterface(uid, component);
    }

    #region Atmos Handling
    // Crescent: every cockpit has life support, so the pilot always breathes and feels a standard atmosphere,
    // whether the hull is airtight or not and whatever is outside (vacuum, fire, plasma).
    private void OnInhale(EntityUid uid, MechPilotComponent component, InhaleLocationEvent args)
    {
        if (GetCockpitAir(component.Mech) is { } air)
            args.Gas = air;
    }

    private void OnExhale(EntityUid uid, MechPilotComponent component, ExhaleLocationEvent args)
    {
        if (GetCockpitAir(component.Mech) is { } air)
            args.Gas = air;
    }

    private void OnExpose(EntityUid uid, MechPilotComponent component, ref AtmosExposedGetAirEvent args)
    {
        if (args.Handled || GetCockpitAir(component.Mech) is not { } air)
            return;

        args.Gas = air;
        args.Handled = true;
    }

    /// <summary>
    /// Scrubs and refills the cockpit to one atmosphere of 20°C air before the pilot uses it.
    /// </summary>
    private GasMixture? GetCockpitAir(EntityUid mech)
    {
        if (!HasComp<MechComponent>(mech))
            return null;

        var air = EnsureComp<MechAirComponent>(mech).Air;
        air.Clear();
        air.Temperature = Atmospherics.T20C;
        var moles = Atmospherics.OneAtmosphere * air.Volume / (Atmospherics.R * Atmospherics.T20C);
        air.AdjustMoles(Gas.Oxygen, moles * Atmospherics.OxygenStandard);
        air.AdjustMoles(Gas.Nitrogen, moles * Atmospherics.NitrogenStandard);
        return air;
    }

    private void OnGetFilterAir(EntityUid uid, MechAirComponent comp, ref GetFilterAirEvent args)
    {
        if (args.Air != null)
            return;

        // only airtight mechs get internal air
        if (!TryComp<MechComponent>(uid, out var mech) || !mech.Airtight)
            return;

        args.Air = comp.Air;
    }
    #endregion
}
