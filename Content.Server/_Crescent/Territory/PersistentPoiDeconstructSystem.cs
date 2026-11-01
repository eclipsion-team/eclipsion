using Content.Server.Popups;
using Content.Shared._Crescent.Territory;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.DoAfter;
using Content.Shared.Ghost;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Maps;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Tools;
using Content.Shared.Tools.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiDeconstructSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly PersistentPoiBuildSystem _build = default!;
    [Dependency] private readonly PersistentPoiSystem _poi = default!;
    [Dependency] private readonly PersistentPoiSiloSystem _silo = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedToolSystem _tools = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TileSystem _tile = default!;

    private static readonly ProtoId<ToolQualityPrototype> Anchoring = "Anchoring";
    private static readonly ProtoId<ToolQualityPrototype> Prying = "Prying";

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TileDelay = TimeSpan.FromSeconds(2);

    private static readonly SoundSpecifier StartSound = new SoundPathSpecifier("/Audio/Items/crowbar.ogg");
    private static readonly SoundSpecifier DoneSound = new SoundPathSpecifier("/Audio/Items/deconstruct.ogg");

    // from the works console: no tools, an RCD tear-down and the refund straight into the silo
    private static readonly TimeSpan RemoteDelay = TimeSpan.FromSeconds(2);
    private const string RemoteEffect = "EffectRCDDeconstruct2";
    private const int RemoteJobsPerUser = 8;

    private readonly List<RemoteJob> _remote = new();

    private sealed class RemoteJob
    {
        public EntityUid User;
        public EntityUid? Target;
        public EntityCoordinates Location;
        public TimeSpan ReadyAt;
        public EntityUid Effect;
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<PersistentPoiDeconstructRequestEvent>(OnRequest);
        SubscribeNetworkEvent<PersistentPoiDeconstructTileRequestEvent>(OnTileRequest);
        SubscribeLocalEvent<HandsComponent, PersistentPoiDeconstructDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<HandsComponent, PersistentPoiDeconstructTileDoAfterEvent>(OnTileDoAfter);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _remote.Clear());
    }

    private void OnTileRequest(PersistentPoiDeconstructTileRequestEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is { } user)
            TryStartTearUp(user, GetCoordinates(ev.Location));
    }

    public bool TryStartTearUp(EntityUid user, EntityCoordinates location)
    {
        if (!TryGetTile(user, location, true, out var tile))
            return false;

        if (PersistentPoiMapping.IsViewing(EntityManager, user))
        {
            var centre = _maps.GridTileToLocal(tile.GridUid, Comp<MapGridComponent>(tile.GridUid), tile.GridIndices);
            return QueueRemote(user, null, centre);
        }

        var doAfter = new DoAfterArgs(EntityManager, user, TileDelay, new PersistentPoiDeconstructTileDoAfterEvent(GetNetCoordinates(location)), user)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return false;

        _audio.PlayPvs(StartSound, _maps.GridTileToLocal(tile.GridUid, Comp<MapGridComponent>(tile.GridUid), tile.GridIndices));
        return true;
    }

    private void OnTileDoAfter(Entity<HandsComponent> ent, ref PersistentPoiDeconstructTileDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;

        if (TryGetTile(ent, GetCoordinates(args.Location), true, out var tile))
            TearUp(ent, tile);
    }

    private void TearUp(EntityUid user, TileRef tile)
    {
        if (!_tile.DeconstructTile(tile, user))
            return;

        _audio.PlayPvs(DoneSound, _maps.GridTileToLocal(tile.GridUid, Comp<MapGridComponent>(tile.GridUid), tile.GridIndices));
        _adminLog.Add(LogType.Construction, LogImpact.Low,
            $"{ToPrettyString(user):user} tore up tile {tile.GridIndices} on territory {ToPrettyString(tile.GridUid)}");
    }

    private bool TryGetTile(EntityUid user, EntityCoordinates location, bool popup, out TileRef tile)
    {
        tile = default;
        if (!_build.CanBuild(user) || !location.IsValid(EntityManager))
            return false;

        var grid = Transform(user).GridUid;
        if (_transform.GetGrid(location) != grid ||
            !TryComp<MapGridComponent>(grid, out var mapGrid) ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            !_maps.TryGetTileRef(grid.Value, mapGrid, location, out tile) ||
            tile.Tile.IsEmpty)
        {
            return false;
        }

        // the ground round the standard stays, whoever holds it
        if (territory.ProtectsTile(tile.GridIndices))
        {
            if (popup)
                _popup.PopupCoordinates(Loc.GetString("persistent-poi-deconstruct-permanent"), location, user, PopupType.MediumCaution);
            return false;
        }

        return InReach(user, location, popup);
    }

    // by hand it takes the tools and arm's length; from the works console only the view's reach
    private bool InReach(EntityUid user, EntityCoordinates location, bool popup)
    {
        if (PersistentPoiMapping.IsViewing(EntityManager, user))
        {
            var origin = PersistentPoiMapping.GetReachOrigin(EntityManager, user, 0f, out var reach);
            return _transform.InRange(origin, location, reach);
        }

        if (!HasTools(user))
        {
            if (popup)
                _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-tools"), user, user, PopupType.SmallCaution);
            return false;
        }

        return _interaction.InRangeUnobstructed(user, location, popup: popup);
    }

    private void OnRequest(PersistentPoiDeconstructRequestEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is { } user && TryGetEntity(ev.Target, out var target))
            TryStartDismantle(user, target.Value);
    }

    public bool TryStartDismantle(EntityUid user, EntityUid target)
    {
        if (!CanDeconstruct(user, target, true))
            return false;

        if (PersistentPoiMapping.IsViewing(EntityManager, user))
            return QueueRemote(user, target, Transform(target).Coordinates);

        var doAfter = new DoAfterArgs(EntityManager, user, Delay, new PersistentPoiDeconstructDoAfterEvent(), user, target)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return false;

        _audio.PlayPvs(StartSound, target);
        _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-start", ("target", target)), target, user);
        return true;
    }

    private void OnDoAfter(Entity<HandsComponent> ent, ref PersistentPoiDeconstructDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } target)
            return;

        args.Handled = true;

        if (CanDeconstruct(ent, target, true))
            Dismantle(ent, target, false);
    }

    private bool QueueRemote(EntityUid user, EntityUid? target, EntityCoordinates location)
    {
        var jobs = 0;
        foreach (var job in _remote)
        {
            if (job.User == user)
                jobs++;

            if (target != null && job.Target == target || target == null && job.Target == null && job.Location.Equals(location))
                return false;
        }

        if (jobs >= RemoteJobsPerUser)
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-busy"), user, user, PopupType.SmallCaution);
            return false;
        }

        _remote.Add(new RemoteJob
        {
            User = user,
            Target = target,
            Location = location,
            ReadyAt = _timing.CurTime + RemoteDelay,
            Effect = Spawn(RemoteEffect, location),
        });
        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_remote.Count == 0)
            return;

        var now = _timing.CurTime;
        for (var i = _remote.Count - 1; i >= 0; i--)
        {
            var job = _remote[i];
            if (now < job.ReadyAt)
                continue;

            _remote.RemoveAt(i);
            if (!TerminatingOrDeleted(job.Effect))
                QueueDel(job.Effect);

            // checked again: the ground, the holder or the target may have changed in the meantime
            if (!Exists(job.User))
                continue;

            if (job.Target is { } target)
            {
                if (CanDeconstruct(job.User, target, false))
                    Dismantle(job.User, target, true);
            }
            else if (job.Location.IsValid(EntityManager) && TryGetTile(job.User, job.Location, false, out var tile))
            {
                TearUp(job.User, tile);
            }
        }
    }

    private void Dismantle(EntityUid user, EntityUid target, bool intoSilo)
    {
        // an emplacement's gun goes with its hardpoint, so pay back what built the pair
        if (TryComp<PersistentPoiStructureComponent>(target, out var structure) &&
            structure.SpawnedBy is { } parent && !TerminatingOrDeleted(parent))
        {
            target = parent;
        }

        // the gun and its hardpoint finishing in the same tick must not pay out twice
        if (EntityManager.IsQueuedForDeletion(target))
            return;

        var grid = Transform(target).GridUid;
        var built = HasComp<PersistentPoiStructureComponent>(target) || HasComp<PersistentPoiFurnishingComponent>(target);
        var silo = CompOrNull<PersistentPoiMaterialSiloComponent>(target);

        // the silo cannot be paid back into itself
        if (built && (!intoSilo || silo != null || grid is not { } siloGrid || !_silo.TryDeposit(siloGrid, _poi.GetRefund(target, reached: true))))
            _poi.RefundConstruction(target, reached: true);

        if (silo != null)
            _silo.Spill((target, silo));

        _audio.PlayPvs(DoneSound, Transform(target).Coordinates);
        _adminLog.Add(LogType.Construction, LogImpact.Medium,
            $"{ToPrettyString(user):user} dismantled {ToPrettyString(target):target} on territory {ToPrettyString(grid)}{(built ? " (refunded)" : string.Empty)}{(intoSilo ? " from the works console" : string.Empty)}");

        QueueDel(target);
    }

    public bool CanDeconstruct(EntityUid user, EntityUid target, bool popup)
    {
        if (!_build.CanBuild(user) || TerminatingOrDeleted(target) || EntityManager.IsQueuedForDeletion(target))
            return false;

        var grid = Transform(user).GridUid;
        var xform = Transform(target);
        if (target == grid || xform.GridUid != grid || !IsDismantlable(target, xform))
        {
            if (popup)
                _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-invalid"), user, user, PopupType.SmallCaution);
            return false;
        }

        if (PersistentPoiMapping.IsViewing(EntityManager, user))
            return InReach(user, xform.Coordinates, popup);

        if (!HasTools(user))
        {
            if (popup)
                _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-tools"), user, user, PopupType.SmallCaution);
            return false;
        }

        return _interaction.InRangeUnobstructed(user, target, popup: popup);
    }

    private bool IsDismantlable(EntityUid uid, TransformComponent xform)
    {
        if (HasComp<PersistentCaptureRegionComponent>(uid) ||
            HasComp<ActorComponent>(uid) ||
            HasComp<GhostComponent>(uid) ||
            TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind ||
            _container.IsEntityInContainer(uid))
        {
            return false;
        }

        if (HasComp<PersistentPoiStructureComponent>(uid) || HasComp<PersistentPoiFurnishingComponent>(uid))
            return true;

        return xform.Anchored && !HasComp<MobStateComponent>(uid) && !HasComp<ItemComponent>(uid);
    }

    private bool HasTools(EntityUid user)
    {
        var wrench = false;
        var crowbar = false;
        foreach (var held in _hands.EnumerateHeld(user))
        {
            wrench |= _tools.HasQuality(held, Anchoring);
            crowbar |= _tools.HasQuality(held, Prying);
        }

        return wrench && crowbar;
    }
}
