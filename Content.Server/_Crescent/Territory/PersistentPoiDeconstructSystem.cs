using System.Linq;
using Content.Server.Popups;
using Content.Shared._Crescent.Territory;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Item;
using Content.Shared.Maps;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// Tears territory works and floor down from the construction console, paying what built them back into the silo.
/// </summary>
public sealed class PersistentPoiDeconstructSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly PersistentPoiBuildSystem _build = default!;
    [Dependency] private readonly PersistentPoiSystem _poi = default!;
    [Dependency] private readonly PersistentPoiSiloSystem _silo = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TileSystem _tile = default!;

    private static readonly SoundSpecifier DoneSound = new SoundPathSpecifier("/Audio/Items/deconstruct.ogg");

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);
    private const string Effect = "EffectRCDDeconstruct2";
    private const int JobsPerUser = 8;

    private readonly List<Job> _jobs = new();

    private sealed class Job
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
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _jobs.Clear());
    }

    private void OnTileRequest(PersistentPoiDeconstructTileRequestEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is { } user)
            TryStartTearUp(user, GetCoordinates(ev.Location));
    }

    private void OnRequest(PersistentPoiDeconstructRequestEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is { } user && TryGetEntity(ev.Target, out var target))
            TryStartDismantle(user, target.Value);
    }

    public bool TryStartTearUp(EntityUid user, EntityCoordinates location)
    {
        if (!TryGetTile(user, location, true, out var tile))
            return false;

        var centre = _maps.GridTileToLocal(tile.GridUid, Comp<MapGridComponent>(tile.GridUid), tile.GridIndices);
        return Queue(user, null, centre);
    }

    public bool TryStartDismantle(EntityUid user, EntityUid target)
    {
        if (!CanDeconstruct(user, target, true))
            return false;

        if (!Queue(user, target, Transform(target).Coordinates))
            return false;

        _popup.PopupCoordinates(Loc.GetString("persistent-poi-deconstruct-start", ("target", target)),
            Transform(target).Coordinates, user);
        return true;
    }

    private bool TryGetTile(EntityUid user, EntityCoordinates location, bool popup, out TileRef tile)
    {
        tile = default;
        if (!MayWork(user, popup) || !location.IsValid(EntityManager))
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

        return InReach(user, location);
    }

    // only from the construction console now; by hand nothing comes down
    private bool MayWork(EntityUid user, bool popup)
    {
        if (!_build.CanBuild(user))
            return false;

        if (PersistentPoiMapping.IsViewing(EntityManager, user))
            return true;

        if (popup)
            _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-console-only"), user, user, PopupType.SmallCaution);
        return false;
    }

    private bool InReach(EntityUid user, EntityCoordinates location)
    {
        var origin = PersistentPoiMapping.GetReachOrigin(EntityManager, user, 0f, out var reach);
        return _transform.InRange(origin, location, reach);
    }

    public bool CanDeconstruct(EntityUid user, EntityUid target, bool popup)
    {
        if (!MayWork(user, popup) || TerminatingOrDeleted(target) || EntityManager.IsQueuedForDeletion(target))
            return false;

        var grid = Transform(user).GridUid;
        var xform = Transform(target);
        if (target == grid || xform.GridUid != grid || !IsDismantlable(target, xform))
        {
            if (popup)
                _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-invalid"), user, user, PopupType.SmallCaution);
            return false;
        }

        return InReach(user, xform.Coordinates);
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

    private bool Queue(EntityUid user, EntityUid? target, EntityCoordinates location)
    {
        var jobs = 0;
        foreach (var job in _jobs)
        {
            if (job.User == user)
                jobs++;

            if (target != null && job.Target == target || target == null && job.Target == null && job.Location.Equals(location))
                return false;
        }

        if (jobs >= JobsPerUser)
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-deconstruct-busy"), user, user, PopupType.SmallCaution);
            return false;
        }

        _jobs.Add(new Job
        {
            User = user,
            Target = target,
            Location = location,
            ReadyAt = _timing.CurTime + Delay,
            Effect = Spawn(Effect, location),
        });
        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_jobs.Count == 0)
            return;

        var now = _timing.CurTime;
        for (var i = _jobs.Count - 1; i >= 0; i--)
        {
            var job = _jobs[i];
            if (now < job.ReadyAt)
                continue;

            _jobs.RemoveAt(i);
            if (!TerminatingOrDeleted(job.Effect))
                QueueDel(job.Effect);

            // checked again: the ground, the holder or the target may have changed in the meantime
            if (!Exists(job.User))
                continue;

            if (job.Target is { } target)
            {
                if (CanDeconstruct(job.User, target, false))
                    Dismantle(job.User, target);
            }
            else if (job.Location.IsValid(EntityManager) && TryGetTile(job.User, job.Location, false, out var tile))
            {
                TearUp(job.User, tile);
            }
        }
    }

    private void TearUp(EntityUid user, TileRef tile)
    {
        if (!_tile.DeconstructTile(tile, user))
            return;

        _audio.PlayPvs(DoneSound, _maps.GridTileToLocal(tile.GridUid, Comp<MapGridComponent>(tile.GridUid), tile.GridIndices));
        _adminLog.Add(LogType.Construction, LogImpact.Low,
            $"{ToPrettyString(user):user} tore up tile {tile.GridIndices} on territory {ToPrettyString(tile.GridUid)}");
    }

    private void Dismantle(EntityUid user, EntityUid target)
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

        var xform = Transform(target);
        var grid = xform.GridUid;
        var coordinates = xform.Coordinates;
        var name = Name(target);
        var silo = CompOrNull<PersistentPoiMaterialSiloComponent>(target);

        // map-native walls and machines pay back what their own graph took, the same as built works.
        // the silo cannot be paid back into itself
        var refund = _poi.GetRefund(target, reached: true);
        var intoSilo = refund.Count > 0 && silo == null && grid is { } siloGrid && _silo.TryDeposit(siloGrid, refund);
        if (!intoSilo)
            _poi.RefundConstruction(target, reached: true);

        if (silo != null)
            _silo.Spill((target, silo));

        _audio.PlayPvs(DoneSound, coordinates);
        _popup.PopupCoordinates(Loc.GetString(intoSilo ? "persistent-poi-deconstruct-done-silo" : "persistent-poi-deconstruct-done",
                ("target", name),
                ("refund", DescribeRefund(refund))),
            coordinates, user);

        _adminLog.Add(LogType.Construction, LogImpact.Medium,
            $"{ToPrettyString(user):user} dismantled {ToPrettyString(target):target} on territory {ToPrettyString(grid)} from the construction console, refund {DescribeRefund(refund)}{(intoSilo ? " into the silo" : string.Empty)}");

        QueueDel(target);
    }

    private string DescribeRefund(Dictionary<ProtoId<StackPrototype>, int> refund)
    {
        if (refund.Count == 0)
            return Loc.GetString("persistent-poi-deconstruct-refund-none");

        return string.Join(", ", refund.Select(pair => Loc.GetString("persistent-poi-deconstruct-refund-entry",
            ("count", pair.Value),
            ("material", _proto.TryIndex(pair.Key, out var stack) ? Loc.GetString(stack.Name) : pair.Key.Id))));
    }
}
