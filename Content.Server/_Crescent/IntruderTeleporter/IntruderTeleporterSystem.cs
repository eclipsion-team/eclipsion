using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Chat.Managers;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent.IntruderTeleporter;
using Content.Shared.Access.Systems;
using Content.Shared.Administration.Logs;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.IntruderTeleporter;

/// <summary>
/// Drives the boarding console. See <see cref="IntruderTeleporterComponent"/> for what the device is meant to
/// be; this is the roster, the targeting and the telegraphed drop that make it work.
/// </summary>
public sealed class IntruderTeleporterSystem : EntitySystem
{
    /// <summary>
    /// How far off a hull a click on the scanner may land and still count as picking that vessel, in metres.
    /// Ships are only a few pixels wide on a 200m radar, so the click needs slack to be usable at all.
    /// </summary>
    private const float ClickSlack = 12f;

    /// <summary>
    /// Seconds between pushes of the console's UI state. The scanner redraws itself from live entities, so
    /// this only has to keep the roster, the range readout and the target status honest.
    /// </summary>
    private const float UiUpdateInterval = 0.5f;

    private float _uiAccumulator;

    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDef = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedShuttleSystem _shuttle = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<IntruderTeleporterComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<IntruderTeleporterComponent, IntruderTeleporterToggleJoinMessage>(OnToggleJoin);
        SubscribeLocalEvent<IntruderTeleporterComponent, IntruderTeleporterSelectTargetMessage>(OnSelectTarget);
        SubscribeLocalEvent<IntruderTeleporterComponent, IntruderTeleporterLaunchMessage>(OnLaunch);
        SubscribeLocalEvent<IntruderTeleporterComponent, IntruderTeleporterAbortMessage>(OnAbort);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;

        _uiAccumulator += frameTime;
        var pushUi = _uiAccumulator >= UiUpdateInterval;
        if (pushUi)
            _uiAccumulator = 0f;

        var query = EntityQueryEnumerator<IntruderTeleporterComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var ent = (uid, comp);

            if (comp.CooldownEnd is { } cooldownEnd && now >= cooldownEnd)
                comp.CooldownEnd = null;

            if (comp.LaunchAt is { } launchAt)
            {
                // Cutting the console's power mid-countdown calls the drop off. The target has already been
                // warned, so this is a real counter for them rather than a free reset for the attacker: the
                // cooldown was started by the launch order and is deliberately left running.
                if (!_power.IsPowered(uid))
                {
                    AbortLaunch(ent, "intruder-teleporter-abort-power");
                }
                else
                {
                    if (!comp.FinalWarningSent && now >= launchAt - TimeSpan.FromSeconds(comp.FinalWarningDelay))
                    {
                        comp.FinalWarningSent = true;
                        if (comp.LaunchTarget is { } warnTarget && !TerminatingOrDeleted(warnTarget))
                            WarnTarget(ent, warnTarget, (int) comp.FinalWarningDelay, true);
                    }

                    if (now >= launchAt)
                        ExecuteLaunch(ent);
                }
            }

            if (pushUi && _ui.IsUiOpen(uid, IntruderTeleporterUiKey.Key))
                UpdateUi(ent);
        }
    }

    #region UI

    private void OnUiOpened(Entity<IntruderTeleporterComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!Equals(args.UiKey, IntruderTeleporterUiKey.Key))
            return;

        UpdateUi(ent);
    }

    private void UpdateUi(Entity<IntruderTeleporterComponent> ent)
    {
        PruneSquad(ent);

        var xform = Transform(ent);

        // Docks are irrelevant to a boarding console and gathering them is a global sweep, so the scanner is
        // fed an empty set rather than paying for GetAllDocks() twice a second.
        var nav = _console.GetNavState(
            (ent.Owner, null, xform),
            new Dictionary<NetEntity, List<DockingPortState>>(),
            xform.Coordinates,
            0);

        var squad = new List<IntruderSquadMember>(ent.Comp.Squad.Count);
        foreach (var member in ent.Comp.Squad)
        {
            squad.Add(new IntruderSquadMember(
                GetNetEntity(member),
                Identity.Name(member, EntityManager),
                _access.IsAllowed(member, ent)));
        }

        var status = GetTargetStatus(ent, ent.Comp.SelectedTarget, out var distance);
        if (status == IntruderTargetStatus.Lost)
        {
            ent.Comp.SelectedTarget = null;
            status = IntruderTargetStatus.None;
        }

        var state = new IntruderTeleporterBuiState(nav, squad)
        {
            MaxSquadSize = ent.Comp.MaxSquadSize,
            Range = ent.Comp.Range,
            WarningDelay = ent.Comp.WarningDelay,
            SelectedTarget = ent.Comp.SelectedTarget is { } selected ? GetNetEntity(selected) : null,
            SelectedTargetName = ent.Comp.SelectedTarget is { } named ? GetVesselName(named) : null,
            SelectedTargetDistance = distance,
            SelectedTargetStatus = status,
            LaunchAt = ent.Comp.LaunchAt,
            LaunchTargetName = ent.Comp.LaunchTarget is { } flight && !TerminatingOrDeleted(flight)
                ? GetVesselName(flight)
                : null,
            CooldownEnd = ent.Comp.CooldownEnd,
        };

        _ui.SetUiState(ent.Owner, IntruderTeleporterUiKey.Key, state);
    }

    /// <summary>
    /// Drops roster entries that can no longer board - deleted, or no longer alive. Somebody who merely walked
    /// off the console's grid is left on the roster and misses the drop instead, so that stepping away for a
    /// moment does not silently cost them their seat.
    /// </summary>
    private void PruneSquad(Entity<IntruderTeleporterComponent> ent)
    {
        ent.Comp.Squad.RemoveAll(member =>
            TerminatingOrDeleted(member)
            || !TryComp<MobStateComponent>(member, out var mob)
            || !_mobState.IsAlive(member, mob));
    }

    #endregion

    #region Roster

    private void OnToggleJoin(EntityUid uid, IntruderTeleporterComponent comp, IntruderTeleporterToggleJoinMessage args)
    {
        var user = args.Actor;
        var ent = (uid, comp);

        if (comp.Squad.Remove(user))
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-left"), uid, user);
            UpdateUi(ent);
            return;
        }

        if (comp.LaunchAt != null)
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-roster-locked"), uid, user);
            return;
        }

        if (comp.Squad.Count >= comp.MaxSquadSize)
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-roster-full", ("max", comp.MaxSquadSize)), uid, user);
            return;
        }

        if (!TryComp<MobStateComponent>(user, out var mob) || !_mobState.IsAlive(user, mob))
            return;

        comp.Squad.Add(user);
        _popup.PopupEntity(Loc.GetString("intruder-teleporter-joined"), uid, user);
        UpdateUi(ent);
    }

    #endregion

    #region Targeting

    private void OnSelectTarget(EntityUid uid, IntruderTeleporterComponent comp, IntruderTeleporterSelectTargetMessage args)
    {
        var ent = (uid, comp);

        // Once the order is out the target is fixed. Otherwise the scanner could be swung onto a second vessel
        // mid-countdown and drop the squad somewhere nobody was ever warned about.
        if (comp.LaunchAt != null)
            return;

        if (!_access.IsAllowed(args.Actor, uid))
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-access-denied"), uid, args.Actor);
            return;
        }

        var clicked = GetCoordinates(args.Coordinates);
        if (!clicked.IsValid(EntityManager))
            return;

        var mapCoords = _transform.ToMapCoordinates(clicked);
        var consoleXform = Transform(uid);
        if (consoleXform.MapID != mapCoords.MapId)
            return;

        var consolePos = _transform.GetWorldPosition(consoleXform);

        EntityUid? best = null;
        var bestClickDistance = float.MaxValue;

        var query = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var gridUid, out var grid, out var gridXform))
        {
            if (gridXform.MapID != mapCoords.MapId || gridUid == consoleXform.GridUid)
                continue;

            if (!IsHostile(ent, gridUid))
                continue;

            var worldAabb = GetWorldAabb((gridUid, grid), gridXform);

            if (DistanceToAabb(consolePos, worldAabb) > comp.Range)
                continue;

            var clickDistance = DistanceToAabb(mapCoords.Position, worldAabb);
            if (clickDistance > ClickSlack || clickDistance >= bestClickDistance)
                continue;

            bestClickDistance = clickDistance;
            best = gridUid;
        }

        if (best == null)
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-no-target-there"), uid, args.Actor);
            return;
        }

        comp.SelectedTarget = best;
        UpdateUi(ent);
    }

    /// <summary>
    /// Whether the console's own grid is at a relation with <paramref name="grid"/> that allows boarding it.
    /// Vessels running dark are excluded: there is nothing on the scanner to click in the first place.
    /// </summary>
    private bool IsHostile(Entity<IntruderTeleporterComponent> ent, EntityUid grid)
    {
        if (!TryComp<IFFComponent>(grid, out var iff) || (iff.Flags & (IFFFlags.Hide | IFFFlags.HideLabel)) != 0)
            return false;

        if (Transform(ent).GridUid is not { } consoleGrid)
            return false;

        var relation = _shuttle.GetIFFRelation(consoleGrid, iff.Faction);
        return ent.Comp.HostileRelations.Contains(relation);
    }

    private IntruderTargetStatus GetTargetStatus(Entity<IntruderTeleporterComponent> ent, EntityUid? target, out float distance)
    {
        distance = 0f;

        if (target is not { } grid)
            return IntruderTargetStatus.None;

        if (TerminatingOrDeleted(grid) || !TryComp<MapGridComponent>(grid, out var gridComp))
            return IntruderTargetStatus.Lost;

        var gridXform = Transform(grid);
        var consoleXform = Transform(ent);
        if (gridXform.MapID != consoleXform.MapID || gridXform.MapID == MapId.Nullspace)
            return IntruderTargetStatus.Lost;

        distance = DistanceToAabb(_transform.GetWorldPosition(consoleXform), GetWorldAabb((grid, gridComp), gridXform));

        if (!IsHostile(ent, grid))
            return IntruderTargetStatus.NotHostile;

        return distance > ent.Comp.Range ? IntruderTargetStatus.OutOfRange : IntruderTargetStatus.Valid;
    }

    private Box2 GetWorldAabb(Entity<MapGridComponent> grid, TransformComponent xform)
    {
        var (_, _, matrix) = xform.GetWorldPositionRotationMatrix();
        return matrix.TransformBox(grid.Comp.LocalAABB);
    }

    /// <summary>
    /// Hull-to-point distance rather than origin-to-origin: a grid's origin can sit well off its own hull, and
    /// a 200m limit measured from there would refuse boarding actions that are visibly alongside.
    /// </summary>
    private static float DistanceToAabb(Vector2 point, Box2 aabb)
    {
        return (Vector2.Clamp(point, aabb.BottomLeft, aabb.TopRight) - point).Length();
    }

    #endregion

    #region Launch

    private void OnLaunch(EntityUid uid, IntruderTeleporterComponent comp, IntruderTeleporterLaunchMessage args)
    {
        var user = args.Actor;
        var ent = (uid, comp);

        if (!_access.IsAllowed(user, uid))
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-access-denied"), uid, user);
            return;
        }

        if (comp.LaunchAt != null)
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-already-launching"), uid, user);
            return;
        }

        if (comp.CooldownEnd is { } cooldownEnd && _timing.CurTime < cooldownEnd)
        {
            var remaining = (int) (cooldownEnd - _timing.CurTime).TotalSeconds;
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-on-cooldown", ("seconds", remaining)), uid, user);
            return;
        }

        if (!_power.IsPowered(uid))
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-unpowered"), uid, user);
            return;
        }

        PruneSquad(ent);
        if (comp.Squad.Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-roster-empty"), uid, user);
            return;
        }

        var status = GetTargetStatus(ent, comp.SelectedTarget, out _);
        if (comp.SelectedTarget is not { } target || status != IntruderTargetStatus.Valid)
        {
            _popup.PopupEntity(GetStatusMessage(status), uid, user);
            return;
        }

        var now = _timing.CurTime;
        comp.LaunchTarget = target;
        comp.LaunchSquad = new List<EntityUid>(comp.Squad);
        comp.LaunchAt = now + TimeSpan.FromSeconds(comp.WarningDelay);
        // A second warning that would land at or before the first one is no warning at all.
        comp.FinalWarningSent = comp.FinalWarningDelay <= 0f || comp.FinalWarningDelay >= comp.WarningDelay;
        // Timed from the order rather than the arrival, so aborting does not hand the console straight back.
        comp.CooldownEnd = now + TimeSpan.FromSeconds(comp.Cooldown);

        WarnTarget(ent, target, (int) comp.WarningDelay, false);

        foreach (var member in comp.LaunchSquad)
        {
            _popup.PopupEntity(
                Loc.GetString("intruder-teleporter-squad-countdown", ("seconds", (int) comp.WarningDelay)),
                member,
                member,
                PopupType.LargeCaution);
        }

        _adminLog.Add(LogType.Teleport, LogImpact.Extreme,
            $"{ToPrettyString(user):user} ordered a boarding action from {ToPrettyString(uid):console} onto {ToPrettyString(target):target} " +
            $"in {comp.WarningDelay} seconds with squad [{string.Join(", ", comp.LaunchSquad.Select(member => ToPrettyString(member).ToString()))}]");

        UpdateUi(ent);
    }

    private void OnAbort(EntityUid uid, IntruderTeleporterComponent comp, IntruderTeleporterAbortMessage args)
    {
        if (comp.LaunchAt == null)
            return;

        if (!_access.IsAllowed(args.Actor, uid))
        {
            _popup.PopupEntity(Loc.GetString("intruder-teleporter-access-denied"), uid, args.Actor);
            return;
        }

        _adminLog.Add(LogType.Teleport, LogImpact.High,
            $"{ToPrettyString(args.Actor):user} aborted the boarding action from {ToPrettyString(uid):console}");

        AbortLaunch((uid, comp), "intruder-teleporter-abort-manual");
    }

    /// <summary>
    /// Stands an in-flight launch down. The target is told, because it was told the launch was coming.
    /// </summary>
    private void AbortLaunch(Entity<IntruderTeleporterComponent> ent, string reasonLoc)
    {
        if (ent.Comp.LaunchTarget is { } target && !TerminatingOrDeleted(target))
            AnnounceToGrid(target, Loc.GetString("intruder-teleporter-warning-cleared"), null);

        foreach (var member in ent.Comp.LaunchSquad)
        {
            if (!TerminatingOrDeleted(member))
                _popup.PopupEntity(Loc.GetString(reasonLoc), member, member, PopupType.MediumCaution);
        }

        ent.Comp.LaunchAt = null;
        ent.Comp.LaunchTarget = null;
        ent.Comp.LaunchSquad.Clear();
        ent.Comp.FinalWarningSent = false;

        UpdateUi(ent);
    }

    private void ExecuteLaunch(Entity<IntruderTeleporterComponent> ent)
    {
        var target = ent.Comp.LaunchTarget;
        var squad = new List<EntityUid>(ent.Comp.LaunchSquad);

        ent.Comp.LaunchAt = null;
        ent.Comp.LaunchTarget = null;
        ent.Comp.LaunchSquad.Clear();
        ent.Comp.FinalWarningSent = false;

        // The warning window is long enough for the target to FTL out, be destroyed, or simply outrun the
        // console, and none of those should end with the squad thrown into open space.
        if (target is not { } targetGrid
            || GetTargetStatus(ent, targetGrid, out _) != IntruderTargetStatus.Valid
            || !TryComp<MapGridComponent>(targetGrid, out var gridComp)
            || Transform(targetGrid).MapUid is not { } targetMap)
        {
            foreach (var member in squad)
            {
                if (!TerminatingOrDeleted(member))
                    _popup.PopupEntity(Loc.GetString("intruder-teleporter-abort-target-lost"), member, member, PopupType.LargeCaution);
            }

            _adminLog.Add(LogType.Teleport, LogImpact.High,
                $"Boarding action from {ToPrettyString(ent.Owner):console} failed: the target was lost before the drop");
            UpdateUi(ent);
            return;
        }

        var consoleGrid = Transform(ent).GridUid;
        var usedTiles = new HashSet<Vector2i>();
        var landed = 0;

        foreach (var member in squad)
        {
            // The roster snapshot is taken at the order, so anyone who went down during the countdown is still
            // on it. Nobody gets a body delivered onto the target.
            if (TerminatingOrDeleted(member) || !_mobState.IsAlive(member))
                continue;

            // Moving a contained entity by coordinates pulls it out behind the container's back and leaves the
            // container still listing it. Mech pilots, lockers and body bags all stay home.
            if (_container.IsEntityOrParentInContainer(member))
            {
                _popup.PopupEntity(Loc.GetString("intruder-teleporter-contained"), member, member, PopupType.LargeCaution);
                continue;
            }

            // The console throws whoever is standing on its own hull. Wandering off during the countdown is
            // how you miss the drop, and it keeps the device from reaching across the sector for stragglers.
            if (Transform(member).GridUid != consoleGrid)
            {
                _popup.PopupEntity(Loc.GetString("intruder-teleporter-left-behind"), member, member, PopupType.LargeCaution);
                continue;
            }

            if (!TryFindDropTile(ent, (targetGrid, gridComp), targetMap, usedTiles, out var coords))
            {
                _popup.PopupEntity(Loc.GetString("intruder-teleporter-no-drop-point"), member, member, PopupType.LargeCaution);
                continue;
            }

            _transform.SetCoordinates(member, coords);
            _audio.PlayPvs(ent.Comp.ArrivalSound, member);
            landed++;

            _adminLog.Add(LogType.Teleport, LogImpact.Extreme,
                $"{ToPrettyString(member):player} boarded {ToPrettyString(targetGrid):target} at {coords} via {ToPrettyString(ent.Owner):console}");
        }

        _audio.PlayPvs(ent.Comp.DepartureSound, ent.Owner);

        if (landed > 0)
        {
            AnnounceToGrid(targetGrid,
                Loc.GetString("intruder-teleporter-warning-arrived", ("count", landed)),
                ent.Comp.WarningSound);
        }

        UpdateUi(ent);
    }

    /// <summary>
    /// Picks a tile on the target that a person can survive standing on. Sampled rather than enumerated: a
    /// station runs to thousands of tiles and every candidate costs an atmos lookup.
    /// </summary>
    private bool TryFindDropTile(
        Entity<IntruderTeleporterComponent> ent,
        Entity<MapGridComponent> grid,
        EntityUid map,
        HashSet<Vector2i> used,
        out EntityCoordinates coords)
    {
        coords = EntityCoordinates.Invalid;

        var bounds = grid.Comp.LocalAABB;
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return false;

        for (var i = 0; i < ent.Comp.DropSearchAttempts; i++)
        {
            var tile = new Vector2i(
                _random.Next((int) MathF.Floor(bounds.Left), (int) MathF.Ceiling(bounds.Right)),
                _random.Next((int) MathF.Floor(bounds.Bottom), (int) MathF.Ceiling(bounds.Top)));

            // Two boarders on one tile would shove each other through a wall on arrival.
            if (!used.Add(tile))
                continue;

            if (!_map.TryGetTileRef(grid.Owner, grid.Comp, tile, out var tileRef))
                continue;

            if (tileRef.Tile.IsEmpty || tileRef.IsSpace(_tileDef))
                continue;

            if (_turf.IsTileBlocked(tileRef, CollisionGroup.MobMask))
                continue;

            if (ent.Comp.RequireBreathableDrop && !_atmos.IsTileMixtureProbablySafe((grid.Owner, null), (map, null), tile))
                continue;

            coords = _map.GridTileToLocal(grid.Owner, grid.Comp, tile);
            return true;
        }

        return false;
    }

    #endregion

    #region Warnings

    private void WarnTarget(Entity<IntruderTeleporterComponent> ent, EntityUid target, int seconds, bool final)
    {
        string message;
        if (ent.Comp.RevealSourceVessel && Transform(ent).GridUid is { } sourceGrid)
        {
            message = Loc.GetString(
                final ? "intruder-teleporter-warning-final-source" : "intruder-teleporter-warning-source",
                ("seconds", seconds),
                ("vessel", GetVesselName(sourceGrid)));
        }
        else
        {
            message = Loc.GetString(
                final ? "intruder-teleporter-warning-final" : "intruder-teleporter-warning",
                ("seconds", seconds));
        }

        AnnounceToGrid(target, message, ent.Comp.WarningSound);

        _adminLog.Add(LogType.Teleport, LogImpact.High,
            $"Boarding warning sent to {ToPrettyString(target):target} from {ToPrettyString(ent.Owner):console}: {message}");
    }

    /// <summary>
    /// Puts a red console line in front of everybody aboard one grid, and nobody else. Deliberately not a
    /// station announcement: those play their sound to the whole server and reach every grid of the station,
    /// which would tell half the sector that a boarding action is inbound.
    /// </summary>
    private void AnnounceToGrid(EntityUid grid, string message, SoundSpecifier? sound)
    {
        var filter = Filter.BroadcastGrid(grid);
        if (filter.Count == 0)
            return;

        var wrapped = Loc.GetString("chat-manager-sender-announcement-wrap-message",
            ("sender", Loc.GetString("intruder-teleporter-warning-sender")),
            ("message", FormattedMessage.EscapeText(message)));

        _chatManager.ChatMessageToManyFiltered(filter, ChatChannel.Radio, message, wrapped, grid, false, true, Color.Red);

        if (sound != null)
            _audio.PlayGlobal(sound, filter, true);
    }

    #endregion

    private string GetStatusMessage(IntruderTargetStatus status)
    {
        return Loc.GetString(status switch
        {
            IntruderTargetStatus.OutOfRange => "intruder-teleporter-status-outofrange",
            IntruderTargetStatus.NotHostile => "intruder-teleporter-status-nothostile",
            IntruderTargetStatus.Lost => "intruder-teleporter-status-lost",
            _ => "intruder-teleporter-status-none",
        });
    }

    private string GetVesselName(EntityUid grid)
    {
        return _shuttle.GetIFFLabel(grid) ?? MetaData(grid).EntityName;
    }
}
