using System.Numerics;
using Content.Server.Parallax;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Parallax;
using Content.Shared._Crescent.SpaceBiomes;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Content.Server.Station.Components;
using Content.Shared._Crescent.Vessel;

namespace Content.Server._Crescent.SpaceBiomes;

public sealed class SpaceBiomeSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerMan = default!;
    [Dependency] private readonly IPrototypeManager _protMan = default!;
    [Dependency] private readonly TransformSystem _formSys = default!;
    [Dependency] private readonly ParallaxSystem _parallaxSys = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private Dictionary<Vector2, HashSet<EntityUid>> _chunks = new();

    // The chunks each source was registered into. RemoveBiome used to recompute them from the source's position at
    // shutdown, but sources ride on grids that move (and a deleted grid's children can already be detached), so it
    // missed the original chunks and left the deleted uid behind. Update then threw on it every 15 seconds, which
    // aborted biome updates for every player on the server.
    private readonly Dictionary<EntityUid, List<Vector2>> _sourceChunks = new();
    private float _updTimer;

    //if false, biomes will only be selected by chunks and not by their actual distance to the player
    private const bool PreciseRange = true;
    private const int ChunkSize = 1000; //in meters
    private const float UpdateInterval = 15; //in seconds

    private ISawmill _sawmill = default!; //used for logging | .2 2025

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SpaceBiomeSourceComponent, ComponentInit>(OnSourceInit);
        SubscribeLocalEvent<SpaceBiomeSourceComponent, ComponentShutdown>(OnSourceShutdown);
        SubscribeLocalEvent<SpaceBiomeTrackerComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRestart);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn);
        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("spacebiomes");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _updTimer += frameTime;
        if (_updTimer < UpdateInterval)
            return;
        _updTimer = 0;

        foreach (ICommonSession session in _playerMan.Sessions)
        {
            if (session.AttachedEntity == null)
                continue;

            var playerXform = Transform(session.AttachedEntity.Value);
            Vector2 playerPos = _formSys.GetWorldPosition(playerXform);
            SpaceBiomeTrackerComponent tracker = EnsureComp<SpaceBiomeTrackerComponent>(session.AttachedEntity.Value);

            HashSet<EntityUid> sourceUids = new();
            if (_chunks.TryGetValue((playerPos / ChunkSize).Floored() * ChunkSize, out var uids))
                sourceUids = uids;

            SpaceBiomeSourceComponent? newSource = null;
            foreach (EntityUid sourceUid in sourceUids)
            {
                // Chunks are keyed by world position only; a source on another map shares
                // coordinates but must not leak its biome (e.g. Gliess's planet on a loadgrid map).
                if (!TryComp<SpaceBiomeSourceComponent>(sourceUid, out var source)
                    || TerminatingOrDeleted(sourceUid)
                    || Transform(sourceUid).MapID != playerXform.MapID)
                {
                    continue;
                }

                if (PreciseRange && (_formSys.GetWorldPosition(sourceUid) - playerPos).Length() > source.SwapDistance)
                    continue;

                if (newSource == null ||
                    source.Priority > newSource.Priority ||
                    source.Priority == newSource.Priority && source == tracker.Source)
                {
                    newSource = source;
                }
            }

            var newBiome = newSource?.Biome ?? "default";

            // Two sources can carry the same biome (Tatsumoto has a short and a long one); walking from
            // one into the other should not replay the parallax fade and the zone banner.
            if (tracker.Biome == newBiome)
            {
                tracker.Source = newSource;
                continue;
            }

            tracker.Source = newSource;
            tracker.Biome = newBiome;
            Dirty(session.AttachedEntity.Value, tracker);
            SwapBiome(session, session.AttachedEntity.Value, newSource);
        }
    }

    private void OnRestart(RoundRestartCleanupEvent ev)
    {
        _chunks.Clear();
        _sourceChunks.Clear();
    }

    private void OnSourceInit(Entity<SpaceBiomeSourceComponent> uid, ref ComponentInit args)
    {
        AddBiome(uid, uid.Comp);
    }

    private void OnSourceShutdown(Entity<SpaceBiomeSourceComponent> uid, ref ComponentShutdown args)
    {
        RemoveBiome(uid, uid.Comp);
    }

    /// <summary>
    /// HULLROT: This specifically makes the station's designation show up 10 seconds after you spawn in. This is exclusively for music, and to show cool title at the top of ur screen.
    /// </summary>
    /// <param name="args"></param>
    private void OnPlayerSpawn(PlayerSpawnCompleteEvent args)
    {

        _sawmill.Debug("PLAYER SPAWN EVENT RAN!!!! STATION:" + args.Station);
        var uid = args.Mob;

        if (!TryComp<ActorComponent>(uid, out var actor))
            return;

        var parentStation = _stationSystem.GetOwningStation(uid);

        if (parentStation == null)
            return;

        // HULLROT EDIT: BoringStations and keeping track of what we've visited before is removed
        // because we want people to see the message each time you enter, coupled with music and flavor text

        if (!TryComp<VesselDesignationComponent>(parentStation, out var desig) || !TryComp<StationNameSetupComponent>(parentStation, out var setup))
            return;

        var description = ""; //fallback if shuttle/station has no description

        if (TryComp<VesselDescriptionComponent>(parentStation, out var desc)) //if this succeeds, we have a description! if it fails,
            description = desc.Description;                                   //the component is missing and we just keep ""

        var musicPrototype = "";

        if (TryComp<VesselMusicComponent>(parentStation, out var music)) //if this succeeds, we have custom music! if it fails,
            musicPrototype = music.AmbientMusicPrototype;                                   //the component is missing and we just keep ""

        var name = setup.StationNameTemplate.Replace("{1}", "").Trim();

        // capture the session now: if the player detaches (ghosting, disconnect) before the
        // timer fires, ActorComponent.PlayerSession is nulled out and reading it would throw.
        var session = actor.PlayerSession;

        Timer.Spawn(TimeSpan.FromSeconds(10), () =>
        {
            if (session.Status == SessionStatus.Disconnected)
                return;

            NewVesselEnteredMessage message = new NewVesselEnteredMessage(name, Loc.GetString(desig.Designation), description, musicPrototype);
            RaiseNetworkEvent(message, session);
        });
    }

    private void OnParentChanged(EntityUid uid, SpaceBiomeTrackerComponent component, EntParentChangedMessage args)
    {
        if (!TryComp<ActorComponent>(uid, out var actor))
            return;

        var parentStation = _stationSystem.GetOwningStation(uid);

        if (parentStation == null)
            return;

        // HULLROT EDIT: BoringStations and keeping track of what we've visited before is removed
        // because we want people to see the message each time you enter, coupled with music and flavor text

        if (!TryComp<VesselDesignationComponent>(parentStation, out var desig) || !TryComp<StationNameSetupComponent>(parentStation, out var setup))
            return;

        var description = ""; //fallback if shuttle/station has no description

        if (TryComp<VesselDescriptionComponent>(parentStation, out var desc)) //if this succeeds, we have a description! if it fails,
            description = desc.Description;                                   //the component is missing and we just keep ""

        var musicPrototype = "";

        if (TryComp<VesselMusicComponent>(parentStation, out var music)) //if this succeeds, we have custom music! if it fails,
            musicPrototype = music.AmbientMusicPrototype;                                   //the component is missing and we just keep ""

        var name = setup.StationNameTemplate.Replace("{1}", "").Trim();

        NewVesselEnteredMessage message = new NewVesselEnteredMessage(name, Loc.GetString(desig.Designation), description, musicPrototype);
        RaiseNetworkEvent(message, actor.PlayerSession);
    }

    public void AddBiome(EntityUid uid, SpaceBiomeSourceComponent source)
    {
        RemoveBiome(uid, source);

        var covered = GetCoveredChunks(_formSys.GetWorldPosition(uid), source.SwapDistance);
        foreach (Vector2 chunkPos in covered)
        {
            if (!_chunks.TryGetValue(chunkPos, out var uids))
                _chunks[chunkPos] = uids = new();
            uids.Add(uid);
        }

        _sourceChunks[uid] = covered;
    }

    public void RemoveBiome(EntityUid uid, SpaceBiomeSourceComponent source)
    {
        if (!_sourceChunks.Remove(uid, out var covered))
            return;

        foreach (Vector2 chunkPos in covered)
        {
            if (!_chunks.TryGetValue(chunkPos, out var uids))
                continue;

            uids.Remove(uid);
            if (uids.Count == 0)
                _chunks.Remove(chunkPos);
        }
    }

    private void SwapBiome(ICommonSession session, EntityUid uid, SpaceBiomeSourceComponent? source)
    {
        EntityUid? mapUid = _formSys.GetMap(session.AttachedEntity ?? EntityUid.Invalid);
        if (mapUid == null)
            return;

        SpaceBiomePrototype biome = _protMan.Index<SpaceBiomePrototype>(source?.Biome ?? "default");
        _parallaxSys.SwapParallax(uid, EnsureComp<ParallaxComponent>(uid), biome.Parallax, biome.SwapDuration);

        SpaceBiomeSwapMessage msg = new() { Biome = source?.Biome ?? "default" };
        RaiseNetworkEvent(msg, session);
    }

    private List<Vector2> GetCoveredChunks(Vector2 pos, int radius)
    {
        List<Vector2> result = new();
        Vector2 posFloor = (pos / ChunkSize).Floored() * ChunkSize;

        int chunks = (radius + ChunkSize - 1) / ChunkSize; //ceil of int division
        for (int y = -chunks; y <= chunks; y++)
        {
            for (int x = -chunks; x <= chunks; x++)
            {
                Vector2 chunkPos = new Vector2(x * ChunkSize, y * ChunkSize) + posFloor;
                if (CrescentHelpers.RectCircleIntersect(
                    new Box2(chunkPos, chunkPos + new Vector2(ChunkSize)),
                    pos,
                    radius))
                {
                    result.Add(chunkPos);
                }
            }
        }

        return result;
    }

    public void RegenerateChunks()
    {
        _chunks.Clear();
        _sourceChunks.Clear();
        var query = EntityQueryEnumerator<SpaceBiomeSourceComponent>();

        while (query.MoveNext(out var uid, out var source))
        {
            AddBiome(uid, source);
        }
    }
}
