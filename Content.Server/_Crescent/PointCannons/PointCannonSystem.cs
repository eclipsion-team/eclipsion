using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Content.Server.Administration;
using Content.Server.Popups;
using Content.Server.Shuttles.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.PointCannons;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.UserInterface;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using System;
using Content.Server._Crescent.Hardpoint;
using Content.Server.Power.Components;
using Content.Shared._Crescent.CCvars;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Crescent.Radar;
using Robust.Shared.Timing;

namespace Content.Server.PointCannons;

public sealed class PointCannonSystem : EntitySystem
{
    [Dependency] private readonly ISharedPlayerManager _playerMan = default!;
    [Dependency] private readonly TransformSystem _formSys = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSys = default!;
    [Dependency] private readonly PopupSystem _popSys = default!;
    [Dependency] private readonly QuickDialogSystem _dialogSys = default!;
    [Dependency] private readonly GunSystem _gunSys = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConSys = default!;
    [Dependency] private readonly PvsOverrideSystem _pvsSys = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HardpointSystem _hardpoint = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!; // +NEW

    private float _accumulatedFrameTime;
    private float _uiTps;
    private bool _shipPowerDrawEnabled;

    private readonly HashSet<EntityUid> _activeConsoles = new();

    private readonly Dictionary<EntityUid, PendingRelink> _pendingRelinks = new();
    private readonly List<(EntityUid Console, bool Full)> _relinkScratch = new();
    private readonly Dictionary<EntityUid, GridCannonCacheComponent> _dirtyGridScratch = new();
    private static readonly TimeSpan RelinkDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     How long a fire order stands before it lapses on its own. Two and a half of the console's 100ms
    ///     message intervals: long enough that a dropped message does not stutter the guns, short enough that
    ///     a client which stops sending for any reason silences them almost at once.
    /// </summary>
    private static readonly TimeSpan FireOrderLifetime = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Clearance radius, in tiles, that a cannon's obstruction arcs are measured over. Public because
    ///     anything that measures a gun itself (the ship AI's arc solver) has to use the same figure, or a
    ///     console-linked gun and an AI-measured one on the same hull disagree about which lanes are blocked.
    /// </summary>
    public const int CannonCheckRange = 25;
    private HashSet<EntityUid> QueuedGrids = new();

    private readonly record struct PendingRelink(TimeSpan Due, bool Full);

    private EntityQuery<PointCannonComponent> _cannonQuery;
    private EntityQuery<TargetingConsoleComponent> _consoleQuery;
    private EntityQuery<GunComponent> _gunQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<HardpointAnchorableOnlyComponent> _anchorQuery;
    private EntityQuery<ApcPowerReceiverComponent> _powerQuery;
    private EntityQuery<HardpointFixedMountComponent> _fixedMountQuery;
    private EntityQuery<HardpointComponent> _hardpointQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<CannonFireCooldownComponent> _cooldownQuery;
    private EntityQuery<GridCannonCacheComponent> _gridCacheQuery;

    public override void Initialize()
    {
        base.Initialize();

        _shipPowerDrawEnabled = _cfg.GetCVar(CrescentCVars.ShipSystemsPowerDrawEnabled);
        Subs.CVar(_cfg, CrescentCVars.PointCannonUiTps, (float val) => { _uiTps = val; }, true);

        SubscribeLocalEvent<TargetingConsoleComponent, ActivatableUIOpenAttemptEvent>(OnConsoleOpenAttempt);
        SubscribeLocalEvent<TargetingConsoleComponent, BoundUserInterfaceMessageAttempt>(BUIValidation);
        SubscribeLocalEvent<TargetingConsoleComponent, BoundUIOpenedEvent>(OnConsoleOpened);
        SubscribeLocalEvent<TargetingConsoleComponent, BoundUIClosedEvent>(OnConsoleClosed);
        SubscribeLocalEvent<TargetingConsoleComponent, TargetingConsoleFireMessage>(OnConsoleFire);
        SubscribeLocalEvent<TargetingConsoleComponent, TargetingConsoleStopFireMessage>(OnConsoleStopFire);
        SubscribeLocalEvent<TargetingConsoleComponent, TargetingConsoleGroupChangedMessage>(OnConsoleGroupChanged);
        SubscribeLocalEvent<TargetingConsoleComponent, FireControlConsoleRefreshServerMessage>(OnRefreshServer);
        SubscribeLocalEvent<TargetingConsoleComponent, ComponentRemove>(OnConsoleDelete);
        SubscribeLocalEvent<TargetingConsoleComponent, AnchorStateChangedEvent>(OnConsoleAnchor);

        SubscribeLocalEvent<PointCannonComponent, EntityTerminatingEvent>(OnCannonDetach);
        SubscribeLocalEvent<PointCannonComponent, EntParentChangedMessage>(OnCannonDetach);
        SubscribeLocalEvent<PointCannonComponent, ReAnchorEvent>(OnCannonDetach);

        SubscribeLocalEvent<PointCannonLinkToolComponent, UseInHandEvent>(OnLinkToolHandUse);

        SubscribeLocalEvent<PointCannonComponent, AnchorStateChangedEvent>(OnCannonAnchorChanged);
        SubscribeLocalEvent<PointCannonComponent, ComponentInit>(OnCannonInit);
        SubscribeLocalEvent<PointCannonComponent, ComponentRemove>(OnCannonRemoved);

        _cannonQuery = GetEntityQuery<PointCannonComponent>();
        _consoleQuery = GetEntityQuery<TargetingConsoleComponent>();
        _gunQuery = GetEntityQuery<GunComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _anchorQuery = GetEntityQuery<HardpointAnchorableOnlyComponent>();
        _powerQuery = GetEntityQuery<ApcPowerReceiverComponent>();
        _fixedMountQuery = GetEntityQuery<HardpointFixedMountComponent>();
        _hardpointQuery = GetEntityQuery<HardpointComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _cooldownQuery = GetEntityQuery<CannonFireCooldownComponent>();
        _gridCacheQuery = GetEntityQuery<GridCannonCacheComponent>();
    }

    private void OnCannonAnchorChanged(EntityUid uid, PointCannonComponent comp, ref AnchorStateChangedEvent args)
    {
        InvalidateGridCache(uid);
    }

    private void OnCannonInit(EntityUid uid, PointCannonComponent comp, ComponentInit args)
    {
        InvalidateGridCache(uid);
    }

    private void OnCannonRemoved(EntityUid uid, PointCannonComponent comp, ComponentRemove args)
    {
        InvalidateGridCache(uid);
    }

    /// <summary>
    /// Marks a grid's cannon list stale and re-derives every active console's links from it. The
    /// automated repair slip calls this once a hull is back together: a gun it reinstated announced
    /// itself while it was still sitting unmounted, so the links drawn then missed it.
    /// </summary>
    public void InvalidateGridCannons(EntityUid gridUid)
    {
        if (_gridCacheQuery.TryGetComponent(gridUid, out var cache))
            cache.Dirty = true;

        QueueGridConsoleRelink(gridUid);
    }

    private void InvalidateGridCache(EntityUid cannonUid)
    {
        if (!_xformQuery.TryGetComponent(cannonUid, out var xform))
            return;
        if (xform.GridUid is not { } gridUid)
            return;
        if (_gridCacheQuery.TryGetComponent(gridUid, out var cache))
            cache.Dirty = true;

        QueueGridConsoleRelink(gridUid);
    }

    private void QueueGridConsoleRelink(EntityUid gridUid)
    {
        foreach (var console in _activeConsoles)
        {
            if (!_xformQuery.TryGetComponent(console, out var consoleXform))
                continue;
            if (consoleXform.GridUid != gridUid)
                continue;

            QueueConsoleRelink(console);
        }
    }

    private void QueueConsoleRelink(EntityUid console, bool full = false, bool immediate = false)
    {
        var due = immediate ? TimeSpan.Zero : _timing.CurTime + RelinkDebounce;

        // Never push an already-queued relink further out, only pull it forward.
        if (_pendingRelinks.TryGetValue(console, out var existing))
        {
            _pendingRelinks[console] = new PendingRelink(
                existing.Due < due ? existing.Due : due,
                existing.Full || full);
            return;
        }

        _pendingRelinks[console] = new PendingRelink(due, full);
    }

    private void ProcessPendingRelinks()
    {
        if (_pendingRelinks.Count == 0)
            return;

        var now = _timing.CurTime;
        _relinkScratch.Clear();

        foreach (var (console, pending) in _pendingRelinks)
        {
            if (now >= pending.Due)
                _relinkScratch.Add((console, pending.Full));
        }

        RefreshDirtyGridCaches();

        foreach (var (console, full) in _relinkScratch)
        {
            _pendingRelinks.Remove(console);

            if (!_consoleQuery.TryGetComponent(console, out var comp))
                continue;

            if (full)
                ProcessGridShapeChange(console, comp);
            else
                SyncConsoleLinks(console, comp);
        }
    }

    private HashSet<EntityUid> GetGridCannons(EntityUid gridUid)
    {
        var cache = EnsureComp<GridCannonCacheComponent>(gridUid);

        if (!cache.Dirty)
            return cache.CachedCannons;

        cache.CachedCannons.Clear();

        var query = AllEntityQuery<PointCannonComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                cache.CachedCannons.Add(uid);
        }

        cache.Dirty = false;
        return cache.CachedCannons;
    }

    private void RefreshDirtyGridCaches()
    {
        _dirtyGridScratch.Clear();

        foreach (var (console, _) in _relinkScratch)
        {
            if (!_xformQuery.TryGetComponent(console, out var xform) || xform.GridUid is not { } gridUid)
                continue;
            if (_dirtyGridScratch.ContainsKey(gridUid))
                continue;

            var cache = EnsureComp<GridCannonCacheComponent>(gridUid);
            if (!cache.Dirty)
                continue;

            cache.CachedCannons.Clear();
            _dirtyGridScratch[gridUid] = cache;
        }

        if (_dirtyGridScratch.Count == 0)
            return;

        var query = AllEntityQuery<PointCannonComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid is { } gridUid && _dirtyGridScratch.TryGetValue(gridUid, out var cache))
                cache.CachedCannons.Add(uid);
        }

        foreach (var cache in _dirtyGridScratch.Values)
            cache.Dirty = false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        ProcessPendingRelinks();

        // Every tick, deliberately: this is what lets a gun keep its own burst rate instead of inheriting the
        // console's message rate. It must sit above the UI throttle below, which returns early.
        ProcessSustainedFire();

        _accumulatedFrameTime += frameTime;
        float targetTime = _uiTps > 0 ? 1.0f / _uiTps : 1.0f;
        if (_accumulatedFrameTime < targetTime)
            return;
        _accumulatedFrameTime -= targetTime;
        if (_accumulatedFrameTime > targetTime)
            _accumulatedFrameTime = 0;

        List<EntityUid>? inactiveConsoles = null;
        foreach (var uid in _activeConsoles)
        {
            if (!_consoleQuery.TryGetComponent(uid, out var console))
            {
                inactiveConsoles ??= new();
                inactiveConsoles.Add(uid);
                continue;
            }
            UpdateConsoleState(uid, console);
        }

        if (inactiveConsoles != null)
        {
            foreach (var uid in inactiveConsoles)
                _activeConsoles.Remove(uid);
        }
    }

    private void ProcessGridShapeChange(EntityUid console, TargetingConsoleComponent component)
    {
        var sessions = GetUiSessions(console);
        TogglePvsOverride(component.CurrentGroup, sessions, false);

        UnlinkAllCannonsFromConsole(console, component);
        LinkAllCannonsToConsole(console, component);

        RebuildCurrentGroup(console, component, sessions);
    }

    private void SyncConsoleLinks(EntityUid console, TargetingConsoleComponent comp)
    {
        var gridUid = Transform(console).GridUid;
        if (gridUid is null)
        {
            UnlinkAllCannonsFromConsole(console, comp);
            RebuildCurrentGroup(console, comp);
            return;
        }

        var mounted = new HashSet<EntityUid>();
        foreach (var cannonUid in GetGridCannons(gridUid.Value))
        {
            if (IsCannonMounted(cannonUid))
                mounted.Add(cannonUid);
        }

        // Collect first, UnlinkConsole mutates CannonGroups as it goes.
        var stale = new List<EntityUid>();
        foreach (var (_, cannons) in comp.CannonGroups)
        {
            foreach (var cannon in cannons)
            {
                if (!mounted.Contains(cannon))
                    stale.Add(cannon);
            }
        }

        foreach (var cannon in stale)
            UnlinkConsole(cannon, console, comp);

        foreach (var cannon in mounted)
            LinkCannon(cannon, console, comp, MetaData(cannon).EntityName);

        RebuildCurrentGroup(console, comp);
    }

    private void RebuildCurrentGroup(EntityUid console, TargetingConsoleComponent comp, List<ICommonSession>? sessions = null)
    {
        sessions ??= GetUiSessions(console);

        comp.ActiveGroups.RemoveWhere(group => !comp.CannonGroups.ContainsKey(group));

        // A cannon can sit in more than one selected group, so dedupe.
        var seen = new HashSet<EntityUid>();
        var selected = new List<EntityUid>();

        foreach (var group in comp.ActiveGroups.OrderBy(g => g, StringComparer.Ordinal))
        {
            if (!comp.CannonGroups.TryGetValue(group, out var cannons))
                continue;

            foreach (var cannon in cannons)
            {
                if (seen.Add(cannon))
                    selected.Add(cannon);
            }
        }

        List<EntityUid>? dropped = null;
        foreach (var cannon in comp.CurrentGroup)
        {
            if (seen.Contains(cannon))
                continue;

            dropped ??= new List<EntityUid>();
            dropped.Add(cannon);
        }

        if (dropped != null)
            TogglePvsOverride(dropped, sessions, false);

        comp.CurrentGroup = selected;
        TogglePvsOverride(comp.CurrentGroup, sessions, true);
        comp.RegenerateCannons = true;
    }

    private bool IsCannonMounted(EntityUid cannonUid)
    {
        if (!_xformQuery.TryGetComponent(cannonUid, out var xform) || !xform.Anchored)
            return false;

        return _anchorQuery.TryGetComponent(cannonUid, out var anchorComp) && anchorComp.anchoredTo is not null;
    }

    private void OnRefreshServer(EntityUid console, TargetingConsoleComponent component, FireControlConsoleRefreshServerMessage args)
    {
        var gridUid = Transform(console).GridUid;
        if (gridUid is not null && _gridCacheQuery.TryGetComponent(gridUid.Value, out var cache))
            cache.Dirty = true;

        // A cannon is only ever measured on the tick it gets linked, so arcs describe the hull as it was when
        // the console first picked the gun up. Walls come and go - a breach opens a lane the arcs still call
        // blocked, a rebuilt section leaves one they still call clear, and a gun firing down a lane it thinks
        // is clear puts the shot through its own ship. Refresh is the one place the player can say "the hull
        // changed", so re-measure everything aboard rather than only re-deriving the links.
        if (gridUid is not null)
        {
            foreach (var cannonUid in GetGridCannons(gridUid.Value))
            {
                if (IsCannonMounted(cannonUid))
                    RefreshFiringRanges(cannonUid, range: CannonCheckRange);
            }
        }

        QueueConsoleRelink(console, full: true, immediate: true);
    }

    private void UnlinkAllCannonsFromConsole(EntityUid console, TargetingConsoleComponent comp)
    {
        var allLinks = new List<(string group, EntityUid cannon)>();

        foreach (var (group, cannons) in comp.CannonGroups)
        {
            foreach (var cannon in cannons)
            {
                allLinks.Add((group, cannon));
            }
        }

        foreach (var (group, cannon) in allLinks)
        {
            UnlinkConsole(cannon, console, comp);
        }
    }

    private void OnConsoleDelete<T>(EntityUid console, TargetingConsoleComponent comp, ref T args)
    {
        UnlinkAllCannonsFromConsole(console, comp);
        comp.CurrentGroup.Clear();
        comp.ActiveGroups.Clear();
        comp.FireOrderExpiry = TimeSpan.Zero;
        _activeConsoles.Remove(console);
        _pendingRelinks.Remove(console);
    }

    private void OnConsoleAnchor(EntityUid console, TargetingConsoleComponent comp, ref AnchorStateChangedEvent args)
    {
        if (!args.Anchored)
        {
            OnConsoleDelete(console, comp, ref args);
            return;
        }
    }

    public void LinkAllCannonsToConsole(EntityUid console, TargetingConsoleComponent comp)
    {
        var gridUid = Transform(console).GridUid;
        if (gridUid is null)
            return;

        var cachedCannons = GetGridCannons(gridUid.Value);

        foreach (var cannonUid in cachedCannons)
        {
            if (!IsCannonMounted(cannonUid))
                continue;
            LinkCannon(cannonUid, console, comp, MetaData(cannonUid).EntityName);
        }
    }

    private void OnConsoleOpenAttempt(EntityUid uid, TargetingConsoleComponent component, ActivatableUIOpenAttemptEvent args)
    {
        var uis = _uiSys.GetActorUis(args.User);
        var ourGridUid = Transform(uid).GridUid;
        if (ourGridUid is null)
        {
            args.Cancel();
            return;
        }

        foreach (var (_, key) in uis)
        {
            if (key is ShuttleConsoleUiKey)
            {
                args.Cancel();
                _popSys.PopupEntity(Loc.GetString("targeting-rejection-shuttle-console"), args.User, args.User, PopupType.LargeCaution);
                return;
            }
        }
    }

    private void BUIValidation(EntityUid uid, TargetingConsoleComponent component, BoundUserInterfaceMessageAttempt args)
    {
        var uis = _uiSys.GetActorUis(args.Actor);
        foreach (var (_, key) in uis)
        {
            if (key is ShuttleConsoleUiKey)
            {
                args.Cancel();
                return;
            }
        }
    }

    private void OnConsoleOpened(Entity<TargetingConsoleComponent> uid, ref BoundUIOpenedEvent args)
    {
        uid.Comp.RegenerateCannons = true;
        _activeConsoles.Add(uid.Owner);

        QueueConsoleRelink(uid.Owner, immediate: true);
    }

    private void OnConsoleClosed(Entity<TargetingConsoleComponent> uid, ref BoundUIClosedEvent args)
    {
        if (_playerMan.TryGetSessionByEntity(args.Actor, out var session))
            TogglePvsOverride(uid.Comp.CurrentGroup, new[] { session }, false);

        // Someone else may still have the console open, don't tear it down under them.
        if (_uiSys.IsUiOpen(uid.Owner, TargetingConsoleUiKey.Key))
            return;

        // Nobody is driving it any more, so drop the standing order rather than letting it run out its expiry.
        uid.Comp.FireOrderExpiry = TimeSpan.Zero;
        _activeConsoles.Remove(uid.Owner);
        _pendingRelinks.Remove(uid.Owner);
    }

    private void OnCannonDetach<T>(Entity<PointCannonComponent> uid, ref T args)
    {
        UnlinkCannon(uid);
    }

    private void OnLinkToolHandUse(Entity<PointCannonLinkToolComponent> uid, ref UseInHandEvent args)
    {
        if (!_playerMan.TryGetSessionByEntity(args.User, out var session))
            return;

        _dialogSys.OpenDialog(session, "Group name", "Name (case insensitive)", (string name) =>
        {
            // ToLowerInvariant, not ToLower: under a Turkish locale a typed "IFF" lowercases to a dotless i
            // and stops matching the group keys, which come from data.
            uid.Comp.GroupName = string.IsNullOrEmpty(name) ? "all" : name.ToLowerInvariant();
        });
    }

    public void LinkCannon(EntityUid cannonUid, EntityUid consoleUid, TargetingConsoleComponent console, string group)
    {
        if (!_cannonQuery.TryGetComponent(cannonUid, out var cannonComponent))
            return;
        if (!console.CannonGroups.ContainsKey(group))
            console.CannonGroups[group] = new List<EntityUid>();

        if (console.CannonGroups[group].Contains(cannonUid))
        {
            if (cannonComponent.LinkedConsoleId is null)
                cannonComponent.LinkedConsoleId = consoleUid;

            // Relinking an already-grouped cannon takes this path, so it is the only chance a gun that
            // somehow never got measured has to pick up its arcs. An empty list reads as "nothing is in the
            // way" and lets the gun fire straight through the hull, so never leave one that way.
            if (cannonComponent.ObstructedRanges.Count == 0)
                RefreshFiringRanges(cannonUid, null, null, cannonComponent, CannonCheckRange);

            return;
        }

        console.CannonGroups[group].Add(cannonUid);
        if (group != "all" && !console.CannonGroups["all"].Contains(cannonUid))
            console.CannonGroups["all"].Add(cannonUid);

        console.RegenerateCannons = true;
        cannonComponent.LinkedConsoleId = consoleUid;
        cannonComponent.LinkedConsoleIds.Add(consoleUid);

        RefreshFiringRanges(cannonUid, null, null, cannonComponent, CannonCheckRange);
    }

    public void UnlinkCannon(EntityUid cannonUid)
    {
        if (!_cannonQuery.TryGetComponent(cannonUid, out var cannonComp))
            return;

        var consoleIds = cannonComp.LinkedConsoleIds.ToList();

        foreach (var consoleUid in consoleIds)
        {
            if (!_consoleQuery.TryGetComponent(consoleUid, out var console))
                continue;

            var groups = console.CannonGroups.Keys.ToList();

            foreach (string group in groups)
            {
                if (console.CannonGroups.TryGetValue(group, out var cannons))
                {
                    cannons.Remove(cannonUid);

                    if (cannons.Count == 0 && group != "all")
                        console.CannonGroups.Remove(group);
                }
            }

            console.CurrentGroup.Remove(cannonUid);
            console.RegenerateCannons = true;
            TogglePvsOverride(new[] { cannonUid }, GetUiSessions(consoleUid), false);
        }

        cannonComp.LinkedConsoleIds.Clear();
    }

    public void UnlinkConsole(EntityUid cannonUid, EntityUid consoleUid, TargetingConsoleComponent comp)
    {
        // Cannon may already be gone; the console still needs cleaning up either way.
        if (_cannonQuery.TryGetComponent(cannonUid, out var cannonComp))
            cannonComp.LinkedConsoleIds.Remove(consoleUid);

        if (!_consoleQuery.TryGetComponent(consoleUid, out var console))
            return;

        var groups = console.CannonGroups.Keys.ToList();

        foreach (string group in groups)
        {
            if (console.CannonGroups.TryGetValue(group, out var cannons))
            {
                cannons.Remove(cannonUid);

                if (cannons.Count == 0 && group != "all")
                    console.CannonGroups.Remove(group);
            }
        }

        console.CurrentGroup.Remove(cannonUid);
        console.RegenerateCannons = true;
        TogglePvsOverride(new[] { cannonUid }, GetUiSessions(consoleUid), false);
    }

    public void UpdateConsoleState(EntityUid uid, TargetingConsoleComponent console)
    {
        NavInterfaceState navState = _shuttleConSys.GetNavState(uid, _shuttleConSys.GetAllDocks());
        IFFInterfaceState iffState = _shuttleConSys.GetIFFState(uid,
            console.RegenerateCannons ? null : console.PrevState?.IFFState.Turrets);

        List<string>? groups = console.RegenerateCannons ? console.CannonGroups.Keys.ToList() : null;

        var consoleState = new TargetingConsoleBoundUserInterfaceState(
            navState,
            iffState,
            groups,
            GetNetEntityList(console.CurrentGroup),
            console.ActiveGroups.ToList()); // KS14: lights the selected groups on the console
        console.RegenerateCannons = false;
        console.PrevState = consoleState;
        _uiSys.SetUiState(uid, TargetingConsoleUiKey.Key, consoleState);
    }

    /// <summary>
    ///     Crescent - a fire message renews the standing order rather than firing on the spot.
    /// </summary>
    /// <remarks>
    ///     Firing directly off the message capped every console-driven gun at the console's own message rate of
    ///     ten rounds a second: <c>AttemptShoot</c> measures its cadence against how long it has been since the
    ///     gun last fired, and a hundred milliseconds is longer than the interval of anything quicker than 10/s,
    ///     so it re-based the clock to now and handed back exactly one shot every time. A 16/s burst lost a third
    ///     of its rate and a 24/s one lost well over half, permanently, with no way to catch up.
    /// </remarks>
    private void OnConsoleFire(EntityUid uid, TargetingConsoleComponent console, TargetingConsoleFireMessage ev)
    {
        console.FireCoordinates = ev.Coordinates;
        console.FireOrderExpiry = _timing.CurTime + FireOrderLifetime;
    }

    private void OnConsoleStopFire(EntityUid uid, TargetingConsoleComponent console, TargetingConsoleStopFireMessage ev)
    {
        console.FireOrderExpiry = TimeSpan.Zero;
    }

    /// <summary>
    ///     Crescent - applies every console's standing fire order, once per tick.
    /// </summary>
    private void ProcessSustainedFire()
    {
        var now = _timing.CurTime;

        var query = EntityQueryEnumerator<TargetingConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
        {
            if (console.FireOrderExpiry <= now || console.CurrentGroup.Count == 0)
                continue;

            FireGroup(uid, console, console.FireCoordinates, now);
        }
    }

    private void FireGroup(EntityUid uid, TargetingConsoleComponent console, Vector2 coordinates, TimeSpan now)
    {
        for (int i = 0; i < console.CurrentGroup.Count;)
        {
            EntityUid cannonUid = console.CurrentGroup[i];
            if (Deleted(cannonUid))
            {
                console.CurrentGroup.RemoveAt(i);
                TogglePvsOverride(new[] { cannonUid }, GetUiSessions(uid), false);
                continue;
            }

            if (_cooldownQuery.TryGetComponent(cannonUid, out var cooldown))
            {
                if (now < cooldown.NextFire)
                {
                    i++;
                    continue; // Cannon is on cooldown - skip it without calling GunSystem
                }
            }

            if (TryFireCannon(cannonUid, coordinates, console: uid))
            {
                if (cooldown != null)
                    cooldown.NextFire = now + TimeSpan.FromSeconds(cooldown.FireCooldown);
            }

            i++;
        }
    }

    private void OnConsoleGroupChanged(Entity<TargetingConsoleComponent> uid, ref TargetingConsoleGroupChangedMessage args)
    {
        if (!uid.Comp.CannonGroups.ContainsKey(args.GroupName))
            return;

        var sessions = GetUiSessions(uid);

        // Drop the lot and rebuild, since groups can share cannons.
        TogglePvsOverride(uid.Comp.CurrentGroup, sessions, false);

        if (uid.Comp.ActiveGroups.Contains(args.GroupName))
        {
            if (args.GroupName == "all")
                uid.Comp.ActiveGroups.Clear();
            else
                uid.Comp.ActiveGroups.Remove(args.GroupName);
        }
        else if (args.GroupName == "all")
        {
            uid.Comp.ActiveGroups.Clear();
            uid.Comp.ActiveGroups.Add("all");
        }
        else
        {
            uid.Comp.ActiveGroups.Add(args.GroupName);
        }

        RebuildCurrentGroup(uid, uid.Comp, sessions);
    }

    public bool TryFireCannon(
        EntityUid uid,
        Vector2 pos,
        TransformComponent? form = null,
        GunComponent? gun = null,
        PointCannonComponent? cannon = null,
        EntityUid? console = null)
    {
        if (form == null && !_xformQuery.TryGetComponent(uid, out form))
            return false;
        if (gun == null && !_gunQuery.TryGetComponent(uid, out gun))
            return false;
        if (cannon == null && !_cannonQuery.TryGetComponent(uid, out cannon))
            return false;

        if (form.MapUid == null || !_gunSys.CanShoot(gun))
            return false;

        if (!CanCannonFire(uid, out var hardpoint))
            return false;

        var entPos = new EntityCoordinates(uid, new Vector2(0, -1));
        var swings = !_fixedMountQuery.HasComponent(hardpoint);

        // The bearing the shot would actually leave along: a turret swings onto the target, while a fixed mount
        // never moves and fires down the axis it is already welded to.
        var bearing = form.LocalRotation;
        Angle targetRot = default;

        if (swings)
        {
            Vector2 cannonPos = _formSys.GetWorldPosition(form);
            targetRot = Angle.FromWorldVec(pos - cannonPos);

            // Taking the parent's rotation off is exactly what SetWorldRotation does, so this is the local
            // rotation the cannon would land on - worked out without having to move it there to find out.
            bearing = targetRot - (_formSys.GetWorldRotation(form) - form.LocalRotation);
        }

        // Tested before the swing rather than after it. Rotating first and letting the check refuse the shot
        // afterwards left the gun parked in the arc it was refused on, and the console repeats the same order ten
        // times a second, so it stayed there: a turret visibly stuck facing its own hull instead of holding the
        // last bearing it could actually shoot from.
        if (!SafetyCheck(bearing - Math.PI / 2, cannon))
            return false;

        if (swings)
        {
            _formSys.SetWorldRotation(uid, targetRot);
            entPos = new EntityCoordinates(form.MapUid.Value, pos);
        }

        EntityManager.System<ShipWeaponTargetingSystem>().SetConsole(uid, console);
        _gunSys.AttemptShoot(uid, uid, gun, entPos);
        return true;
    }

    /// <summary>
    /// Angular slack allowed when judging whether a welded gun's axis lines up with a target. Shared with the
    /// ship AI's arc solver so a hull that turns to a bearing on account of a fixed gun then actually shoots
    /// with it. This is a firing arc, not the gun's spread cone - never substitute <c>GunComponent.MaxAngle</c>,
    /// which is the accuracy cap and has nothing to do with where the mount points.
    /// </summary>
    public static readonly Angle FixedMountAimTolerance = Angle.FromDegrees(2);

    /// <summary>
    /// Whether a mounted cannon can physically bear on a world position. Traversing hardpoints can always turn;
    /// fixed mounts only qualify when their welded axis is already close enough to the target bearing.
    /// </summary>
    public bool CanAimAt(
        EntityUid uid,
        Vector2 position,
        TransformComponent? form = null)
    {
        if (form == null && !_xformQuery.TryGetComponent(uid, out form))
            return false;
        if (!_anchorQuery.TryGetComponent(uid, out var anchor) || anchor.anchoredTo is not { } hardpoint)
            return false;

        // A mount that no longer exists is not a traversing one. Reading "no HardpointFixedMount" as "it can
        // turn" meant a gun whose hardpoint had been destroyed under it reported that it could bear on anything.
        if (!_hardpointQuery.HasComponent(hardpoint))
            return false;

        if (!_fixedMountQuery.HasComponent(hardpoint))
            return true;

        var delta = position - _formSys.GetWorldPosition(form);
        if (delta.LengthSquared() < 0.01f)
            return false;

        // A welded gun shoots down its own local -Y, which is precisely the world rotation TryFireCannon turns a
        // traversing mount to before firing: SetWorldRotation(uid, Angle.FromWorldVec(pos - cannonPos)). So the
        // mount's world rotation and the bearing to the target are already in the same frame and compare
        // directly. Offsetting either one by a quarter turn puts every fixed gun 90 degrees off its real axis.
        var axis = _formSys.GetWorldRotation(form);
        var target = Angle.FromWorldVec(delta);
        return Math.Abs(Angle.ShortestDistance(axis, target).Theta) <= FixedMountAimTolerance.Theta;
    }

    /// <summary>
    ///     Crescent - the standing conditions <see cref="TryFireCannon"/> needs before a gun will fire at all:
    ///     mounted on a hardpoint, and that hardpoint powered while ship power draw is on. Deliberately leaves out
    ///     the transient ones (cooldown, having a map) so that anything reasoning about what a hull can bring to
    ///     bear - the ship AI's arc solver - counts exactly the guns this system will actually shoot.
    /// </summary>
    public bool CanCannonFire(EntityUid cannonUid, out EntityUid hardpoint)
    {
        hardpoint = default;

        if (!_anchorQuery.TryGetComponent(cannonUid, out var anchorComp) ||
            anchorComp.anchoredTo is not { } mount ||
            !_hardpoint.IsMounted((cannonUid, anchorComp)))
            return false;

        // The hardpoint must not reintroduce a power requirement while ship-weapon power is opted out.
        if (_shipPowerDrawEnabled &&
            (!_powerQuery.TryGetComponent(mount, out var powerComp) || !powerComp.Powered))
            return false;

        hardpoint = mount;
        return true;
    }

    public bool SafetyCheck(Angle ang, PointCannonComponent cannon)
    {
        foreach (var (start, width) in cannon.ObstructedRanges)
        {
            if (CrescentHelpers.AngInSector(ang, start, width))
                return false;
        }
        return true;
    }

    public void RefreshFiringRanges(EntityUid uid, TransformComponent? form = null, GunComponent? gun = null, PointCannonComponent? cannon = null, int? range = null)
    {
        if (form == null && !_xformQuery.TryGetComponent(uid, out form))
            return;
        if (gun == null && !_gunQuery.TryGetComponent(uid, out gun))
            return;
        if (cannon == null && !_cannonQuery.TryGetComponent(uid, out cannon))
            return;
        range ??= 10;

        cannon.ObstructedRanges = CalculateFiringRanges(uid, form, gun, cannon, range.Value);
        Dirty(uid, cannon);
    }

    private List<(Angle, Angle)> CalculateFiringRanges(EntityUid uid, TransformComponent form, GunComponent gun, PointCannonComponent cannon, int range)
    {
        if (form.GridUid == null)
            return new();

        var gridUid = form.GridUid.Value;
        var cannonPos = form.LocalPosition;

        var entities = _lookup.GetEntitiesInRange(uid, range, LookupFlags.Static);
        var sectors = new List<(Angle start, Angle width)>();

        foreach (var childUid in entities)
        {
            if (!_xformQuery.TryGetComponent(childUid, out var otherForm))
                continue;
            if (otherForm.GridUid != gridUid)
                continue;

            var dir = otherForm.LocalPosition - cannonPos;
            if (!otherForm.Anchored)
                continue;

            if (!_physicsQuery.TryGetComponent(childUid, out var body) || !body.Hard)
                continue;

            if ((body.CollisionLayer & (int)CollisionGroup.BulletImpassable) == 0)
                continue;

            var (start, width) = GetObstacleSector(dir);
            sectors.Add((start, width));
        }

        if (sectors.Count == 0)
            return new();

        sectors.Sort((a, b) => a.start.Theta.CompareTo(b.start.Theta));

        var merged = new List<(Angle start, Angle width)>();
        var current = sectors[0];

        for (int i = 1; i < sectors.Count; i++)
        {
            var next = sectors[i];
            if (CrescentHelpers.AngSectorsOverlap(current.start, current.width, next.start, next.width))
            {
                var (newStart, newWidth) = CrescentHelpers.AngCombinedSector(current.start, current.width, next.start, next.width);
                current = (newStart, newWidth);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        var maxSpread = gun.MaxAngle + Angle.FromDegrees(10);
        var clearance = maxSpread + cannon.ClearanceAngle;

        var result = new List<(Angle, Angle)>(merged.Count);
        foreach (var (start, width) in merged)
        {
            result.Add((CrescentHelpers.AngNormal(start - clearance / 2), width + clearance));
        }

        return result;
    }

    private (Angle, Angle) GetObstacleSector(Vector2 delta)
    {
        Angle dirAngle = CrescentHelpers.AngNormal(new Angle(delta));
        Vector2 a, b;

        if (dirAngle % (Math.PI * 0.5) == 0)
        {
            switch (dirAngle.Theta)
            {
                case 0:
                case Math.Tau:
                    a = delta - Vector2Helpers.Half;
                    b = new Vector2(delta.X - 0.5f, delta.Y + 0.5f);
                    break;
                case Math.PI * 0.5:
                    a = delta - Vector2Helpers.Half;
                    b = new Vector2(delta.X + 0.5f, delta.Y - 0.5f);
                    break;
                case Math.PI:
                    a = delta + Vector2Helpers.Half;
                    b = new Vector2(delta.X + 0.5f, delta.Y - 0.5f);
                    break;
                case Math.PI * 1.5:
                    a = delta + Vector2Helpers.Half;
                    b = new Vector2(delta.X - 0.5f, delta.Y + 0.5f);
                    break;
                default:
                    return (double.NaN, double.NaN);
            }
        }
        else if (dirAngle > 0 && dirAngle < Math.PI * 0.5 || dirAngle > Math.PI && dirAngle < Math.PI * 1.5)
        {
            a = new Vector2(delta.X - 0.5f, delta.Y + 0.5f);
            b = new Vector2(delta.X + 0.5f, delta.Y - 0.5f);
        }
        else
        {
            a = delta + Vector2Helpers.Half;
            b = delta - Vector2Helpers.Half;
        }

        Angle start = CrescentHelpers.AngNormal(new Angle(a));
        Angle end = CrescentHelpers.AngNormal(new Angle(b));
        Angle width = Angle.ShortestDistance(start, end);
        if (width < 0)
        {
            start = end;
            width = -width;
        }

        return (start, width);
    }

    private List<ICommonSession> GetUiSessions(EntityUid uid)
    {
        var sessions = new List<ICommonSession>();
        foreach (var actorUid in _uiSys.GetActors(uid, TargetingConsoleUiKey.Key))
        {
            if (_playerMan.TryGetSessionByEntity(actorUid, out var session))
                sessions.Add(session);
        }
        return sessions;
    }

    private void TogglePvsOverride(IEnumerable<EntityUid> uids, IEnumerable<ICommonSession> sessions, bool enable)
    {
        foreach (var session in sessions)
        {
            foreach (var uid in uids)
            {
                if (!Exists(uid))
                    continue;

                if (enable)
                    _pvsSys.AddSessionOverride(uid, session);
                else
                    _pvsSys.RemoveSessionOverride(uid, session);
            }
        }
    }
}
