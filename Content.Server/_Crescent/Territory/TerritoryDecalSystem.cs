using System.Numerics;
using Content.Server.Decals;
using Content.Server.Popups;
using Content.Shared._Crescent.Territory;
using Content.Shared.Decals;
using Content.Shared.GameTicking;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryDecalSystem : EntitySystem
{
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly PersistentPoiBuildSystem _build = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private const int StrokesPerSecond = 12;

    private readonly Dictionary<EntityUid, (TimeSpan Window, int Strokes)> _strokes = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<TerritoryDecalPaintRequestEvent>(OnPaint);
        SubscribeNetworkEvent<TerritoryDecalEraseRequestEvent>(OnErase);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _strokes.Clear());
    }

    private void OnPaint(TerritoryDecalPaintRequestEvent ev, EntitySessionEventArgs args)
    {
        if (!TryGetTile(args.SenderSession.AttachedEntity, ev.Coordinates, out var user, out var grid, out var territory, out var tile))
            return;

        if (GetColor(territory.Owner, ev.Color) is not { } color ||
            ev.Rotation is < 0 or > 3 ||
            !_proto.HasIndex<DecalPrototype>(ev.Decal) ||
            !TerritoryDecals.IsAllowed(_proto, territory.Owner, ev.Decal))
        {
            return;
        }

        if (!TryComp<DecalGridComponent>(grid, out var decals))
            return;

        if (decals.DecalIndex.Count >= TerritoryDecals.MaxPerGrid)
        {
            Deny(user, "territory-decal-grid-full");
            return;
        }

        var existing = _decals.GetDecalsIntersecting(grid, TileBounds(tile), decals);
        foreach (var (_, decal) in existing)
        {
            if (decal.Id == ev.Decal && decal.Color == color && decal.Angle.Equals(Quarter(ev.Rotation)))
                return;
        }

        if (existing.Count >= TerritoryDecals.MaxPerTile)
        {
            Deny(user, "territory-decal-tile-full");
            return;
        }

        _decals.TryAddDecal(ev.Decal, new EntityCoordinates(grid, tile), out _, color, Quarter(ev.Rotation),
            TerritoryDecals.ZIndex, cleanable: false);
    }

    private void OnErase(TerritoryDecalEraseRequestEvent ev, EntitySessionEventArgs args)
    {
        if (!TryGetTile(args.SenderSession.AttachedEntity, ev.Coordinates, out _, out var grid, out _, out var tile))
            return;

        foreach (var (id, _) in _decals.GetDecalsIntersecting(grid, TileBounds(tile)))
        {
            _decals.RemoveDecal(grid, id);
        }
    }

    private bool TryGetTile(
        EntityUid? attached,
        NetCoordinates netCoordinates,
        out EntityUid user,
        out EntityUid grid,
        out PersistentPoiGridComponent territory,
        out Vector2i tile)
    {
        user = default;
        grid = default;
        territory = default!;
        tile = default;

        if (attached is not { } painter || !_build.CanBuild(painter))
            return false;

        user = painter;
        var coordinates = GetCoordinates(netCoordinates);
        var userXform = Transform(painter);

        if (_transform.GetGrid(coordinates) is not { } target ||
            target != userXform.GridUid ||
            !TryComp<MapGridComponent>(target, out var mapGrid) ||
            !TryComp(target, out PersistentPoiGridComponent? held))
        {
            return false;
        }

        var origin = PersistentPoiMapping.GetReachOrigin(EntityManager, painter, TerritoryDecals.Reach, out var reach);
        if (!_transform.InRange(coordinates, origin, reach) || !Throttle(painter))
            return false;

        var indices = _map.TileIndicesFor(target, mapGrid, coordinates);
        if (!_map.TryGetTileRef(target, mapGrid, indices, out var tileRef) || tileRef.IsSpace(_tiles))
            return false;

        grid = target;
        territory = held;
        tile = indices;
        return true;
    }

    private bool Throttle(EntityUid painter)
    {
        var now = _timing.CurTime;
        var (window, strokes) = _strokes.GetValueOrDefault(painter);

        if (now - window >= TimeSpan.FromSeconds(1))
        {
            _strokes[painter] = (now, 1);
            return true;
        }

        if (strokes >= StrokesPerSecond)
            return false;

        _strokes[painter] = (window, strokes + 1);
        return true;
    }

    private Color? GetColor(string? faction, int index)
    {
        return TerritoryDecals.GetPalette(_proto, faction) is { } palette && index >= 0 && index < palette.Colors.Count
            ? palette.Colors[index]
            : null;
    }

    private static Box2 TileBounds(Vector2i tile)
    {
        return new Box2(tile.X - 0.25f, tile.Y - 0.25f, tile.X + 0.75f, tile.Y + 0.75f);
    }

    private static Angle Quarter(int turns)
    {
        return Angle.FromDegrees(90 * turns);
    }

    private void Deny(EntityUid user, string message)
    {
        _popup.PopupEntity(Loc.GetString(message), user, user, PopupType.SmallCaution);
    }
}
