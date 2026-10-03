using System.Numerics;
using Content.Server.GameTicking.Rules;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking.Components;
using Content.Shared._Crescent.RoundEnd;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Crescent.RoundEnd;

/// <summary>
/// Loads the Analiesse as a station and jumps her in beside the CMM's seat of power. See
/// <see cref="AnaliesseArrivalRuleComponent"/>.
/// </summary>
public sealed class AnaliesseArrivalRuleSystem : GameRuleSystem<AnaliesseArrivalRuleComponent>
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    protected override void Started(EntityUid uid, AnaliesseArrivalRuleComponent component, GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (AlreadyArrived(uid))
        {
            Log.Warning("Analiesse arrival started again, but she is already in the sector. Ignoring.");
            ForceEndSelf(uid, gameRule);
            return;
        }

        if (!_proto.TryIndex(component.GameMap, out var gameMap)
            || !gameMap.Stations.TryGetValue(component.GameMap, out var stationConfig))
        {
            Log.Error($"Analiesse arrival: gameMap {component.GameMap} is missing or has no station entry of the same id.");
            ForceEndSelf(uid, gameRule);
            return;
        }

        // Load her on a holding map first, then FTL her into the sector so she visibly jumps in.
        var holdingMap = _mapSystem.CreateMap(out var loadMap);
        if (!_mapLoader.TryLoadGrid(loadMap, gameMap.MapPath, out var grid))
        {
            Log.Error($"Analiesse arrival could not load grid {gameMap.MapPath}");
            QueueDel(holdingMap);
            ForceEndSelf(uid, gameRule);
            return;
        }

        component.HoldingMap = holdingMap;

        var gridUid = grid.Value.Owner;
        component.GridUid = gridUid;

        // Registers the station, which is what opens her jobs for latejoin.
        _station.InitializeNewStation(stationConfig, [gridUid]);

        _shuttle.SetIFFColor(gridUid, component.IffColor);
        _shuttle.SetIFFFaction(gridUid, component.IffFaction);

        var target = PickArrivalPoint(component);
        if (TryComp<ShuttleComponent>(gridUid, out var shuttle))
            _shuttle.FTLToCoordinates(gridUid, shuttle, target, 0f, 0f, component.HyperspaceTime);
        else
            _transform.SetCoordinates(gridUid, target); // no FTL drive — just park her
    }

    protected override void ActiveTick(EntityUid uid, AnaliesseArrivalRuleComponent component, GameRuleComponent gameRule,
        float frameTime)
    {
        base.ActiveTick(uid, component, gameRule, frameTime);

        // FTL only moves her off the holding map once the jump actually starts, so it can't be deleted in Started.
        if (component.HoldingMap is not { } holdingMap)
            return;

        if (component.GridUid is { } grid && Exists(grid) && Transform(grid).MapUid == holdingMap)
            return;

        component.HoldingMap = null;
        QueueDel(holdingMap);
    }

    private bool AlreadyArrived(EntityUid self)
    {
        var query = EntityQueryEnumerator<AnaliesseArrivalRuleComponent>();
        while (query.MoveNext(out var other, out var comp))
        {
            if (other != self && comp.GridUid is { } grid && Exists(grid))
                return true;
        }

        return false;
    }

    /// <summary>A random point on the configured ring around the faction's seat of power (or the map origin).</summary>
    private EntityCoordinates PickArrivalPoint(AnaliesseArrivalRuleComponent component)
    {
        var mapUid = _mapSystem.GetMap(GameTicker.DefaultMap);
        var anchor = Vector2.Zero;

        var query = EntityQueryEnumerator<FactionStationComponent, TransformComponent>();
        while (query.MoveNext(out _, out var station, out var xform))
        {
            if (station.Faction != component.NearFaction || xform.MapUid != mapUid)
                continue;

            anchor = _transform.GetWorldPosition(xform);
            break;
        }

        var angle = _random.NextFloat(MathF.Tau);
        var radius = _random.NextFloat(component.MinDistance, component.MaxDistance);
        var offset = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        return new EntityCoordinates(mapUid, anchor + offset);
    }
}
