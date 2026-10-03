using System.Linq;
using Content.Server._Crescent.HullrotSelfDeleteTimer;
using Content.Server._Crescent.Shuttles.Components;
using Content.Server.Administration.Logs;
using Content.Server.Body.Systems;
using Content.Server.Buckle.Systems;
using Content.Server.Doors.Systems;
using Content.Server.Parallax;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Stunnable;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.GameTicking;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Throwing;
using Content.Shared._Crescent.Shuttles.Components;
using Content.Shared._Crescent.World;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Shuttles.Systems;

[UsedImplicitly]
public sealed partial class ShuttleSystem : SharedShuttleSystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly BiomeSystem _biomes = default!;
    [Dependency] private readonly BodySystem _bobby = default!;
    [Dependency] private readonly BuckleSystem _buckle = default!;
    [Dependency] private readonly DamageableSystem _damageSys = default!;
    [Dependency] private readonly DockingSystem _dockSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly FixtureSystem _fixtures = default!;
    [Dependency] private readonly InventorySystem _inventorySystem = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly PvsOverrideSystem _pvs = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedSalvageSystem _salvage = default!;
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StunSystem _stuns = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly ThrusterSystem _thruster = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IAdminLogManager _logger = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    private EntityQuery<BuckleComponent> _buckleQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    public const float TileMassMultiplier = 0.5f;
    public float accumulator = 0f;

    //used for logging, don't touch this
    private ISawmill _sawmill = default!;


    public override void Initialize()
    {
        base.Initialize();

        _buckleQuery = GetEntityQuery<BuckleComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        InitializeFTL();
        InitializeGridFills();
        InitializeIFF();
        InitializeImpact();

        SubscribeLocalEvent<ShuttleComponent, ComponentStartup>(OnShuttleStartup);
        SubscribeLocalEvent<ShuttleComponent, ComponentShutdown>(OnShuttleShutdown);

        SubscribeLocalEvent<GridInitializeEvent>(OnGridInit);
        SubscribeLocalEvent<FixturesComponent, GridFixtureChangeEvent>(OnGridFixtureChange);

        NfInitialize(); // Frontier Initialization for the ShuttleSystem

        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("shuttlesystem.server");

    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Crescent: grid impacts are resolved here rather than inside StartCollideEvent, see ShuttleSystem.Impact.
        UpdateImpact();

        accumulator += frameTime;
        if (accumulator >= 1f)
        {
            accumulator = 0f;
            var query = EntityQueryEnumerator<IFFConsoleComponent>();
            while (query.MoveNext(out var uid, out var comp))
            {
                // Crescent: an unlimited cloak never builds heat, cloaked or not.
                if (comp.UnlimitedCloak)
                {
                    if (comp.CurrentHeat != 0f)
                    {
                        comp.CurrentHeat = 0f;
                        UpdateIFFInterface(uid, comp);
                    }
                    continue;
                }

                if (!comp.active || comp.dissipateAlways)
                {
                    comp.CurrentHeat = float.Clamp(comp.CurrentHeat - comp.HeatDissipation, 0f, comp.HeatCapacity);
                    UpdateIFFInterface(uid, comp);
                    continue;
                }
                // Hullrot edit: Making patch for Custom cloak proportional to speed. If has component, will calculate right, if not, ignore.
                var grid = Transform(uid).GridUid;
                if (grid != null && TryComp<IFFCloakSpeedComponent>(uid, out var speedCloakComp) && EntityManager.TryGetComponent(grid, out PhysicsComponent? physicsComponent))
                {
                    comp.CurrentHeat = float.Clamp(comp.CurrentHeat + comp.HeatGeneration * speedCloakComp.SpeedMultiplier * physicsComponent.LinearVelocity.Length(), 0f, comp.HeatCapacity);
                }
                else
                {
                    comp.CurrentHeat = float.Clamp(comp.CurrentHeat + comp.HeatGeneration, 0f, comp.HeatCapacity);
                }

                UpdateIFFInterface(uid, comp);
                if (comp.CurrentHeat != comp.HeatCapacity)
                    continue;
                if (grid is null)
                    continue;
                RemoveIFFFlag(grid.Value, IFFFlags.Hide);
                comp.active = false;

            }
        }

        UpdateMassCloakFields();
        UpdateMassValues();

        UpdateHyperspace();
    }

    private void OnGridFixtureChange(EntityUid uid, FixturesComponent manager, GridFixtureChangeEvent args)
    {
        foreach (var fixture in args.NewFixtures)
        {
            _physics.SetDensity(uid, fixture.Key, fixture.Value, TileMassMultiplier, false, manager);
            _fixtures.SetRestitution(uid, fixture.Key, fixture.Value, 0.1f, false, manager);
        }
    }

    private void UpdateMassCloakFields()
    {
        var activeFields = new List<(EntityUid ConsoleUid, TransformComponent Xform, MassCloakConsoleComponent Comp)>();
        var consoleQuery = EntityQueryEnumerator<MassCloakConsoleComponent, TransformComponent>();

        while (consoleQuery.MoveNext(out var consoleUid, out var consoleComp, out var consoleXform))
        {
            consoleComp.CloakedGrids.Clear();

            if (!consoleComp.MassCloakEnabled || consoleXform.GridUid is null)
                continue;

            // Mothership-bound fields are powered by, but do not control, the mothership's own IFF cloak.
            if (consoleComp.RequiresMothershipCloak &&
                (!TryComp(consoleXform.GridUid, out IFFComponent? mothershipIff) ||
                 (mothershipIff.Flags & IFFFlags.Hide) == 0))
                continue;

            activeFields.Add((consoleUid, consoleXform, consoleComp));
        }

        var toCloak = new Dictionary<EntityUid, EntityUid>(); // gridUid -> consoleUid
        if (activeFields.Count > 0)
        {
            var gridsQuery = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (gridsQuery.MoveNext(out var gridUid, out _, out var gridXform))
            {
                foreach (var (consoleUid, fieldXform, fieldComp) in activeFields)
                {
                    if (gridXform.MapID != fieldXform.MapID)
                        continue;

                    // A mothership-bound field only cloaks surrounding grids; its host grid retains its own cloak state.
                    if (!fieldComp.CloakMothership && gridUid == fieldXform.GridUid)
                        continue;

                    if (fieldComp.IgnoreAsteroids && HasComp<MinedAsteroidDecayComponent>(gridUid))
                        continue;

                    var dist = (gridXform.WorldPosition - fieldXform.WorldPosition).Length();
                    if (dist <= fieldComp.MassCloakRange)
                    {
                        // Track this cloak
                        if (!toCloak.ContainsKey(gridUid))
                        {
                            toCloak[gridUid] = consoleUid;
                            fieldComp.CloakedGrids.Add(gridUid);
                        }
                        break;
                    }
                }
            }
        }

        var existingCloaks = EntityQueryEnumerator<MassCloakComponent>();
        var existList = new List<EntityUid>();
        while (existingCloaks.MoveNext(out var gridUid, out _))
        {
            existList.Add(gridUid);
        }

        // Remove mass cloaking from grids that are no longer in range
        foreach (var gridUid in existList)
        {
            if (!toCloak.ContainsKey(gridUid))
            {
                var clearHideFlag = TryComp(gridUid, out MassCloakedByComponent? cloakedBy) &&
                                    cloakedBy.HideFlagSetByMassCloak;

                RemComp<MassCloakComponent>(gridUid);
                RemComp<MassCloakedByComponent>(gridUid);

                if (clearHideFlag && TryComp(gridUid, out IFFComponent? iff))
                {
                    if ((iff.Flags & IFFFlags.Hide) != 0)
                    {
                        iff.Flags &= ~IFFFlags.Hide;
                        Dirty(gridUid, iff);
                    }
                }
            }
        }

        // Add mass cloaking to grids in range
        foreach (var (gridUid, consoleUid) in toCloak)
        {
            EnsureComp<MassCloakComponent>(gridUid);

            // Track which console is cloaking this grid
            var cloakedBy = EnsureComp<MassCloakedByComponent>(gridUid);

            // Only update and mark dirty if the console UID actually changed
            if (cloakedBy.CloakingConsoleUid != consoleUid)
            {
                cloakedBy.CloakingConsoleUid = consoleUid;

                // Find and set the cloaking range from the console
                foreach (var (cuid, _, comp) in activeFields)
                {
                    if (cuid == consoleUid)
                    {
                        cloakedBy.CloakingRange = comp.MassCloakRange;
                        break;
                    }
                }
                Dirty(gridUid, cloakedBy);
            }
            else if (cloakedBy.CloakingRange != activeFields.FirstOrDefault(f => f.ConsoleUid == consoleUid).Comp.MassCloakRange)
            {
                // Only update range if it actually changed
                foreach (var (cuid, _, comp) in activeFields)
                {
                    if (cuid == consoleUid)
                    {
                        cloakedBy.CloakingRange = comp.MassCloakRange;
                        break;
                    }
                }
                Dirty(gridUid, cloakedBy);
            }

            // Also set the IFF hide flag (create IFF component if needed)
            if (!TryComp(gridUid, out IFFComponent? iff))
            {
                iff = AddComp<IFFComponent>(gridUid);
            }
            if (iff != null)
            {
                // Only mark dirty if Hide flag isn't already set
                if ((iff.Flags & IFFFlags.Hide) == 0)
                {
                    iff.Flags |= IFFFlags.Hide;
                    cloakedBy.HideFlagSetByMassCloak = true;
                    Dirty(gridUid, iff);
                }
            }
        }
    }

    private void UpdateMassValues()
    {
        var query = EntityQueryEnumerator<IFFComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out var iff, out var physics))
        {
            var mass = physics.Mass;
            if (Math.Abs(iff.Mass - mass) > 0.01f)
            {
                iff.Mass = mass;
                Dirty(uid, iff);
            }
        }
    }

    private void OnGridInit(GridInitializeEvent ev)
    {
        if (HasComp<MapComponent>(ev.EntityUid))
            return;

        //hullrot edit: handling adding lag compensation
        //_sawmill.Debug("GRID INITIALIZED! GRID ID:" + ev.EntityUid.ToString());

        // THAT DOESN'T WORK BECAUSE EITHER:
        // 1. THIS ISN'T THE RIGHT GRID ID. FROM WHAT I SAW, THIS GRID ID IS +1 FROM THE GRIDS ADDED TO THE FUCKING STATIONS!!!
        // 2. THIS RUNS BEFORE BECOMESSTATION AND IFFCOMPONENT AND NAMES GET ADDED, FUCKING EVERYTHING UP!
        // WE ADD THIS TO EVERY GRID, THEN WHEN THE TIMER TICKS DOWN, IF IT
        //if (!TryComp<BecomesStationComponent>(ev.EntityUid, out var _) || !TryComp<IFFComponent>(ev.EntityUid, out var _) || !(Name(ev.EntityUid) != "grid"))
        //_sawmill.Debug("NEW DEBRIS GRID MADE!");

        EnsureComp<SelfDeleteGridComponent>(ev.EntityUid); //default value will be 20 minutes

        //_sawmill.Debug("GRID ID " + ev.EntityUid.ToString() + " WILL DELETE ITSELF IN: " + selfdeletecomp.TimeToDelete.ToString());
        //hullrot edit end
        EntityManager.EnsureComponent<ShuttleComponent>(ev.EntityUid);
    }

    private void OnShuttleStartup(EntityUid uid, ShuttleComponent component, ComponentStartup args)
    {
        if (!EntityManager.HasComponent<MapGridComponent>(uid))
        {
            return;
        }

        if (!EntityManager.TryGetComponent(uid, out PhysicsComponent? physicsComponent))
        {
            return;
        }

        if (component.Enabled)
        {
            Enable(uid, component: physicsComponent, shuttle: component);
        }
    }

    public void Toggle(EntityUid uid, ShuttleComponent component)
    {
        if (!EntityManager.TryGetComponent(uid, out PhysicsComponent? physicsComponent))
            return;

        component.Enabled = !component.Enabled;

        if (component.Enabled)
        {
            Enable(uid, component: physicsComponent, shuttle: component);
        }
        else
        {
            Disable(uid, component: physicsComponent);
        }
    }

    public void Enable(EntityUid uid, FixturesComponent? manager = null, PhysicsComponent? component = null, ShuttleComponent? shuttle = null)
    {
        if (!Resolve(uid, ref manager, ref component, ref shuttle, false))
            return;

        _physics.SetBodyType(uid, BodyType.Dynamic, manager: manager, body: component);
        _physics.SetBodyStatus(uid, component, BodyStatus.InAir);
        _physics.SetFixedRotation(uid, false, manager: manager, body: component);
        _physics.SetLinearDamping(uid, component, shuttle.LinearDamping);
        _physics.SetAngularDamping(uid, component, shuttle.AngularDamping);
    }

    public void Disable(EntityUid uid, FixturesComponent? manager = null, PhysicsComponent? component = null)
    {
        if (!Resolve(uid, ref manager, ref component, false))
            return;

        _physics.SetBodyType(uid, BodyType.Static, manager: manager, body: component);
        _physics.SetBodyStatus(uid, component, BodyStatus.OnGround);
        _physics.SetFixedRotation(uid, true, manager: manager, body: component);
    }

    private void OnShuttleShutdown(EntityUid uid, ShuttleComponent component, ComponentShutdown args)
    {
        // None of the below is necessary for any cleanup if we're just deleting.
        if (EntityManager.GetComponent<MetaDataComponent>(uid).EntityLifeStage >= EntityLifeStage.Terminating)
            return;

        Disable(uid);
    }
}
