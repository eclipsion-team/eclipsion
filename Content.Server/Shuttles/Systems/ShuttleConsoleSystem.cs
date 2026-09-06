using System.Linq;
using Content.Server._Crescent.DynamicAcces;
using Content.Server.Access.Systems;
using Content.Server.DeviceLinking.Systems;
using Content.Server.PointCannons;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Systems;
using Content.Shared._Crescent;
using Content.Shared._Crescent.Helpers;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Access.Components; // Frontier
using Content.Shared.ActionBlocker;
using Content.Shared.Alert;
using Content.Shared._Crescent.CCvars;
using Content.Shared.Crescent.Radar;
using Content.Shared.Damage; // KS14
using Robust.Shared.Timing; // KS14
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Events;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Tag;
using Content.Shared.Movement.Systems;
using Content.Shared.NamedModules.Components;
using Content.Shared.PointCannons;
using Content.Shared.Power;
using Content.Shared.Shipyard.Components;
using Content.Shared.Shuttles.UI.MapObjects;
using Content.Shared.Timing;
using Robust.Server.GameObjects;
using Robust.Shared.Collections;
using Robust.Shared.Configuration; // hullrot
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Utility;
using Content.Shared.UserInterface;
using Robust.Server.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;


namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem : SharedShuttleConsoleSystem
{
    [Dependency] private readonly SharedMapSystem _mapManager = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly AlertsSystem _alertsSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedContentEyeSystem _eyeSystem = default!;
    [Dependency] private readonly DynamicCodeSystem _codes = default!;
    [Dependency] private readonly CrescentHelperSystem _crescent = default!;
    [Dependency] private readonly AccessSystem _acces = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly DeviceLinkSystem _link = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly IPrototypeManager _manager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly _Lavaland.Shuttles.Systems.DockingConsoleSystem _dockingConsole = default!; // Lavaland Change: FTL
    [Dependency] private readonly IConfigurationManager _cfg = default!; // hullrot: console ratelimits
    [Dependency] private readonly IGameTiming _timing = default!; // KS14

    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private readonly HashSet<Entity<ShuttleConsoleComponent>> _consoles = new();

    /// <summary>
    ///     KS14: when each grid last took damage, for the console's TAKING FIRE warning.
    ///     Written from a subscription on every damageable entity, so the handler stays a
    ///         grid lookup and a dictionary write and nothing more.
    /// </summary>
    private readonly Dictionary<EntityUid, TimeSpan> _lastGridDamage = new();

    /// <summary>KS14: how long after the last hit the warning stays lit.</summary>
    private static readonly TimeSpan TakingFireHold = TimeSpan.FromSeconds(3);

    private float _accumulatedFrameTime;
    private float _uiTps;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, CrescentCVars.ShuttleConsoleUiTps, (float val) => { _uiTps = val; }, true);

        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<ShuttleConsoleComponent, ComponentShutdown>(OnConsoleShutdown);
        SubscribeLocalEvent<ShuttleConsoleComponent, PowerChangedEvent>(OnConsolePowerChange);
        SubscribeLocalEvent<ShuttleConsoleComponent, AnchorStateChangedEvent>(OnConsoleAnchorChange);
        SubscribeLocalEvent<ShuttleConsoleComponent, ReAnchorEvent>(OnConsoleReAnchor);
        SubscribeLocalEvent<ShuttleConsoleComponent, ActivatableUIOpenAttemptEvent>(OnConsoleUIOpenAttempt);
        SubscribeLocalEvent<ShuttleConsoleComponent, AfterInteractUsingEvent>(OnAfterInteractUsing);
        SubscribeLocalEvent<ShuttleConsoleComponent, BoundUserInterfaceMessageAttempt>(BUIValidation);
        SubscribeLocalEvent<ShuttleConsoleComponent, EntInsertedIntoContainerMessage>(UpdateUI);
        SubscribeLocalEvent<ShuttleConsoleComponent, EntRemovedFromContainerMessage>(UpdateUI);
        SubscribeLocalEvent<ShuttleConsoleComponent, AfterActivatableUIOpenEvent>(UpdateUI);
        SubscribeLocalEvent<ShuttleConsoleComponent, TryMakeEmployeeMessage>(OnToggleEmployee);
        Subs.BuiEvents<ShuttleConsoleComponent>(ShuttleConsoleUiKey.Key, subs =>
        {
            subs.Event<ShuttleConsoleFTLBeaconMessage>(OnBeaconFTLMessage);
            subs.Event<ShuttleConsoleFTLPositionMessage>(OnPositionFTLMessage);
            subs.Event<BoundUIClosedEvent>(OnConsoleUIClose);
            subs.Event<BoundUIOpenedEvent>(OnConsoleUIOpened);
            subs.Event<SwitchedToCrewHudMessage>(OnCrewSwitch);
        });

        SubscribeLocalEvent<DroneConsoleComponent, ConsoleShuttleEvent>(OnCargoGetConsole);
        SubscribeLocalEvent<DroneConsoleComponent, AfterActivatableUIOpenEvent>(OnDronePilotConsoleOpen);
        Subs.BuiEvents<DroneConsoleComponent>(ShuttleConsoleUiKey.Key, subs =>
        {
            subs.Event<BoundUIClosedEvent>(OnDronePilotConsoleClose);
        });

        // KS14: universal damage hook for the TAKING FIRE warning. DamageableComponent is on
        //   every damageable entity and nothing else claims this pair, so one subscription
        //   covers hull, walls, machines and crew alike - the shield-only signal it replaces
        //   went quiet exactly when the ship was worst off.
        SubscribeLocalEvent<DamageableComponent, DamageChangedEvent>(OnAnyDamageChanged);
        SubscribeLocalEvent<GridRemovalEvent>(OnGridRemovedForDamage);

        SubscribeLocalEvent<DockEvent>(OnDock);
        SubscribeLocalEvent<UndockEvent>(OnUndock);

        SubscribeLocalEvent<PilotComponent, ComponentGetState>(OnGetState);

        SubscribeLocalEvent<FTLDestinationComponent, ComponentStartup>(OnFtlDestStartup);
        SubscribeLocalEvent<FTLDestinationComponent, ComponentShutdown>(OnFtlDestShutdown);

        SubscribeLocalEvent<ShuttleConsoleComponent, NavConsoleGroupPressedMessage>(OnGroupPressed);
        SubscribeLocalEvent<NamedModulesComponent, ModuleNamingChangeEvent>(OnNameChange);

        SubscribeLocalEvent<ShuttleConsoleComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<ShuttleConsoleComponent, ComponentRemove>(OnComponentRemove);

        InitializeFTL();
    }

    public void RefreshIFFState()
    {
        var query = AllEntityQuery<ShuttleConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
        {
            if (console.LastUpdatedState == null || console.LastUpdatedState.IFFState == null)
            {
                continue;
            }

            // Closed consoles rebuild their state when opened, so refreshing their turret cache only wastes a
            // world-wide turret scan.
            if (!_ui.IsUiOpen(uid, ShuttleConsoleUiKey.Key))
            {
                continue;
            }

            console.LastUpdatedState.IFFState.Turrets = GetAllTurrets(uid);
        }
    }

    private void BUIValidation(EntityUid uid, ShuttleConsoleComponent component, BoundUserInterfaceMessageAttempt args)
    {
        var uis = _ui.GetActorUis(args.Actor);

        foreach (var (_, key) in uis)
        {
            if (key is TargetingConsoleUiKey)
            {
                args.Cancel();
            }
        }
    }

    public void OnGroupPressed(EntityUid consoleUid, ShuttleConsoleComponent shuttleConsole, NavConsoleGroupPressedMessage args)
    {
        switch (args.Payload)
        {
            case 1: _link.InvokePort(consoleUid, "Group1"); break;
            case 2: _link.InvokePort(consoleUid, "Group2"); break;
            case 3: _link.InvokePort(consoleUid, "Group3"); break;
            case 4: _link.InvokePort(consoleUid, "Group4"); break;
            case 5: _link.InvokePort(consoleUid, "Group5"); break;
            default:
                break;
        };
    }

    private void OnToggleEmployee(EntityUid uid, ShuttleConsoleComponent comp, TryMakeEmployeeMessage args)
    {
        if (comp.accesState != ShuttleConsoleAccesState.CaptainAcces)
            return;
        if (comp.targetIdSlot?.Item is null)
            return;
        var grid = _transform.GetGrid(uid);
        if (grid is null)
            return;
        if (!TryComp<DynamicCodeHolderComponent>(grid, out var dynCodes))
            return;
        if (!dynCodes.mappedCodes.ContainsKey(args.chosenOption))
            return;
        var accesCodes = dynCodes.mappedCodes[args.chosenOption];
        var dynIdComp = EnsureComp<DynamicCodeHolderComponent>(comp.targetIdSlot.Item.Value);
        if (_codes.hasAllKeys(accesCodes, dynIdComp))
        {
            foreach(var key in accesCodes)
                _codes.RemoveKeyFromComponent(dynIdComp, key, args.chosenOption);
        }
        else
        {
            foreach (var key in accesCodes)
            {
                _codes.AddKeyToComponent(dynIdComp, key, null);
            }
        }
        //Logger.Error($"Trying to dirty {MetaData(comp.targetIdSlot.Item.Value).EntityName}");
        Dirty(comp.targetIdSlot.Item.Value, dynIdComp);
        //irtyEntity(comp.targetIdSlot.Item.Value);
        UpdateState(uid, comp);
    }

    private void OnFtlDestStartup(EntityUid uid, FTLDestinationComponent component, ComponentStartup args)
    {
        RefreshShuttleConsoles();
    }

    private void OnFtlDestShutdown(EntityUid uid, FTLDestinationComponent component, ComponentShutdown args)
    {
        RefreshShuttleConsoles();
    }

    private void OnDock(DockEvent ev)
    {
        RefreshShuttleConsoles();
    }

    private void OnUndock(UndockEvent ev)
    {
        RefreshShuttleConsoles();
    }

    /// <summary>
    /// Refreshes all the shuttle console data for a particular grid.
    /// </summary>
    public void RefreshShuttleConsoles(EntityUid gridUid)
    {
        var exclusions = new List<ShuttleExclusionObject>();
        GetExclusions(ref exclusions);
        _consoles.Clear();
        _lookup.GetChildEntities(gridUid, _consoles);

        foreach (var entity in _consoles)
        {
            UpdateState(entity, entity.Comp);
        }

        _dockingConsole.UpdateConsolesUsing(gridUid); // Lavaland Change: FTL
    }

    private void OnAfterInteractUsing(
        EntityUid uid,
        ShuttleConsoleComponent component,
        AfterInteractUsingEvent args
    )
    {
        if (component.accesState == ShuttleConsoleAccesState.NotDynamic)
            return;

        if (!_crescent.getGridOfEntity(uid, out var gridId))
            return;
        if (!TryComp<DynamicCodeHolderComponent>(gridId, out var dynamicAccesComponent))
            return;
        if (!TryComp<IdCardComponent>(args.Used, out var _))
            return;
        var dynIdComp = EnsureComp<DynamicCodeHolderComponent>(args.Used);
        // Crescent - a console that was not standing here when the ship was keyed has no key names of
        // its own yet; the grid keeps them so it can take them now rather than refuse every ID forever.
        AdoptGridKeyNames(component, dynamicAccesComponent);
        if (component.captainIdentifier is null || component.pilotIdentifier is null)
            return;

        // Crescent - a ship keyed off a mapping that files neither name would otherwise throw here.
        if (!dynamicAccesComponent.mappedCodes.TryGetValue(component.captainIdentifier, out var captainCodes)
            || !dynamicAccesComponent.mappedCodes.TryGetValue(component.pilotIdentifier, out var pilotCodes))
        {
            return;
        }

        if (_codes.hasKey(captainCodes, dynIdComp))
        {
            if (component.accesState != ShuttleConsoleAccesState.NoAcces)
            {
                component.accesState = ShuttleConsoleAccesState.NoAcces;
                _popup.PopupEntity("Console locked", uid, args.User, PopupType.Small);
                _itemSlotsSystem.TryEject(uid, SharedShuttleConsoleComponent.IdSlotName, args.User, out _);
                _itemSlotsSystem.SetLock(uid, SharedShuttleConsoleComponent.IdSlotName, true);
                return;
            }
            component.accesState = ShuttleConsoleAccesState.CaptainAcces;
            _audio.PlayPvs("/Audio/Machines/high_tech_confirm.ogg", uid, AudioParams.Default);
            _popup.PopupEntity("Console unlocked. Welcome onboard, captain.", uid, args.User);
            UpdateState(uid, component);
            return;
        }

        if (_codes.hasKey(pilotCodes, dynIdComp))
        {
            if (component.accesState != ShuttleConsoleAccesState.NoAcces)
            {
                component.accesState = ShuttleConsoleAccesState.NoAcces;
                _popup.PopupEntity("Console locked", uid, args.User, PopupType.Small);
                _itemSlotsSystem.TryEject(uid, SharedShuttleConsoleComponent.IdSlotName, args.User, out _);
                _itemSlotsSystem.SetLock(uid, SharedShuttleConsoleComponent.IdSlotName, true);
                return;
            }
            component.accesState = ShuttleConsoleAccesState.PilotAcces;
            _audio.PlayPvs("/Audio/Machines/high_tech_confirm.ogg", uid, AudioParams.Default);
            _popup.PopupEntity("Authorized to console as pilot.", uid, args.User);
            UpdateState(uid, component);
        }


    }

    private void OnComponentInit(EntityUid uid, ShuttleConsoleComponent component, ComponentInit args)
    {
        _itemSlotsSystem.AddItemSlot(uid, SharedShuttleConsoleComponent.IdSlotName, component.targetIdSlot);
        _itemSlotsSystem.SetLock(uid, SharedShuttleConsoleComponent.IdSlotName,true);
        var grid = Transform(uid).GridUid;
        if (grid is  null)
            return;
        if (TryComp<DynamicCodeHolderComponent>(grid.Value, out var gridCodes))
        {
            component.accesState = ShuttleConsoleAccesState.NoAcces;
            AdoptGridKeyNames(component, gridCodes);
        }
        else
        {
            component.accesState = ShuttleConsoleAccesState.NotDynamic;
        }
        RefreshShuttleConsoles(grid.Value);
    }

    /// <summary>
    ///     Crescent - takes the ship's captain and pilot key names off the grid.
    /// </summary>
    /// <remarks>
    ///     A ship hands those names out once, as its map initialises, to the consoles standing on it at that
    ///     moment. A console stood up later - built by a crewman, or welded back on by the drydock after the
    ///     original was shot out - misses that pass, and a console with no key names matches no swiped ID at
    ///     all: it sits on "swipe ID to authorize yourself" forever, whatever is presented to it.
    /// </remarks>
    private void AdoptGridKeyNames(ShuttleConsoleComponent component, DynamicCodeHolderComponent gridCodes)
    {
        component.captainIdentifier ??= gridCodes.captainIdentifier;
        component.pilotIdentifier ??= gridCodes.pilotIdentifier;
    }

    private void OnComponentRemove(EntityUid uid, ShuttleConsoleComponent component, ComponentRemove args)
    {
        _itemSlotsSystem.RemoveItemSlot(uid, component.targetIdSlot);

    }

    private void OnCrewSwitch(EntityUid uid, ShuttleConsoleComponent comp, SwitchedToCrewHudMessage args)
    {
        // Crescent: only the captain has anything to do with the id slot, and leaving it unlocked for
        // anyone else means the console eats the next swiped id instead of toggling its lock.
        var unlockSlot = args.Visible && comp.accesState == ShuttleConsoleAccesState.CaptainAcces;

        if (!unlockSlot)
            _itemSlotsSystem.TryEject(uid, comp.targetIdSlot, null, out var item);
        _itemSlotsSystem.SetLock(uid, SharedShuttleConsoleComponent.IdSlotName, !unlockSlot);
        UpdateState(uid, comp);

    }
    private void OnNameChange(EntityUid consoleUid, NamedModulesComponent comp, ModuleNamingChangeEvent args)
    {
        comp.ButtonNames = args.NewNames;
        Dirty(consoleUid, comp);
    }

    private void UpdateUI(EntityUid console, ShuttleConsoleComponent comp, object args)
    {
        UpdateState(console, comp);
    }

    private void OnConsoleReAnchor(EntityUid uid, ShuttleConsoleComponent comp, ReAnchorEvent args)
    {
        if (TryComp<DynamicCodeHolderComponent>(args.Grid, out var accesComp))
        {
            comp.accesState = ShuttleConsoleAccesState.NoAcces;
            AdoptGridKeyNames(comp, accesComp);
            _itemSlotsSystem.TryEject(uid, comp.targetIdSlot, null, out var item);
            _itemSlotsSystem.SetLock(uid, comp.targetIdSlot, true);
        }
        else
        {
            comp.accesState = ShuttleConsoleAccesState.NotDynamic;
        }

    }


    /// <summary>
    /// Refreshes all of the data for shuttle consoles.
    /// </summary>
    public void RefreshShuttleConsoles()
    {
        //var exclusions = new List<ShuttleExclusionObject>();
        //GetExclusions(ref exclusions);
        var query = AllEntityQuery<ShuttleConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            UpdateState(uid, comp);
        }
    }

    public void RefreshBulletStateForConsoles()
    {
        //var exclusions = new List<ShuttleExclusionObject>();
        //GetExclusions(ref exclusions);
        var query = AllEntityQuery<ShuttleConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            UpdateBulletState(uid, comp);
        }
    }

    // Hullrot - Auto Anchor
    private void CheckAutoAnchor(float frameTime)
    {
        var query = EntityQueryEnumerator<ShuttleConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            comp.LastKnownGrid = Transform(uid).GridUid;

            if (comp.AutoAnchorDelay <= 0f)
            {
                comp.UnpowerAccumulated = 0f;
                continue;
            }

            if (this.IsPowered(uid, EntityManager))
            {
                comp.UnpowerAccumulated = 0f;
                continue;
            }

            comp.UnpowerAccumulated += frameTime;
            if (comp.UnpowerAccumulated >= comp.AutoAnchorDelay)
            {
                comp.UnpowerAccumulated = 0f;
                TryAutoAnchorFromConsole(uid, comp);
            }
        }
    }

    private void TryAutoAnchorFromConsole(EntityUid uid, ShuttleConsoleComponent component)
    {
        var grid = Transform(uid).GridUid ?? component.LastKnownGrid;
        if (grid is not { } gridUid)
        {
            return;
        }

        if (!TryComp<ShuttleComponent>(grid, out var shuttle))
        {
            return;
        }

        var ev = new SetInertiaDampeningRequest
        {
            ShuttleEntityUid = GetNetEntity(grid),
            Mode = InertiaDampeningMode.Anchored,
        };
        RaiseLocalEvent(uid, ev);
    }

    /// <summary>
    /// Stop piloting if the window is closed.
    /// </summary>
    private void OnConsoleUIClose(EntityUid uid, ShuttleConsoleComponent component, BoundUIClosedEvent args)
    {
        if ((ShuttleConsoleUiKey) args.UiKey != ShuttleConsoleUiKey.Key)
        {
            return;
        }

        RemovePilot(args.Actor);

        // Crescent: the crew tab unlocks the id slot, and closing the window never told us to lock it
        // back. Left open, the console swallows the next swiped id instead of locking.
        if (component.accesState != ShuttleConsoleAccesState.NotDynamic
            && !_ui.IsUiOpen(uid, ShuttleConsoleUiKey.Key))
        {
            _itemSlotsSystem.TryEject(uid, component.targetIdSlot, null, out _);
            _itemSlotsSystem.SetLock(uid, SharedShuttleConsoleComponent.IdSlotName, true);
        }
    }

    private void OnConsoleUIOpened(EntityUid uid, ShuttleConsoleComponent component, BoundUIOpenedEvent args)
    {
        UpdateState(uid, component);
    }


    private void OnConsoleUIOpenAttempt(EntityUid uid, ShuttleConsoleComponent component,
        ActivatableUIOpenAttemptEvent args)
    {

        if(component.accesState == ShuttleConsoleAccesState.NoAcces)
        {
            args.Cancel();
            _popup.PopupEntity("Swipe ID to authorize yourself.", uid, args.User, PopupType.LargeCaution);
            return;
        }
        var uis = _ui.GetActorUis(args.User);

        var tgridUid = Transform(uid).GridUid;
        if (tgridUid is null)
            return;
        var gridUid = tgridUid.Value;

        if (!TryComp<IFFComponent>(gridUid, out var _))
        {
            _shuttle.SetIFFColor(gridUid, new Color
            {
                R = 10,
                G = 50,
                B = 100,
                A = 100
            });
            _shuttle.AddIFFFlag(gridUid, IFFFlags.IsPlayerShuttle);
            EnsureComp<ShuttleDeedComponent>(gridUid, out var deedComp);
            List<string> possibleNames = new List<string> {"NX", "KXZ", "ALP", "BET", "TAN", "MV"};
            _random.Shuffle(possibleNames);
            _meta.SetEntityName(gridUid, $"Shuttle {possibleNames[0]}-{(int)_random.NextFloat(100f,999f)}");
            deedComp.ShuttleUid = gridUid;
            deedComp.ShuttleName = MetaData(gridUid).EntityName;
            DirtyEntity(gridUid);


        }

        if (!TryPilot(args.User, uid))
            args.Cancel();
        UpdateState(uid, component);
    }

    private void OnConsoleAnchorChange(EntityUid uid, ShuttleConsoleComponent component,
        ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
        {


            if (TryComp<DynamicCodeHolderComponent>(args.Transform.GridUid, out var gridCodes))
            {
                component.accesState = ShuttleConsoleAccesState.NoAcces;
                AdoptGridKeyNames(component, gridCodes);
                _itemSlotsSystem.TryEject(uid, component.targetIdSlot, null, out var item);
                _itemSlotsSystem.SetLock(uid, component.targetIdSlot, true);
            }
            else
            {
                component.accesState = ShuttleConsoleAccesState.NotDynamic;
            }

        }

        UpdateState(uid, component);
    }

    private void OnConsolePowerChange(EntityUid uid, ShuttleConsoleComponent component, ref PowerChangedEvent args)
    {
        UpdateState(uid, component);
    }

    private bool TryPilot(EntityUid user, EntityUid uid)
    {
        if (!_tags.HasTag(user, "CanPilot") ||
            !TryComp<ShuttleConsoleComponent>(uid, out var component) ||
            !this.IsPowered(uid, EntityManager) ||
            !Transform(uid).Anchored ||
            !_blocker.CanInteract(user, uid))
        {
            return false;
        }

        var pilotComponent = EnsureComp<PilotComponent>(user);
        var console = pilotComponent.Console;

        if (console != null)
        {
            RemovePilot(user, pilotComponent);

            // This feels backwards; is this intended to be a toggle?
            if (console == uid)
                return false;
        }

        AddPilot(uid, user, component);
        return true;
    }

    private void OnGetState(EntityUid uid, PilotComponent component, ref ComponentGetState args)
    {
        args.State = new PilotComponentState(GetNetEntity(component.Console));
    }

    /// <summary>
    /// Returns the position and angle of all dockingcomponents.
    /// </summary>
    public Dictionary<NetEntity, List<DockingPortState>> GetAllDocks()
    {
        // TODO: NEED TO MAKE SURE THIS UPDATES ON ANCHORING CHANGES!
        var result = new Dictionary<NetEntity, List<DockingPortState>>();
        var query = AllEntityQuery<DockingComponent, TransformComponent, MetaDataComponent>();

        while (query.MoveNext(out var uid, out var comp, out var xform, out var metadata))
        {
            if (xform.ParentUid != xform.GridUid)
                continue;

            var gridDocks = result.GetOrNew(GetNetEntity(xform.GridUid.Value));

            var state = new DockingPortState()
            {
                Name = metadata.EntityName,
                Coordinates = GetNetCoordinates(xform.Coordinates),
                Angle = xform.LocalRotation,
                Entity = GetNetEntity(uid),
                GridDockedWith =
                    _xformQuery.TryGetComponent(comp.DockedWith, out var otherDockXform) ?
                    GetNetEntity(otherDockXform.GridUid) :
                    null,
            };

            gridDocks.Add(state);
        }

        return result;
    }

    public CrewInterfaceState GetCrewState(EntityUid consoleUid, ShuttleConsoleComponent shuttleConsole)
    {
        var State = new CrewInterfaceState("", null);
        if (_itemSlotsSystem.TryGetSlot(consoleUid, SharedShuttleConsoleComponent.IdSlotName, out var itemSlot) &&
            itemSlot.Item is not null)
        {
            if (!TryComp<IdCardComponent>(itemSlot.Item.Value, out var comp))
                return State;
            if (!TryComp<DynamicCodeHolderComponent>(itemSlot.Item.Value, out var dynamicAcces))
                return State;
            var gridId = Transform(consoleUid).GridUid;
            if (gridId is null)
                return State;
            if (!TryComp<DynamicCodeHolderComponent>(gridId, out var gridDynAcces))
                return State;
            if(comp.FullName is not null)
                State.IdName = comp.FullName;
            State.IdCodes = gridDynAcces.mappedCodes.Keys.ToHashSet();
            State.Pressed = new HashSet<string>();
            foreach (var key in State.IdCodes)
            {
                if (!_codes.hasAllKeys(gridDynAcces.mappedCodes[key], dynamicAcces))
                    continue;
                State.Pressed.Add(key);
            }
            State.hasId = true;
        }

        return State;

    }


    public IFFInterfaceState GetIFFState(EntityUid consoleUid, Dictionary<NetEntity, List<TurretState>>? turrets)
    {
        var projectiles = GetProjectilesInRange(consoleUid);
        turrets ??= GetAllTurrets(consoleUid);
        return new IFFInterfaceState(projectiles, turrets)
        {
            TakingFire = KsIsGridTakingFire(Transform(consoleUid).GridUid), // KS14
        };
    }

    /// <summary>
    ///     KS14: stamps the damaged entity's grid. Healing and no-op changes are ignored, so
    ///         a medbay patching someone up never reads as the ship being shot at.
    /// </summary>
    private void OnAnyDamageChanged(EntityUid uid, DamageableComponent component, DamageChangedEvent args)
    {
        if (args.DamageDelta is not { } delta || delta.GetTotal() <= 0)
            return;

        if (_xformQuery.TryGetComponent(uid, out var xform) && xform.GridUid is { } grid)
            _lastGridDamage[grid] = _timing.CurTime;
    }

    /// <summary>KS14: a deleted grid can never be hit again, so drop its stamp.</summary>
    private void OnGridRemovedForDamage(GridRemovalEvent args)
    {
        _lastGridDamage.Remove(args.EntityUid);
    }

    /// <summary>KS14: whether this grid was hit inside the warning's hold window.</summary>
    private bool KsIsGridTakingFire(EntityUid? grid)
    {
        return grid is { } gridUid
            && _lastGridDamage.TryGetValue(gridUid, out var last)
            && _timing.CurTime - last < TakingFireHold;
    }

    public List<ProjectileState> GetProjectilesInRange(EntityUid consoleUid)
    {
        var projectiles = new List<ProjectileState>();
        var consoleXform = Transform(consoleUid);
        var consoleMap = consoleXform.MapID;
        var consolePosition = _transform.GetWorldPosition(consoleUid);
        var range = SharedRadarConsoleSystem.DefaultMaxRange;
        var rangeSq = range * range;

        var query = EntityQueryEnumerator<ProjectileIFFComponent, MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var projectileIFF, out var metadata, out var transform))
        {
            // World positions are only comparable on the same map.
            if (transform.MapID != consoleMap)
                continue;

            if (HasComp<RadarPingerComponent>(uid) && !_ui.IsUiOpen(uid, RadarConsoleUiKey.Key))
                continue;

            if ((consolePosition - _transform.GetWorldPosition(uid)).LengthSquared() > rangeSq)
            {
                continue;
            }

            var projectile = new ProjectileState
            {
                Coordinates = GetNetCoordinates(_transform.GetMoverCoordinates(uid, transform)),
                VisualTypeIndex = (int)projectileIFF.VisualType,
                Color = projectileIFF.Color,
                Scale = projectileIFF.Scale // Add this line to support scale
            };
            projectiles.Add(projectile);
        }

        return projectiles;
    }

    public Dictionary<NetEntity, List<TurretState>> GetAllTurrets(EntityUid consoleUid)
    {
        List<EntityUid>? controlledUids = null;
        if (TryComp<TargetingConsoleComponent>(consoleUid, out var targCon))
            controlledUids = targCon.CurrentGroup;

        var turrets = new Dictionary<NetEntity, List<TurretState>>();
        var query = EntityQueryEnumerator<TurretIFFComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var turretIFF, out var transform))
        {
            if (transform?.GridUid == null)
                continue;

            var netEntity = GetNetEntity(transform.GridUid.Value);
            var turret = new TurretState
            {
                IsControlled = controlledUids == null || controlledUids.Contains(uid),
                Coordinates = GetNetCoordinates(transform.Coordinates)
            };

            if (turrets.TryGetValue(netEntity, out var gridTurrets))
            {
                gridTurrets.Add(turret);
            }
            else
            {
                gridTurrets = new List<TurretState> { turret };
                turrets.Add(netEntity, gridTurrets);
            }
        }

        return turrets;
    }

    public void UpdateState(EntityUid consoleUid, ShuttleConsoleComponent console)
    {
        EntityUid? entity = consoleUid;

        var getShuttleEv = new ConsoleShuttleEvent
        {
            Console = entity,
        };

        RaiseLocalEvent(entity.Value, ref getShuttleEv);
        entity = getShuttleEv.Console;

        TryComp<TransformComponent>(entity, out var consoleXform);
        var shuttleGridUid = consoleXform?.GridUid;

        NavInterfaceState navState;
        ShuttleMapInterfaceState mapState;
        var dockState = GetDockState();
        var iffState = GetIFFState(consoleUid, null);
        var crewState = GetCrewState(consoleUid, console);

        if (shuttleGridUid != null && entity != null)
        {
            navState = GetNavState(entity.Value, dockState.Docks);
            mapState = GetMapState(shuttleGridUid.Value);
        }
        else
        {
            navState = new NavInterfaceState(0f, null, 0, new Dictionary<NetEntity, List<DockingPortState>>(), InertiaDampeningMode.Dampened, GetNetEntity(consoleUid));
            mapState = new ShuttleMapInterfaceState(
                FTLState.Invalid,
                default,
                new List<ShuttleBeaconObject>(),
                new List<ShuttleExclusionObject>());
        }

        navState.WeaponTargetingMode = EntityManager.System<ShipWeaponTargetingSystem>().GetMode(consoleUid);

        if (_ui.HasUi(consoleUid, ShuttleConsoleUiKey.Key))
        {
            var state = new ShuttleBoundUserInterfaceState(navState, mapState, dockState, crewState)
            {
                canAccesCrew = (console.accesState == ShuttleConsoleAccesState.CaptainAcces),
                IFFState = iffState,
            };
            state.DirtyFlags = StateDirtyFlags.All;
            // send for 5 ticks to make sure it actually reaches client... (yes i dont know why the first one or two get always lost , shit-UI networking i guess)
            state.sendingDock = 5;
            _ui.SetUiState(consoleUid, ShuttleConsoleUiKey.Key, state);
            console.LastUpdatedState = state;
        }
    }

    private void UpdateBulletState(EntityUid consoleUid, ShuttleConsoleComponent console)
    {
        if (!_ui.IsUiOpen(consoleUid, ShuttleConsoleUiKey.Key))
            return;
        if (console.LastUpdatedState is null)
            return;
        var newState = new ShuttleBoundUserInterfaceState(console.LastUpdatedState);
        newState.IFFState = GetIFFState(consoleUid, console.LastUpdatedState?.IFFState?.Turrets);
        newState.DirtyFlags = StateDirtyFlags.IFF;
        if (console.LastUpdatedState!.sendingDock > 0)
        {
            newState.DirtyFlags = StateDirtyFlags.All;
            console.LastUpdatedState!.sendingDock--;
        }
        else
        { // dont send over the network.
            newState.CrewState = null;
            newState.NavState = null;
            newState.DockState = null;
            newState.MapState = null;
        }
        _ui.SetUiState(consoleUid, ShuttleConsoleUiKey.Key, newState);

    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var toRemove = new ValueList<(EntityUid, PilotComponent)>();
        var query = EntityQueryEnumerator<PilotComponent>();

        _accumulatedFrameTime += frameTime; // Hullrot - console ratelimiting
        float targetTime = _uiTps > 0 ? 1.0f / _uiTps : 0.1f;

        if (_accumulatedFrameTime >= targetTime)
        {
            RefreshBulletStateForConsoles();
            _accumulatedFrameTime -= targetTime;
            
            // no lag spikes
            if (_accumulatedFrameTime > targetTime) 
                _accumulatedFrameTime = 0;
        }

        CheckAutoAnchor(frameTime); // Hullrot - Auto Anchor

        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Console == null)
                continue;

            if (!_blocker.CanInteract(uid, comp.Console))
            {
                toRemove.Add((uid, comp));
            }
        }

        foreach (var (uid, comp) in toRemove)
        {
            RemovePilot(uid, comp);
        }
    }

    protected override void HandlePilotShutdown(EntityUid uid, PilotComponent component, ComponentShutdown args)
    {
        base.HandlePilotShutdown(uid, component, args);
        RemovePilot(uid, component);
    }

    private void OnConsoleShutdown(EntityUid uid, ShuttleConsoleComponent component, ref ComponentShutdown args)
    {
        TryAutoAnchorFromConsole(uid, component); // Hullrot - Auto Anchor
        ClearPilots(component);
    }

    public void AddPilot(EntityUid uid, EntityUid entity, ShuttleConsoleComponent component)
    {
        if (!EntityManager.TryGetComponent(entity, out PilotComponent? pilotComponent)
        || component.SubscribedPilots.Contains(entity))
        {
            return;
        }

        _eyeSystem.SetZoom(entity, component.Zoom, ignoreLimits: true);

        component.SubscribedPilots.Add(entity);

        _alertsSystem.ShowAlert(entity, pilotComponent.PilotingAlert);

        pilotComponent.Console = uid;
        ActionBlockerSystem.UpdateCanMove(entity);
        pilotComponent.Position = EntityManager.GetComponent<TransformComponent>(entity).Coordinates;
        Dirty(entity, pilotComponent);
    }

    public void RemovePilot(EntityUid pilotUid, PilotComponent pilotComponent)
    {
        var console = pilotComponent.Console;

        if (!TryComp<ShuttleConsoleComponent>(console, out var helm))
            return;

        pilotComponent.Console = null;
        pilotComponent.Position = null;
        _eyeSystem.ResetZoom(pilotUid);

        if (!helm.SubscribedPilots.Remove(pilotUid))
            return;

        _alertsSystem.ClearAlert(pilotUid, pilotComponent.PilotingAlert);

        _popup.PopupEntity(Loc.GetString("shuttle-pilot-end"), pilotUid, pilotUid);

        if (pilotComponent.LifeStage < ComponentLifeStage.Stopping)
            EntityManager.RemoveComponent<PilotComponent>(pilotUid);
    }

    public void RemovePilot(EntityUid entity)
    {
        if (!EntityManager.TryGetComponent(entity, out PilotComponent? pilotComponent))
            return;

        RemovePilot(entity, pilotComponent);
    }

    public void ClearPilots(ShuttleConsoleComponent component)
    {
        var query = GetEntityQuery<PilotComponent>();
        while (component.SubscribedPilots.TryGetValue(0, out var pilot))
        {
            if (query.TryGetComponent(pilot, out var pilotComponent))
                RemovePilot(pilot, pilotComponent);
        }
    }

    /// <summary>
    /// Specific for a particular shuttle.
    /// </summary>
    public NavInterfaceState GetNavState(Entity<RadarConsoleComponent?, TransformComponent?> entity, Dictionary<NetEntity, List<DockingPortState>> docks)
    {
        if (!Resolve(entity, ref entity.Comp1, ref entity.Comp2))
            return new NavInterfaceState(SharedRadarConsoleSystem.DefaultMaxRange, null, 0, docks, Shared._NF.Shuttles.Events.InertiaDampeningMode.Dampened, GetNetEntity(entity.Owner)); // Frontier: add inertia dampening

        var state = GetNavState(
            entity,
            docks,
            entity.Comp2.Coordinates,
            entity.Comp2.LocalRotation.Theta);
        state.Target = entity.Comp1.Target;
        state.TargetEntity = GetNetEntity(entity.Comp1.TargetEntity);
        state.HideTarget = entity.Comp1.HideTarget;
        return state;
    }

    public NavInterfaceState GetNavState(
        Entity<RadarConsoleComponent?, TransformComponent?> entity,
        Dictionary<NetEntity, List<DockingPortState>> docks,
        EntityCoordinates coordinates,
        double angle)
    {
        if (!Resolve(entity, ref entity.Comp1, ref entity.Comp2))
            return new NavInterfaceState(SharedRadarConsoleSystem.DefaultMaxRange, GetNetCoordinates(coordinates), angle, docks, InertiaDampeningMode.Dampened, GetNetEntity(entity.Owner)); // Frontier: add inertial dampening

        var state = new NavInterfaceState(entity.Comp1.MaxRange, GetNetCoordinates(coordinates), angle, docks, _shuttle.NfGetInertiaDampeningMode(entity), GetNetEntity(entity.Owner))
        {
            AlignToWorld = entity.Comp1.KeepWorldAligned,
            WeaponTargetingMode = EntityManager.System<ShipWeaponTargetingSystem>().GetMode(entity.Owner),
        }; // Frontier: inertia dampening
        state.Target = entity.Comp1.Target;
        state.TargetEntity = GetNetEntity(entity.Comp1.TargetEntity);
        state.HideTarget = entity.Comp1.HideTarget;
        return state;
    }

    /// <summary>
    /// Global for all shuttles.
    /// </summary>
    /// <returns></returns>
    public DockingInterfaceState GetDockState()
    {
        var docks = GetAllDocks();
        return new DockingInterfaceState(docks);
    }

    /// <summary>
    /// Specific to a particular shuttle.
    /// </summary>
    public ShuttleMapInterfaceState GetMapState(Entity<FTLComponent?> shuttle)
    {
        FTLState ftlState = FTLState.Available;
        StartEndTime stateDuration = default;

        if (Resolve(shuttle, ref shuttle.Comp, false) && shuttle.Comp.LifeStage < ComponentLifeStage.Stopped)
        {
            ftlState = shuttle.Comp.State;
            stateDuration = _shuttle.GetStateTime(shuttle.Comp);
        }

        List<ShuttleBeaconObject>? beacons = null;
        List<ShuttleExclusionObject>? exclusions = null;
        GetBeacons(ref beacons);
        GetExclusions(ref exclusions);

        return new ShuttleMapInterfaceState(
            ftlState,
            stateDuration,
            beacons ?? new List<ShuttleBeaconObject>(),
            exclusions ?? new List<ShuttleExclusionObject>());
    }
}
