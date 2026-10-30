using System.Linq;
using System.Numerics;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Maps;
using Content.Server.Salvage;
using Content.Server.Salvage.Magnet;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Server.StationEvents.Components;
using Content.Shared.Coordinates;
using Content.Shared.GameTicking.Components;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.StationEvents.Events;

public sealed class BluespaceErrorRule : StationEventSystem<BluespaceErrorRuleComponent>
{
    [Dependency] private readonly SharedMapSystem _mapManager = default!;
    [Dependency] private readonly MapLoaderSystem _map = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly CargoSystem _cargo = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    private List<(Entity<TransformComponent> Entity, EntityUid MapUid, Vector2 LocalPosition)> _playerMobs = new();

    protected override void Started(EntityUid uid, BluespaceErrorRuleComponent component, GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        // No round map yet (lobby, or the rule spawned on its own): nowhere to send the grid.
        var mapId = GameTicker.DefaultMap;
        if (!_mapManager.MapExists(mapId))
        {
            ForceEndSelf(uid, gameRule);
            return;
        }

        var shuttleMapUid = _mapManager.CreateMap(out var shuttleMap);

        if (!_map.TryLoadGrid(shuttleMap, new ResPath(component.GridPath), out var gridUids))
            return;
        component.GridUid = gridUids.Value.Owner;
        if (component.GridUid is not EntityUid gridUid)
            return;
        component.startingValue = _pricing.AppraiseGrid(gridUid);
        _shuttle.SetIFFColor(gridUid, component.Color);
        var offset = GetSpawnOffset(component);
        var mapUid = _mapManager.GetMap(mapId);
        if (TryComp<ShuttleComponent>(component.GridUid, out var shuttle))
        {
            _shuttle.FTLToCoordinates(gridUid, shuttle, new EntityCoordinates(mapUid, offset), 0f, 0f, 30f);
        }

        // Grids that need to be stations of their own - traders and the like, anything holding a cargo console -
        // name the game map carrying their station config. Everything else stays a plain loot grid.
        var gameProto = component.StationMap?.Id ?? new ResPath(component.GridPath).FilenameWithoutExtension;
        if (_prototypeManager.TryIndex<GameMapPrototype>(gameProto, out var stationProto))
        {
            if (stationProto.Stations.TryGetValue(gameProto, out var stationConfig))
                component.StationUid = _station.InitializeNewStation(stationConfig, new List<EntityUid>(){gridUid});
            else
                Log.Error($"Game map {gameProto} has no station config keyed to its own id, {component.GridPath} will not become a station.");
        }
        else if (component.StationMap != null)
        {
            Log.Error($"Bluespace error rule points at missing game map {gameProto}, {component.GridPath} will not become a station.");
        }


    }

    /// <summary>
    /// Where on the default map the grid drops in. A min/max distance picks a random point on a ring around the
    /// belt centre, which is the only way to guarantee the grid lands clear of the belt and the station cluster -
    /// the min/max box contains the origin, so it can put a grid right on top of them. Otherwise it falls back to
    /// the box.
    /// </summary>
    private Vector2 GetSpawnOffset(BluespaceErrorRuleComponent component)
    {
        if (component.MinDistance is not { } minDistance || component.MaxDistance is not { } maxDistance)
            return _random.NextVector2Box(component.minX, component.minY, component.maxX, component.maxY); // Hullrot - fix random event spawns being only around kal

        if (minDistance > maxDistance)
        {
            Log.Error($"Bluespace error rule for {component.GridPath} has minDistance above maxDistance, spawning at the inner edge.");
            maxDistance = minDistance;
        }

        // Sample the radius by area, otherwise a plain lerp crowds the spawns against the inner edge of the ring.
        var radius = MathF.Sqrt(_random.NextFloat(minDistance * minDistance, maxDistance * maxDistance));
        var angle = _random.NextAngle();

        return angle.RotateVec(new Vector2(radius, 0f));
    }

    protected override void Ended(EntityUid uid, BluespaceErrorRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        // Never got a grid (no round map when it started).
        if (component.GridUid == null)
            return;

        if(!EntityManager.TryGetComponent<TransformComponent>(component.GridUid, out var gridTransform))
        {
            Log.Error("bluespace error objective was missing transform component");
            return;
        }

        if (gridTransform.GridUid is not EntityUid gridUid)
        {
            Log.Error( "bluespace error has no associated grid?");
            return;
        }

        var gridValue = _pricing.AppraiseGrid(gridUid, null);

        var mobQuery = AllEntityQuery<HumanoidAppearanceComponent, MobStateComponent, TransformComponent>();
        _playerMobs.Clear();

        while (mobQuery.MoveNext(out var mobUid, out _, out _, out var xform))
        {
            if (xform.GridUid == null || xform.MapUid == null || xform.GridUid != gridUid)
                continue;

            // Can't parent directly to map as it runs grid traversal.
            _playerMobs.Add(((mobUid, xform), xform.MapUid.Value, _transform.GetWorldPosition(xform)));
            _transform.DetachParentToNull(mobUid, xform);
        }

        // Deletion has to happen before grid traversal re-parents players.
        Del(gridUid);

        foreach (var mob in _playerMobs)
        {
            _transform.SetCoordinates(mob.Entity.Owner, new EntityCoordinates(mob.MapUid, mob.LocalPosition));
        }

        // A station outlives its grid, so without this every trader event leaves a gridless station behind.
        if (component.StationUid is { } stationUid && Exists(stationUid))
            _station.DeleteStation(stationUid);

        var query = EntityQueryEnumerator<StationBankAccountComponent>();
        while(query.MoveNext(out var id, out var bank))
        {
            _cargo.UpdateBankAccount(id, bank,(int) (gridValue * component.RewardFactor) );
        }
    }
}

