using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Systems;
using Content.Server.Storage.Components;
using Content.Shared._Crescent.Territory;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.Fluids.Components;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Item;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Projectiles;
using Content.Shared.Storage;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Spawners;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiSaveSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly FollowerSystem _follower = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    [Dependency] private readonly TerritoryProfileSystem _profiles = default!;

    private ResPath SaveDirectory => _profiles.PathFor("persistent_pois");

    private ResPath IndexPath => SaveDirectory / "index.json";
    private const int IndexVersion = 1;

    private readonly Dictionary<string, PersistentPoiIndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    private bool _serializing;

    private bool _savedThisRound;

    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

    public override void Initialize()
    {
        base.Initialize();

        LoadIndex();
        _mapLoader.OnIsSerializable += OnIsSerializable;

        SubscribeLocalEvent<TerritoryProfileChangedEvent>(OnProfileChanged);

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _mapLoader.OnIsSerializable -= OnIsSerializable;
    }

    #region Loading

    public bool TryLoad(
        MapId map,
        string key,
        int version,
        ResPath mapPath,
        Vector2 position,
        [NotNullWhen(true)] out EntityUid? grid)
    {
        grid = null;
        var savePath = GetSavePath(key);

        if (_resources.UserData.Exists(savePath))
        {
            switch (TryLoadSave(map, key, version, savePath, position, out var saved))
            {
                case SaveLoadResult.Loaded when saved != null:
                    grid = saved;
                    return true;

                case SaveLoadResult.Superseded:
                    Log.Info($"Persistent territory '{key}' was saved against an older version of its map; it starts " +
                             "over from the new one.");
                    Archive(key, "superseded");
                    break;

                case SaveLoadResult.Failed:
                    Log.Error($"Persistent territory '{key}' could not be loaded from its save and starts over from " +
                              "its map file. The save was set aside for inspection.");
                    Archive(key, "broken");
                    break;
            }
        }

        if (!_mapLoader.TryLoadGrid(map, mapPath, out var fresh, offset: position))
            return false;

        grid = fresh.Value.Owner;
        var persistent = EnsureComp<PersistentPoiSaveComponent>(grid.Value);
        persistent.Key = key;
        persistent.MapVersion = version;
        persistent.NativesStamped = false;

        if (_map.IsInitialized(map))
            StampNatives(grid.Value, persistent);

        return true;
    }

    private enum SaveLoadResult : byte
    {
        Loaded,
        Superseded,
        Failed,
    }

    private SaveLoadResult TryLoadSave(
        MapId map,
        string key,
        int version,
        ResPath savePath,
        Vector2 position,
        out EntityUid? grid)
    {
        grid = null;

        var options = DeserializationOptions.Default with
        {
            LogInvalidEntities = false,
        };

        EntityUid loadedGrid;
        try
        {
            if (!_mapLoader.TryLoadGrid(map, savePath, out var loaded, options))
                return SaveLoadResult.Failed;

            loadedGrid = loaded.Value.Owner;
        }
        catch (Exception e)
        {
            Log.Error($"Exception while loading persistent territory '{key}': {e}");
            return SaveLoadResult.Failed;
        }

        var persistent = EnsureComp<PersistentPoiSaveComponent>(loadedGrid);
        if (persistent.MapVersion != version)
        {
            Del(loadedGrid);
            return SaveLoadResult.Superseded;
        }

        persistent.Key = key;

        persistent.NativesStamped = true;

        if (_map.TryGetMap(map, out var mapUid))
            _transform.SetCoordinates(loadedGrid, new EntityCoordinates(mapUid.Value, position));

        var ev = new PersistentPoiGridLoadedEvent(loadedGrid);
        RaiseLocalEvent(ref ev);

        grid = loadedGrid;
        Log.Info($"Persistent territory '{key}' loaded from its save.");
        return SaveLoadResult.Loaded;
    }

    public void MarkPersistent(EntityUid grid, string key, int version = 1)
    {
        var persistent = EnsureComp<PersistentPoiSaveComponent>(grid);
        persistent.Key = key;
        persistent.MapVersion = version;
        StampNatives(grid, persistent);
    }

    private void OnRoundStarted(RoundStartedEvent ev)
    {
        var query = EntityQueryEnumerator<PersistentPoiSaveComponent>();
        while (query.MoveNext(out var uid, out var persistent))
        {
            if (!persistent.NativesStamped)
                StampNatives(uid, persistent);
        }
    }

    private void StampNatives(EntityUid grid, PersistentPoiSaveComponent persistent)
    {
        var stack = new Stack<EntityUid>();
        stack.Push(grid);

        while (stack.TryPop(out var uid))
        {
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                EnsureComp<PersistentPoiNativeComponent>(child);
                stack.Push(child);
            }
        }

        persistent.NativesStamped = true;
    }

    #endregion

    #region Saving

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        switch (ev.New)
        {
            case GameRunLevel.InRound:
                _savedThisRound = false;
                _held.Clear();
                break;

            case GameRunLevel.PostRound:
                SaveAll();
                break;
        }
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        if (!_savedThisRound)
            SaveAll();
    }

    public int SaveAll()
    {
        _savedThisRound = true;

        var grids = new List<Entity<PersistentPoiSaveComponent>>();
        var query = EntityQueryEnumerator<PersistentPoiSaveComponent>();
        while (query.MoveNext(out var uid, out var persistent))
        {
            grids.Add((uid, persistent));
        }

        var saved = 0;
        foreach (var grid in grids)
        {
            if (_held.Contains(grid.Comp.Key))
            {
                Log.Info($"Persistent territory '{grid.Comp.Key}' was reset or rolled back this round; its round is not saved.");
                continue;
            }

            if (TrySave(grid))
                saved++;
        }

        return saved;
    }

    public bool IsHeld(string key)
    {
        return _held.Contains(key);
    }

    public bool TrySave(Entity<PersistentPoiSaveComponent> grid)
    {
        if (TerminatingOrDeleted(grid) || string.IsNullOrWhiteSpace(grid.Comp.Key))
            return false;

        var key = grid.Comp.Key;

        if (!grid.Comp.NativesStamped)
        {
            Log.Warning($"Persistent territory '{key}' reached its save without its natives marked; marking them now.");
            StampNatives(grid, grid.Comp);
        }

        try
        {
            PrepareForSave(grid);
        }
        catch (Exception e)
        {
            Log.Error($"Exception while clearing persistent territory '{key}' for saving: {e}");
        }

        var userData = _resources.UserData;
        var temporary = SaveDirectory / $"{key}.tmp.yml";
        var current = GetSavePath(key);
        var previous = GetBackupPath(key);

        var options = SerializationOptions.Default with
        {
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
        };

        bool written;
        _serializing = true;
        try
        {
            written = _mapLoader.TrySaveGrid(grid, temporary, options);
        }
        finally
        {
            _serializing = false;
        }

        try
        {
            if (!written)
            {
                Log.Error($"Persistent territory '{key}' could not be saved; its previous save stays in place.");
                if (userData.Exists(temporary))
                    userData.Delete(temporary);

                return false;
            }

            if (userData.Exists(current))
            {
                if (userData.Exists(previous))
                    userData.Delete(previous);

                userData.Rename(current, previous);
            }

            userData.Rename(temporary, current);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to move the save of persistent territory '{key}' into place: {e}");
            return false;
        }

        _index[key] = new PersistentPoiIndexEntry
        {
            MapVersion = grid.Comp.MapVersion,
            RegionId = CompOrNull<PersistentPoiGridComponent>(grid)?.RegionId,
            RoundId = _ticker.RoundId,
            SavedAt = DateTime.UtcNow,
        };
        SaveIndex();

        Log.Info($"Persistent territory '{key}' saved.");
        return true;
    }

    private void PrepareForSave(EntityUid grid)
    {
        _docking.UndockDocks(grid);

        var playerBound = new List<EntityUid>();
        var discard = new List<EntityUid>();
        var followed = new List<EntityUid>();

        var stack = new Stack<EntityUid>();
        stack.Push(grid);

        while (stack.TryPop(out var uid))
        {
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (IsPlayerBound(child))
                {
                    playerBound.Add(child);
                    continue;
                }

                if (ShouldDiscard(child))
                {
                    discard.Add(child);
                    continue;
                }

                if (HasComp<FollowedComponent>(child))
                    followed.Add(child);

                stack.Push(child);
            }
        }

        foreach (var uid in followed)
        {
            _follower.StopAllFollowers(uid);
        }

        foreach (var uid in playerBound)
        {
            if (TryComp<PullableComponent>(uid, out var pullable) && pullable.BeingPulled)
                _pulling.TryStopPull(uid, pullable);

            if (TryComp<PullerComponent>(uid, out var puller) && puller.Pulling is { } pulled)
                _pulling.TryStopPull(pulled);

            if (TryComp<BuckleComponent>(uid, out var buckle) && buckle.Buckled)
                _buckle.Unbuckle((uid, buckle), null);

            if (_container.IsEntityInContainer(uid))
                _container.TryRemoveFromContainer(uid, force: true);
        }

        foreach (var uid in discard)
        {
            if (!TerminatingOrDeleted(uid))
                Del(uid);
        }
    }

    private bool IsPlayerBound(EntityUid uid)
    {
        return HasComp<ActorComponent>(uid) ||
               HasComp<GhostComponent>(uid) ||
               TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind;
    }

    private bool ShouldDiscard(EntityUid uid)
    {
        if (HasComp<ProjectileComponent>(uid) || HasComp<TimedDespawnComponent>(uid))
            return true;

        var native = HasComp<PersistentPoiNativeComponent>(uid);

        // built turrets carry MobState too
        if (TryComp<MobStateComponent>(uid, out var mobState))
            return !native && !HasComp<PersistentPoiStructureComponent>(uid) || _mobState.IsDead(uid, mobState);

        if (native)
            return false;

        if (HasComp<PuddleComponent>(uid))
            return true;

        if (_container.TryGetContainingContainer((uid, null, null), out var container))
        {
            return HasComp<EntityStorageComponent>(container.Owner) ||
                   HasComp<StorageComponent>(container.Owner) ||
                   HasComp<PersistentPoiFurnishingComponent>(container.Owner) && HasComp<ItemComponent>(uid);
        }

        return !Transform(uid).Anchored && !HasComp<PersistentPoiFurnishingComponent>(uid);
    }

    private void OnIsSerializable(Entity<MetaDataComponent> ent, ref bool serializable)
    {
        if (_serializing && serializable && IsPlayerBound(ent))
            serializable = false;
    }

    #endregion

    #region Administration

    public List<(string Key, PersistentPoiIndexEntry? Entry, bool Loaded)> GetStatus()
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var query = EntityQueryEnumerator<PersistentPoiSaveComponent>();
        while (query.MoveNext(out _, out var persistent))
        {
            loaded.Add(persistent.Key);
        }

        var rows = new List<(string, PersistentPoiIndexEntry?, bool)>();
        foreach (var key in _index.Keys.Union(loaded, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add((key, _index.GetValueOrDefault(key), loaded.Contains(key)));
        }

        return rows;
    }

    public bool Reset(string key)
    {
        if (!_index.ContainsKey(key) && !_resources.UserData.Exists(GetSavePath(key)) && !IsLoaded(key))
            return false;

        Archive(key, "reset");
        _held.Add(key);
        return true;
    }

    public bool Rollback(string key)
    {
        var userData = _resources.UserData;
        var current = GetSavePath(key);
        var previous = GetBackupPath(key);

        if (!userData.Exists(previous))
            return false;

        try
        {
            if (userData.Exists(current))
                userData.Rename(current, GetArchivePath(key, "rolledback"));

            userData.Rename(previous, current);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to roll back persistent territory '{key}': {e}");
            return false;
        }

        _held.Add(key);
        return true;
    }

    public int ResetAll()
    {
        var keys = GetStatus().Select(row => row.Key).ToList();
        foreach (var key in keys)
        {
            Reset(key);
        }

        return keys.Count;
    }

    public string? FindKey(string regionId)
    {
        regionId = regionId.Trim();

        var query = EntityQueryEnumerator<PersistentPoiSaveComponent, PersistentPoiGridComponent>();
        while (query.MoveNext(out _, out var persistent, out var territory))
        {
            if (string.Equals(territory.RegionId, regionId, StringComparison.OrdinalIgnoreCase))
                return persistent.Key;
        }

        foreach (var (key, entry) in _index)
        {
            if (string.Equals(entry.RegionId, regionId, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return null;
    }

    private void OnProfileChanged(ref TerritoryProfileChangedEvent args)
    {
        _held.Clear();
        LoadIndex();
    }

    private bool IsLoaded(string key)
    {
        var query = EntityQueryEnumerator<PersistentPoiSaveComponent>();
        while (query.MoveNext(out _, out var persistent))
        {
            if (string.Equals(persistent.Key, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    #endregion

    #region Files

    private ResPath GetSavePath(string key) => SaveDirectory / $"{key}.yml";

    private ResPath GetBackupPath(string key) => SaveDirectory / $"{key}.bak.yml";

    private ResPath GetArchivePath(string key, string reason)
    {
        return SaveDirectory / $"{key}.{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.yml";
    }

    private void Archive(string key, string reason)
    {
        var userData = _resources.UserData;
        var current = GetSavePath(key);

        try
        {
            if (userData.Exists(current))
                userData.Rename(current, GetArchivePath(key, reason));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to set aside the save of persistent territory '{key}': {e}");
        }

        if (_index.Remove(key))
            SaveIndex();
    }

    private void LoadIndex()
    {
        _index.Clear();

        try
        {
            if (!_resources.UserData.Exists(IndexPath) || !_resources.UserData.TryReadAllText(IndexPath, out var json))
                return;

            var data = JsonSerializer.Deserialize<PersistentPoiIndexData>(json);
            if (data is null || data.Version != IndexVersion)
                return;

            foreach (var (key, entry) in data.Pois)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    _index[key.Trim()] = entry;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load the persistent territory index: {e}");
        }
    }

    private void SaveIndex()
    {
        try
        {
            var data = new PersistentPoiIndexData { Version = IndexVersion };
            foreach (var (key, entry) in _index)
            {
                data.Pois[key] = entry;
            }

            _resources.UserData.CreateDir(SaveDirectory);
            _resources.UserData.WriteAllText(IndexPath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save the persistent territory index: {e}");
        }
    }

    #endregion
}

public sealed class PersistentPoiIndexEntry
{
    public int MapVersion { get; set; }

    public string? RegionId { get; set; }

    public int RoundId { get; set; }

    public DateTime SavedAt { get; set; }
}

internal sealed class PersistentPoiIndexData
{
    public int Version { get; set; }

    public Dictionary<string, PersistentPoiIndexEntry> Pois { get; set; } = new();
}
