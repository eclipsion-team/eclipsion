using System.Linq;
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.SurveillanceCamera;
using Content.Server._Crescent.Squad;
using Content.Shared._Crescent.Overwatch;
using Content.Shared._Crescent.Squad;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Implants.Components;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Overwatch;

/// <summary>
/// System for managing Overwatch.
/// </summary>
public sealed class OverwatchSystem : EntitySystem
{
    /// <summary>
    /// Cache invalidation interval, in seconds.
    /// The cache is dropped whenever faction membership changes.
    /// </summary>
    private const float CacheInvalidationInterval = 2.0f;

    /// <summary>
    /// UI refresh interval, in seconds.
    /// </summary>
    private const float UpdateInterval = 1.0f;

    [Dependency] private readonly MobStateSystem _mobStateSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly AccessReaderSystem _accessReaderSystem = default!;
    [Dependency] private readonly SurveillanceCameraSystem _cameraSystem = default!;
    [Dependency] private readonly SharedIdCardSystem _idCardSystem = default!;
    [Dependency] private readonly SharedEyeSystem _eyeSystem = default!;
    [Dependency] private readonly ViewSubscriberSystem _viewSubscriberSystem = default!;
    [Dependency] private readonly SquadSystem _squadSystem = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    /// Watch pairs: watcher -> target.
    /// </summary>
    private readonly Dictionary<EntityUid, EntityUid> _watchingPairs = new();

    /// <summary>
    /// Cached faction members, to cut allocations during frequent refreshes.
    /// </summary>
    private readonly Dictionary<string, List<EntityUid>> _factionMembersCache = new();

    /// <summary>
    /// Cached member data for the UI.
    /// </summary>
    private readonly Dictionary<EntityUid, OverwatchMemberData> _memberDataCache = new();

    private float _cacheInvalidationTimer;
    private float _updateTimer;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<OverwatchConsoleComponent, ActivatableUIOpenAttemptEvent>(OnUIOpenAttempt);
        SubscribeLocalEvent<OverwatchConsoleComponent, BeforeActivatableUIOpenEvent>(OnBeforeUIOpen);
        SubscribeLocalEvent<OverwatchConsoleComponent, OverwatchRefreshMessage>(OnRefresh);
        SubscribeLocalEvent<OverwatchConsoleComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<OverwatchConsoleComponent, ComponentShutdown>(OnConsoleShutdown);
        SubscribeLocalEvent<RatOverwatchWatchingComponent, MoveInputEvent>(OnWatchingMoveInput);
        SubscribeLocalEvent<RatOverwatchWatchingComponent, ComponentShutdown>(OnWatchingShutdown);
        SubscribeLocalEvent<RatOverwatchCameraComponent, ComponentShutdown>(OnCameraShutdown);
        SubscribeLocalEvent<RatOverwatchCameraComponent, EntityTerminatingEvent>(OnWatchedEntityTerminating);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn); // Refresh the UI when a player spawns, so new faction members show up
        // Invalidate the cache when faction or squad membership changes
        SubscribeLocalEvent<HullrotFactionComponent, ComponentInit>(OnFactionComponentInit);
        SubscribeLocalEvent<HullrotFactionComponent, ComponentShutdown>(OnFactionComponentShutdown);
        SubscribeLocalEvent<SquadComponent, ComponentInit>(OnSquadComponentInit);
        SubscribeLocalEvent<SquadComponent, ComponentShutdown>(OnSquadComponentShutdown);

        Subs.BuiEvents<OverwatchConsoleComponent>(OverwatchUiKey.Key, subs =>
        {
            subs.Event<OverwatchViewCameraMessage>(OnViewCamera);
            subs.Event<OverwatchStopWatchingMessage>(OnStopWatching);
            subs.Event<OverwatchCreateSquadMessage>(OnCreateSquad);
            subs.Event<OverwatchDeleteSquadMessage>(OnDeleteSquad);
            subs.Event<OverwatchAssignSquadMessage>(OnAssignSquad);
            subs.Event<OverwatchRemoveSquadMemberMessage>(OnRemoveSquadMember);
            subs.Event<OverwatchSendMessageAnnouncement>(OnSendAnnouncement);
        });
    }

    /// <summary>
    /// Drops every cross-round scrap of state. The caches and watch pairs are plain system fields, so
    /// they outlive the entities they name; they happen to be emptied by component shutdowns at round
    /// end today, but relying on that leaves stale EntityUids one refactor away.
    /// </summary>
    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _watchingPairs.Clear();
        _factionMembersCache.Clear();
        _memberDataCache.Clear();
    }

    /// <summary>
    /// Whether <paramref name="user"/> may operate this console: they must belong to the faction it
    /// watches. Checked on open <b>and</b> on every message, because a crafted message can arrive
    /// without the UI ever having been opened, and because a player can be stripped of their faction
    /// while the UI is still up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership is read from the body, not the ID card, so a stolen card cannot turn a boarder into
    /// staff — the same stance the treasury vault takes. Admin ghosts are let through so they can look
    /// inside without being treated as intruders.
    /// </para>
    /// <para>
    /// This is the second gate, not the only one: the prototypes carry an <c>AccessReader</c> and
    /// <c>ActivatableUIRequiresAccess</c>, so a player must hold the faction's access level to get this
    /// far. It follows that a user carrying no faction component at all is let through on the ID alone
    /// — mobs that never had one (and any spawn path that forgets to set it) would otherwise be locked
    /// out of a console they hold a valid key for. What this stops is the case that actually matters:
    /// someone who demonstrably belongs to a <i>different</i> faction working a stolen card.
    /// </para>
    /// </remarks>
    private bool IsAuthorized(Entity<OverwatchConsoleComponent> ent, EntityUid user)
    {
        if (HasComp<GhostComponent>(user))
            return true;

        var faction = ent.Comp.Faction;
        if (string.IsNullOrEmpty(faction))
            return false;

        var userFaction = CompOrNull<HullrotFactionComponent>(user)?.Faction;

        return string.IsNullOrEmpty(userFaction) || userFaction == faction;
    }

    /// <summary>
    /// Invalidates the cache when a faction component is initialised.
    /// </summary>
    private void OnFactionComponentInit(EntityUid uid, HullrotFactionComponent component, ComponentInit args)
    {
        _factionMembersCache.Clear();
    }

    /// <summary>
    /// Invalidates the cache when a faction component is removed.
    /// </summary>
    private void OnFactionComponentShutdown(EntityUid uid, HullrotFactionComponent component, ComponentShutdown args)
    {
        _factionMembersCache.Clear();
        _memberDataCache.Remove(uid);
    }

    /// <summary>
    /// Invalidates the cache when a squad component is initialised.
    /// </summary>
    private void OnSquadComponentInit(EntityUid uid, SquadComponent component, ComponentInit args)
    {
        _factionMembersCache.Clear();
    }

    /// <summary>
    /// Invalidates the cache when a squad component is removed.
    /// </summary>
    private void OnSquadComponentShutdown(EntityUid uid, SquadComponent component, ComponentShutdown args)
    {
        _factionMembersCache.Clear();
        _memberDataCache.Remove(uid);
    }

    /// <summary>
    /// Console UI opened — stops watching if the player is already looking through a camera.
    /// </summary>
    private void OnBeforeUIOpen(Entity<OverwatchConsoleComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        if (TryComp<RatOverwatchWatchingComponent>(args.User, out var watchingComp) && watchingComp.Watching.HasValue)
        {
            StopWatching(args.User, watchingComp);
        }

        RefreshData(ent);
    }

    /// <summary>
    /// UI open attempt — checks the player belongs to the faction.
    /// </summary>
    /// <remarks>
    /// This used to be an empty body while still claiming to check membership, which left every
    /// standalone overwatch console (and the portable clipboard variants) open to anyone who reached
    /// one: full enemy roster, live positions, camera spectate and faction-wide announcements.
    /// </remarks>
    private void OnUIOpenAttempt(Entity<OverwatchConsoleComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || args.User is not { Valid: true } user)
            return;

        if (IsAuthorized(ent, user))
            return;

        args.Cancel();
        _popup.PopupEntity(Loc.GetString("overwatch-console-access-denied"), ent.Owner, user, PopupType.MediumCaution);
    }

    /// <summary>
    /// Player movement — stops watching as soon as the player tries to move.
    /// </summary>
    private void OnWatchingMoveInput(Entity<RatOverwatchWatchingComponent> ent, ref MoveInputEvent args)
    {
        if (!args.HasDirectionalMovement)
            return;

        StopWatching(ent.Owner, ent.Comp);
        RemComp<RatOverwatchWatchingComponent>(ent.Owner);
    }

    /// <summary>
    /// Refreshes the Overwatch console UI data.
    /// </summary>
    private void RefreshData(Entity<OverwatchConsoleComponent> ent)
    {
        var members = GetFactionMembers(ent.Comp.Faction);
        var memberData = new List<OverwatchMemberData>(members.Count);

        var availableSquads = _squadSystem.GetFactionSquads(ent.Comp.Faction);

        foreach (var member in members)
        {
            if (!_memberDataCache.TryGetValue(member, out var cachedData) ||
                ShouldRefreshData(member, cachedData))
            {
                var newData = CreateMemberData(member);
                _memberDataCache[member] = newData;
                memberData.Add(newData);
            }
            else
            {
                memberData.Add(cachedData);
            }
        }

        _uiSystem.SetUiState(ent.Owner, OverwatchUiKey.Key,
            new OverwatchUpdateState(
                memberData,
                availableSquads.ToDictionary(k => k.Key, v => v.Value.Name),
                GetOverwatchColor(ent.Comp.Faction)
            ));
    }

    /// <summary>
    /// Checks whether a member's data needs refreshing.
    /// </summary>
    /// <remarks>
    /// Status is compared first and for every member, not just for the disconnected. It used to be
    /// skipped for anyone still holding an <c>ActorComponent</c>, so a member who died without moving
    /// kept displaying as Alive — the one thing the console exists to report. That was masked only by
    /// <see cref="CacheInvalidationInterval"/> nuking the whole cache every couple of seconds.
    /// </remarks>
    private bool ShouldRefreshData(EntityUid member, OverwatchMemberData cachedData)
    {
        if (cachedData.Status != GetMemberStatus(member))
            return true;

        if (cachedData.Name != Name(member) || cachedData.JobTitle != GetJobTitle(member))
            return true;

        if (TryComp<SquadComponent>(member, out var squadComp))
        {
            if (cachedData.SquadId != squadComp.SquadId || cachedData.SquadName != squadComp.SquadName)
                return true;
        }
        else
        {
            // No component now, but the cache held a squad — needs refreshing
            if (cachedData.SquadId.HasValue)
                return true;
        }

        var (currentCoords, currentLocation) = GetMemberLocation(member);
        if (cachedData.Coordinates.HasValue != currentCoords.HasValue || cachedData.LocationName != currentLocation)
            return true;

        if (cachedData.Coordinates.HasValue && currentCoords.HasValue)
        {
            // Check whether the coordinates moved significantly (0.5f threshold)
            var dx = cachedData.Coordinates.Value.X - currentCoords.Value.X;
            var dy = cachedData.Coordinates.Value.Y - currentCoords.Value.Y;
            if (dx * dx + dy * dy > 0.25f) // 0.5f^2
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a member's data for display in the UI.
    /// </summary>
    private OverwatchMemberData CreateMemberData(EntityUid member)
    {
        var status = GetMemberStatus(member);
        var (coords, location) = GetMemberLocation(member);

        return new OverwatchMemberData(
            GetNetEntity(member),
            Name(member),
            GetJobTitle(member),
            status,
            // A corpse's camera shows a patch of floor. Reported here and enforced in OnViewCamera, so
            // the greyed-out button is backed by an actual rule rather than being decorative.
            status != OverwatchMemberStatus.Dead,
            GetSquadId(member),
            GetSquadName(member) ?? "",
            coords,
            location
        );
    }

    private void OnRefresh(Entity<OverwatchConsoleComponent> ent, ref OverwatchRefreshMessage args)
    {
        RefreshData(ent);
    }

    /// <summary>
    /// Handles a new squad being created. Both the name length and the squad count are capped: the
    /// roster is re-serialized to every open console once a second, so an unbounded list of unbounded
    /// names is a cheap way to bury the server in state updates.
    /// </summary>
    private void OnCreateSquad(Entity<OverwatchConsoleComponent> ent, ref OverwatchCreateSquadMessage args)
    {
        if (args.Actor is not { Valid: true } actor || !IsAuthorized(ent, actor))
            return;

        var name = args.SquadName.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        if (name.Length > OverwatchLimits.MaxSquadNameLength)
            name = name[..OverwatchLimits.MaxSquadNameLength];

        if (_squadSystem.GetFactionSquads(ent.Comp.Faction).Count >= OverwatchLimits.MaxSquadsPerFaction)
        {
            _popup.PopupEntity(
                Loc.GetString("overwatch-squad-limit-reached", ("max", OverwatchLimits.MaxSquadsPerFaction)),
                ent.Owner, actor, PopupType.MediumCaution);
            return;
        }

        if (_squadSystem.CreateSquad(ent.Comp.Faction, name))
        {
            RefreshData(ent);
        }
    }

    /// <summary>
    /// Handles a squad being deleted. Refuses to delete one that still has members — the client greys
    /// the button out, but the button is not the check.
    /// </summary>
    private void OnDeleteSquad(Entity<OverwatchConsoleComponent> ent, ref OverwatchDeleteSquadMessage args)
    {
        if (args.Actor is not { Valid: true } actor || !IsAuthorized(ent, actor))
            return;

        if (_squadSystem.GetSquadMemberCount(args.SquadId) > 0)
        {
            _popup.PopupEntity(Loc.GetString("overwatch-squad-not-empty"), ent.Owner, actor, PopupType.MediumCaution);
            return;
        }

        if (_squadSystem.RemoveSquad(ent.Comp.Faction, args.SquadId))
        {
            RefreshData(ent);
        }
    }

    /// <summary>
    /// Handles a player being assigned to a squad.
    /// </summary>
    private void OnAssignSquad(Entity<OverwatchConsoleComponent> ent, ref OverwatchAssignSquadMessage args)
    {
        if (args.Actor is not { Valid: true } actor || !IsAuthorized(ent, actor))
            return;

        var player = GetEntity(args.Player);
        if (!player.Valid)
            return;

        if (!TryComp<HullrotFactionComponent>(player, out var factionComp) ||
            factionComp.Faction != ent.Comp.Faction)
            return;

        if (_squadSystem.AssignToSquad(player, args.SquadId, ent.Comp.Faction))
        {
            RefreshData(ent);
        }
    }

    /// <summary>
    /// Handles a player being removed from a squad.
    /// </summary>
    private void OnRemoveSquadMember(Entity<OverwatchConsoleComponent> ent, ref OverwatchRemoveSquadMemberMessage args)
    {
        if (args.Actor is not { Valid: true } actor || !IsAuthorized(ent, actor))
            return;

        var player = GetEntity(args.Player);
        if (!player.Valid)
            return;

        if (!TryComp<HullrotFactionComponent>(player, out var factionComp) ||
            factionComp.Faction != ent.Comp.Faction)
            return;

        _squadSystem.RemoveFromSquad(player);
        RefreshData(ent);
    }

    /// <summary>
    /// Handles an Overwatch announcement being sent.
    /// </summary>
    private void OnSendAnnouncement(Entity<OverwatchConsoleComponent> ent, ref OverwatchSendMessageAnnouncement args)
    {
        if (args.Actor is not { Valid: true } actor || !IsAuthorized(ent, actor))
            return;

        var message = args.Message.Trim();
        if (string.IsNullOrEmpty(message))
            return;

        // Clamped rather than rejected: an operator who pastes something long still gets their
        // announcement out, just trimmed. Unbounded text is broadcast to the whole faction as a
        // full-screen overlay plus a chat line, so it cannot be left to the client to limit.
        if (message.Length > OverwatchLimits.MaxAnnouncementLength)
            message = message[..OverwatchLimits.MaxAnnouncementLength];

        var now = _timing.CurTime;
        if (ent.Comp.LastAnnounce is { } last && now - last < ent.Comp.AnnounceCooldown)
        {
            var secondsLeft = Math.Max(1, (int) Math.Ceiling((ent.Comp.AnnounceCooldown - (now - last)).TotalSeconds));
            _popup.PopupEntity(
                Loc.GetString("overwatch-announcement-cooldown", ("seconds", secondsLeft)),
                ent.Owner, actor, PopupType.Medium);
            return;
        }

        ent.Comp.LastAnnounce = now;

        var faction = ent.Comp.Faction;
        var factionMembers = GetFactionMembers(faction);

        _adminLogger.Add(LogType.Chat, LogImpact.Medium,
            $"{ToPrettyString(actor):player} sent a {faction} overwatch announcement: {message}");

        var targetName = !args.TargetSquadId.HasValue
            ? Loc.GetString("overwatch-announcement-target-all")
            : GetSquadTargetName(faction, args.TargetSquadId.Value);

        var overwatchTitle = GetOverwatchTitle(faction);
        var color = GetOverwatchColor(faction);

        var recipients = new List<ICommonSession>();

        foreach (var member in factionMembers)
        {
            if (args.TargetSquadId.HasValue)
            {
                var memberSquadId = GetSquadId(member);
                if (memberSquadId != args.TargetSquadId)
                    continue;
            }

            if (TryComp<ActorComponent>(member, out var memberActor) && memberActor.PlayerSession != null)
            {
                recipients.Add(memberActor.PlayerSession);
                RaiseNetworkEvent(new OverwatchAnnouncementEvent(message, targetName, overwatchTitle, color), memberActor.PlayerSession);
            }
        }

        if (recipients.Count == 0)
            return;

        // The body is player text (or carries player names); escape it so it cannot inject chat markup.
        var wrappedMessage = $"{overwatchTitle}: {FormattedMessage.EscapeText(message)}";

        var filter = Robust.Shared.Player.Filter.Empty();
        foreach (var recipient in recipients)
        {
            filter.AddPlayer(recipient);
        }

        _chatManager.ChatMessageToManyFiltered(
            filter,
            ChatChannel.Local,
            message,
            wrappedMessage,
            EntityUid.Invalid,
            false,
            true,
            color
        );
    }

    /// <summary>
    /// Broadcasts an overwatch-style announcement to every online member of a faction: the purple
    /// on-screen alert plus a filtered local chat line, exactly like a console-sent announcement.
    /// Lets other systems (e.g. treasury security) raise faction-only alerts without a console.
    /// </summary>
    /// <param name="faction">Faction key, e.g. "DSM", "NCWL", "SHI".</param>
    /// <param name="message">The announcement body.</param>
    /// <param name="targetName">Optional "target" label; defaults to the faction-wide label.</param>
    public void SendFactionAnnouncement(string faction, string message, string? targetName = null)
    {
        if (string.IsNullOrEmpty(faction) || string.IsNullOrEmpty(message))
            return;

        var factionMembers = GetFactionMembers(faction);
        var overwatchTitle = GetOverwatchTitle(faction);
        var color = GetOverwatchColor(faction);
        targetName ??= Loc.GetString("overwatch-announcement-target-all");

        var recipients = new List<ICommonSession>();
        foreach (var member in factionMembers)
        {
            if (TryComp<ActorComponent>(member, out var memberActor) && memberActor.PlayerSession != null)
            {
                recipients.Add(memberActor.PlayerSession);
                RaiseNetworkEvent(new OverwatchAnnouncementEvent(message, targetName, overwatchTitle, color), memberActor.PlayerSession);
            }
        }

        if (recipients.Count == 0)
            return;

        // The body is player text (or carries player names); escape it so it cannot inject chat markup.
        var wrappedMessage = $"{overwatchTitle}: {FormattedMessage.EscapeText(message)}";
        var filter = Robust.Shared.Player.Filter.Empty();
        foreach (var recipient in recipients)
            filter.AddPlayer(recipient);

        _chatManager.ChatMessageToManyFiltered(
            filter,
            ChatChannel.Local,
            message,
            wrappedMessage,
            EntityUid.Invalid,
            false,
            true,
            color);
    }

    /// <summary>
    /// Gets a squad's name.
    /// </summary>
    private string GetSquadTargetName(string faction, int squadId)
    {
        var squads = _squadSystem.GetFactionSquads(faction);
        return squads.TryGetValue(squadId, out var squadInfo)
            ? squadInfo.Name
            : Loc.GetString("overwatch-announcement-target-squad");
    }

    /// <summary>
    /// Handles switching the view to the target's camera.
    /// </summary>
    private void OnViewCamera(Entity<OverwatchConsoleComponent> ent, ref OverwatchViewCameraMessage args)
    {
        if (args.Actor is not { Valid: true } actor || !IsAuthorized(ent, actor))
            return;

        var target = GetEntity(args.Target);
        if (!TryComp<HullrotFactionComponent>(target, out var factionComp) ||
            factionComp.Faction != ent.Comp.Faction)
            return;

        // Mirrors OverwatchMemberData.HasCamera. The client greys the button for the dead; this is the
        // check that actually holds, since the button is not a security boundary.
        if (GetMemberStatus(target) == OverwatchMemberStatus.Dead)
            return;

        if (!TryComp<ActorComponent>(actor, out var actorComp) || actorComp.PlayerSession == null)
            return;

        if (_watchingPairs.TryGetValue(actor, out var currentTarget))
        {
            if (currentTarget == target)
                return;

            if (TryComp<RatOverwatchWatchingComponent>(actor, out var watchingComp))
            {
                StopWatching(actor, watchingComp);
            }
        }

        var cameraCompTarget = EnsureComp<RatOverwatchCameraComponent>(target);
        cameraCompTarget.Watching.Add(actor);
        Dirty(target, cameraCompTarget);

        var watchingCompActor = EnsureComp<RatOverwatchWatchingComponent>(actor);
        watchingCompActor.Watching = target;
        watchingCompActor.Console = ent.Owner;
        watchingCompActor.Camera = null;
        Dirty(actor, watchingCompActor);

        _watchingPairs[actor] = target;

        _eyeSystem.SetTarget(actor, target);
        _viewSubscriberSystem.AddViewSubscriber(target, actorComp.PlayerSession);
    }

    private void OnStopWatching(Entity<OverwatchConsoleComponent> ent, ref OverwatchStopWatchingMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (TryComp<RatOverwatchWatchingComponent>(actor, out var watchingComp) && watchingComp.Watching.HasValue)
        {
            StopWatching(actor, watchingComp);
        }
    }

    /// <summary>
    /// Stops a player watching their target.
    /// </summary>
    private void StopWatching(EntityUid watcher, RatOverwatchWatchingComponent watchingComp)
    {
        if (!watchingComp.Watching.HasValue)
            return;

        var target = watchingComp.Watching.Value;

        if (watchingComp.Camera is { } camera &&
            TryComp<SurveillanceCameraComponent>(camera, out var cameraComp))
        {
            _cameraSystem.RemoveActiveViewer(camera, watcher, component: cameraComp);
        }

        if (TryComp<RatOverwatchCameraComponent>(target, out var cameraCompTarget))
        {
            cameraCompTarget.Watching.Remove(watcher);
            Dirty(target, cameraCompTarget);
        }

        if (TryComp<ActorComponent>(watcher, out var actorComp) && actorComp.PlayerSession != null)
        {
            _viewSubscriberSystem.RemoveViewSubscriber(target, actorComp.PlayerSession);
        }

        _eyeSystem.SetTarget(watcher, null);
        _watchingPairs.Remove(watcher);
        watchingComp.Watching = null;
        watchingComp.Console = null;
        watchingComp.Camera = null;
        Dirty(watcher, watchingComp);
    }

    private void OnConsoleShutdown(Entity<OverwatchConsoleComponent> ent, ref ComponentShutdown args)
    {
        StopWatchingForConsole(ent.Owner);
    }

    /// <summary>
    /// Handles the watching component being removed.
    /// </summary>
    private void OnWatchingShutdown(Entity<RatOverwatchWatchingComponent> ent, ref ComponentShutdown args)
    {
        if (!ent.Comp.Watching.HasValue)
            return;

        var target = ent.Comp.Watching.Value;

        if (ent.Comp.Camera is { } camera &&
            TryComp<SurveillanceCameraComponent>(camera, out var cameraComp))
        {
            _cameraSystem.RemoveActiveViewer(camera, ent.Owner, component: cameraComp);
        }

        if (TryComp<RatOverwatchCameraComponent>(target, out var cameraCompTarget))
        {
            cameraCompTarget.Watching.Remove(ent.Owner);
            Dirty(target, cameraCompTarget);
        }

        if (TryComp<ActorComponent>(ent.Owner, out var actorComp) && actorComp.PlayerSession != null)
        {
            _viewSubscriberSystem.RemoveViewSubscriber(target, actorComp.PlayerSession);
        }

        _eyeSystem.SetTarget(ent.Owner, null);
        _watchingPairs.Remove(ent.Owner);
        ent.Comp.Console = null;
        ent.Comp.Camera = null;
    }

    /// <summary>
    /// Camera component removed — clears every watcher.
    /// </summary>
    private void OnCameraShutdown(Entity<RatOverwatchCameraComponent> ent, ref ComponentShutdown args)
    {
        foreach (var watcher in ent.Comp.Watching.ToList())
        {
            if (TryComp<RatOverwatchWatchingComponent>(watcher, out var watchingComp))
            {
                StopWatching(watcher, watchingComp);
            }
        }
    }

    /// <summary>
    /// Watched entity removed — clears every watcher.
    /// </summary>
    private void OnWatchedEntityTerminating(Entity<RatOverwatchCameraComponent> ent, ref EntityTerminatingEvent args)
    {
        foreach (var watcher in ent.Comp.Watching.ToList())
        {
            if (TryComp<RatOverwatchWatchingComponent>(watcher, out var watchingComp))
            {
                StopWatching(watcher, watchingComp);
            }
        }
    }

    /// <summary>
    /// Console lost power — closes every active watch.
    /// </summary>
    private void OnPowerChanged(Entity<OverwatchConsoleComponent> ent, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        StopWatchingForConsole(ent.Owner);

        _uiSystem.CloseUi(ent.Owner, OverwatchUiKey.Key);
    }

    private void StopWatchingForConsole(EntityUid console)
    {
        foreach (var (watcher, _) in _watchingPairs.ToList())
        {
            if (TryComp<RatOverwatchWatchingComponent>(watcher, out var watchingComp) &&
                watchingComp.Console == console)
            {
                StopWatching(watcher, watchingComp);
            }
        }
    }

    /// <summary>
    /// Player spawn finished — refreshes the UI of every open console.
    /// </summary>
    private void OnPlayerSpawn(PlayerSpawnCompleteEvent args)
    {
        var query = EntityQueryEnumerator<OverwatchConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (_uiSystem.IsUiOpen(uid, OverwatchUiKey.Key))
            {
                RefreshData((uid, comp));
            }
        }
    }

    /// <inheritdoc/>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _updateTimer += frameTime;
        _cacheInvalidationTimer += frameTime;

        if (_updateTimer < UpdateInterval)
            return;

        _updateTimer -= UpdateInterval;

        var hasOpenUi = false;
        var query = EntityQueryEnumerator<OverwatchConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (_uiSystem.IsUiOpen(uid, OverwatchUiKey.Key))
            {
                hasOpenUi = true;
                RefreshData((uid, comp));
            }
        }

        if (_cacheInvalidationTimer >= CacheInvalidationInterval && hasOpenUi)
        {
            _cacheInvalidationTimer -= CacheInvalidationInterval;
            _factionMembersCache.Clear();
            _memberDataCache.Clear();
        }

        else if (!hasOpenUi)
            _cacheInvalidationTimer = 0;
    }

    /// <summary>
    /// Gets the cached list of faction members.
    /// </summary>
    private List<EntityUid> GetFactionMembers(string faction)
    {
        if (_factionMembersCache.TryGetValue(faction, out var cached))
            return cached;

        var members = new List<EntityUid>();
        var query = EntityQueryEnumerator<HullrotFactionComponent>();
        while (query.MoveNext(out var uid, out var factionComp))
        {
            if (factionComp.Faction == faction)
                members.Add(uid);
        }

        _factionMembersCache[faction] = members;
        return members;
    }

    /// <summary>
    /// Works out a member's status (alive/dead/SSD).
    /// </summary>
    private OverwatchMemberStatus GetMemberStatus(EntityUid member)
    {
        if (!TryComp<MobStateComponent>(member, out var mobState))
            return OverwatchMemberStatus.Dead;

        if (_mobStateSystem.IsDead(member, mobState))
            return OverwatchMemberStatus.Dead;

        if (!TryComp<ActorComponent>(member, out var actor) || actor.PlayerSession == null)
            return OverwatchMemberStatus.SSD;

        return OverwatchMemberStatus.Alive;
    }



    /// <summary>
    /// Gets an entity's job title.
    /// </summary>
    private string GetJobTitle(EntityUid entity)
    {
        if (TryComp<IdCardComponent>(entity, out var idCard) && !string.IsNullOrEmpty(idCard.LocalizedJobTitle))
            return idCard.LocalizedJobTitle;

        if (_idCardSystem.TryFindIdCard(entity, out var foundIdCard) && !string.IsNullOrEmpty(foundIdCard.Comp.LocalizedJobTitle))
            return foundIdCard.Comp.LocalizedJobTitle;

        return Loc.GetString("overwatch-job-title-unknown");
    }

    /// <summary>
    /// Gets an entity's squad ID.
    /// </summary>
    private int? GetSquadId(EntityUid entity)
    {
        if (TryComp<SquadComponent>(entity, out var squadComp))
            return squadComp.SquadId;

        return null;
    }

    /// <summary>
    /// Gets an entity's squad name.
    /// </summary>
    private string GetSquadName(EntityUid entity)
    {
        if (TryComp<SquadComponent>(entity, out var squadComp) && !string.IsNullOrEmpty(squadComp.SquadName))
            return squadComp.SquadName;

        return "";
    }



    /// <summary>
    /// Gets the Overwatch name for a faction.
    /// </summary>
    private string GetOverwatchTitle(string faction)
    {
        var key = faction switch
        {
            "DSM" => "overwatch-title-dsm",
            "NCWL" => "overwatch-title-ncwl",
            "SHI" => "overwatch-title-shi",
            "TAP" => "overwatch-title-tap",
            "TFSC" => "overwatch-title-tfsc",
            "IPM" => "overwatch-title-ipm",
            "SAW" => "overwatch-title-saw",
            "GSC" => "overwatch-title-gsc",
            "CD" => "overwatch-title-cd",
            "SRM" => "overwatch-title-srm",
            _ => "overwatch-title-default"
        };

        return Loc.GetString(key);
    }

    /// <summary>
    /// Gets the Overwatch colour for a faction.
    /// </summary>
    private Color GetOverwatchColor(string faction)
    {
        return faction switch
        {
            "DSM" => Color.FromHex("#8A00C2"),
            "NCWL" => Color.FromHex("#cf8e00"),
            "SHI" => Color.FromHex("#666d66"),
            "TAP" => Color.FromHex("#009c08"),
            "TFSC" => Color.FromHex("#9b0000"),
            "IPM" => Color.FromHex("#9b0000"),
            "SAW" => Color.FromHex("#9b0000"),
            "GSC" => Color.FromHex("#9b0000"),
            "CD" => Color.FromHex("#9b0000"),
            "SRM" => Color.FromHex("#015124"),
            _ => Color.DarkGray
        };
    }

    /// <summary>
    /// A member's position local to the grid they are standing on, plus that grid's name.
    /// </summary>
    /// <remarks>
    /// Deliberately not world position, which is what this used to return. World coordinates are
    /// meaningless side by side once members are on different maps — two people light-years apart could
    /// read as neighbours — and the old version never returned null, so someone in nullspace, in cryo or
    /// inside a container still reported a confident-looking pair of numbers.
    /// </remarks>
    private (Vector2? Coordinates, string LocationName) GetMemberLocation(EntityUid entity)
    {
        if (!TryComp<TransformComponent>(entity, out var xform) || xform.GridUid is not { } grid)
            return (null, string.Empty);

        var local = Vector2.Transform(_transformSystem.GetWorldPosition(xform), _transformSystem.GetInvWorldMatrix(grid));
        return (local, Name(grid));
    }
}
