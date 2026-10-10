using Content.Server._Crescent.Procedural.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent.Shuttles;
using Content.Shared._Crescent.Shuttles.Components;
using Content.Shared._Crescent.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Timing;
using Content.Shared.Whitelist;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server._Crescent.Shuttles.Systems;

public sealed class DockingConsoleSystem : SharedDockingConsoleSystem
{
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _mapMan = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;

    private readonly ResPath _miningShuttlePath = new("/Maps/_Crescent/Lavaland/mining.yml");

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DockingConsoleComponent, MapInitEvent>(OnMapInit);

        SubscribeLocalEvent<DockEvent>(OnDock);
        SubscribeLocalEvent<UndockEvent>(OnUndock);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFTLCompleted);

        Subs.BuiEvents<DockingConsoleComponent>(
            DockingConsoleUiKey.Key,
            subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<DockingConsoleFTLMessage>(OnFTL);
            subs.Event<DockingConsoleShuttleCheckMessage>(OnCallShuttle);
        });
    }

    private void OnMapInit(Entity<DockingConsoleComponent> ent, ref MapInitEvent args)
    {
        UpdateShuttle(ent);
        UpdateUI(ent);
        Dirty(ent);
    }

    private void OnDock(DockEvent args) =>
        UpdateConsoles(args.GridAUid, args.GridBUid);

    private void OnFTLCompleted(ref FTLCompletedEvent args)
    {
        // Update the state after the cooldown. Shitcode because
        // no events are raised on FTL cooldown completion
        var ent = args.Entity;
        if (!TryComp<FTLComponent>(ent, out var ftl))
            return; // how?

        Timer.Spawn(ftl.StateTime.Length + TimeSpan.FromSeconds(1), () => UpdateConsolesUsing(ent));
        Dirty(ent, ftl);
    }

    private void OnUndock(UndockEvent args) =>
        UpdateConsoles(args.GridAUid, args.GridBUid);

    private void OnOpened(Entity<DockingConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (TerminatingOrDeleted(ent.Comp.Shuttle))
            UpdateShuttle(ent);

        UpdateUI(ent);
    }

    private void UpdateConsoles(EntityUid gridA, EntityUid gridB)
    {
        UpdateConsolesUsing(gridA);
        UpdateConsolesUsing(gridB);
    }

    /// <summary>
    /// Update the UI of every console that is using a certain shuttle.
    /// </summary>
    public void UpdateConsolesUsing(EntityUid shuttle)
    {
        if (!HasComp<DockingShuttleComponent>(shuttle))
            return;

        var query = EntityQueryEnumerator<DockingConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
            if (comp.Shuttle == shuttle)
                UpdateUI((uid, comp));
    }

    public void UpdateUI(Entity<DockingConsoleComponent> ent)
    {
        if (ent.Comp.Shuttle is not {} shuttle)
            return;

        var ftlState = FTLState.Available;
        StartEndTime ftlTime = default;
        List<DockingDestination> destinations = new();

        if (TryComp<FTLComponent>(shuttle, out var ftl))
        {
            ftlState = ftl.State;
            ftlTime = _shuttle.GetStateTime(ftl);
        }

        if (TryComp<DockingShuttleComponent>(shuttle, out var docking))
            destinations = docking.Destinations;

        var state = new DockingConsoleState(ftlState, ftlTime, destinations);
        _ui.SetUiState(ent.Owner, DockingConsoleUiKey.Key, state);
    }

    private void OnFTL(Entity<DockingConsoleComponent> ent, ref DockingConsoleFTLMessage args)
    {
        if (ent.Comp.Shuttle is not {} shuttle || !TryComp<DockingShuttleComponent>(shuttle, out var docking))
            return;

        // Bounds-checked against the list actually being indexed: Destinations and LocationUID are
        // appended in lockstep, but the index comes from the client, so guard the real one.
        if (args.Index < 0 || args.Index >= docking.LocationUID.Count)
            return;

        var grid = docking.LocationUID[args.Index];

        // can't FTL if your already at the grid
        if (docking.currentlocation == grid.Id)
            return;

        RaiseLocalEvent(shuttle, new ShuttleLocationChangeEvent(grid.Id));

        Log.Debug($"{ToPrettyString(args.Actor):user} is FTL-docking {ToPrettyString(shuttle):shuttle} to {ToPrettyString(grid):grid}");

        // Set new current location and FTL!
        _shuttle.FTLToDock(shuttle, Comp<ShuttleComponent>(shuttle), grid, priorityTag: docking.DockTag);
    }

    /// <summary>
    /// Load a new mining shuttle if it still doesn't exist
    /// </summary>
    private void OnCallShuttle(Entity<DockingConsoleComponent> ent, ref DockingConsoleShuttleCheckMessage args)
    {
        if (ent.Comp.Shuttle != null || UpdateShuttle(ent) || HasComp<DockingShuttleComponent>(ent.Comp.Shuttle))
            return;

        // Find the target. Checked before loading anything so a console off-grid does not leak a map per click.
        var targetMap = Transform(ent).MapID;
        if (Transform(ent).GridUid is not {} grid || Transform(ent).MapUid is not {} targetUid)
            return;

        _mapSystem.CreateMap(out var dummyMap);
        _mapLoader.TryLoadGrid(dummyMap, _miningShuttlePath, out _);

        // Find the mining shuttle
        var shuttle = _mapMan.GetAllGrids(dummyMap).FirstOrNull(x => HasComp<DockingShuttleComponent>(x));
        if (!TryComp<DockingShuttleComponent>(shuttle, out var docking))
        {
            Log.Error("Failed to call Mining shuttle since it failed to load.");
            _mapSystem.DeleteMap(dummyMap);
            return;
        }

        // Add the station of the calling console. The freshly loaded shuttle is the one being called; the
        // console only learns about it through the shuttle's MapInit, so do not depend on that here.
        if (docking.Station == null)
            RaiseLocalEvent(shuttle.Value, new ShuttleAddStationEvent(targetUid, targetMap, grid));

        // Finally FTL
        _shuttle.FTLToDock(shuttle.Value, Comp<ShuttleComponent>(shuttle.Value), grid, priorityTag: docking.DockTag);
        UpdateShuttle(ent);
        UpdateUI(ent);
        Dirty(ent);

        // shitcode because funny
        Timer.Spawn(TimeSpan.FromSeconds(15), () => _mapSystem.DeleteMap(dummyMap));
    }

    /// <summary>
    /// Tries to connect to some mining shuttle on init.
    /// Returns true on success.
    /// </summary>
    public bool UpdateShuttle(Entity<DockingConsoleComponent> ent)
    {
        var hadShuttle = ent.Comp.HasShuttle;

        ent.Comp.Shuttle = FindShuttle(ent.Comp.ShuttleWhitelist);
        ent.Comp.HasShuttle = ent.Comp.Shuttle != null;

        if (ent.Comp.HasShuttle != hadShuttle)
            Dirty(ent);

        return ent.Comp.HasShuttle;
    }

    private EntityUid? FindShuttle(EntityWhitelist whitelist)
    {
        var query = EntityQueryEnumerator<DockingShuttleComponent>();
        while (query.MoveNext(out var uid, out _))
            if (_whitelist.IsValid(whitelist, uid))
                return uid;

        return null;
    }
}
