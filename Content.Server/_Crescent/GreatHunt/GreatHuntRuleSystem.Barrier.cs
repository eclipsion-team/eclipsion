using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Shared._Crescent.SpaceArtillery;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Puts up the preparation walls: a square ring of indestructible wall around every home base in
/// <see cref="GreatHuntRuleComponent.BarrierStations"/>, <see cref="GreatHuntRuleComponent.BarrierMargin"/> tiles out.
/// </summary>
/// <remarks>
/// A ship bought during preparation that finds no free dock is set down by FTL proximity, clear of the bounds of
/// every grid near the base. A grid's bounds cover everything inside it, so one ring grid like Unionfall's would
/// count as a huge grid on top of the base and the ship would land outside the wall. Each side is therefore its own
/// one-tile-wide grid, and every side carries <see cref="FTLProximityIgnoreComponent"/> so placement never counts
/// the wall at all: otherwise, once enough ships are parked to reach one side, it drags in the sides touching it and
/// proximity gives up and falls back to the whole map.
/// </remarks>
public sealed partial class GreatHuntRuleSystem
{
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;

    private void BuildBarriers(GreatHuntRuleComponent hunt)
    {
        hunt.BarrierBuilt = true;

        if (hunt.BarrierStations.Count == 0)
            return;

        var tile = new Tile(_tileDefs[hunt.BarrierTile].TileId);
        var walled = new HashSet<string>();

        var query = EntityQueryEnumerator<BecomesStationComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var becomes, out var grid, out var xform))
        {
            if (!hunt.BarrierStations.Contains(becomes.Id) || xform.MapUid is not { } map)
                continue;

            var bounds = _transform.GetWorldMatrix(xform).TransformBox(grid.LocalAABB).Enlarged(hunt.BarrierMargin);
            var left = (int) MathF.Floor(bounds.Left);
            var bottom = (int) MathF.Floor(bounds.Bottom);
            var right = (int) MathF.Ceiling(bounds.Right);
            var top = (int) MathF.Ceiling(bounds.Top);

            // Bottom and top run the full width, corners included; the sides fill the gap between them.
            BuildStrip(hunt, map, new Vector2(left, bottom), right - left + 1, Vector2i.Right, tile);
            BuildStrip(hunt, map, new Vector2(left, top), right - left + 1, Vector2i.Right, tile);
            BuildStrip(hunt, map, new Vector2(left, bottom + 1), top - bottom - 1, Vector2i.Up, tile);
            BuildStrip(hunt, map, new Vector2(right, bottom + 1), top - bottom - 1, Vector2i.Up, tile);

            walled.Add(becomes.Id);
            Log.Info($"Great Hunt preparation wall raised around {ToPrettyString(uid)}.");
        }

        foreach (var station in hunt.BarrierStations)
        {
            if (!walled.Contains(station))
                Log.Error($"Great Hunt could not wall in '{station}': no grid with that BecomesStation id is loaded.");
        }
    }

    /// <summary>One side of a ring: a one-tile-wide grid of wall, <paramref name="length"/> tiles along <paramref name="step"/>.</summary>
    private void BuildStrip(GreatHuntRuleComponent hunt, EntityUid map, Vector2 origin, int length, Vector2i step, Tile tile)
    {
        if (length <= 0)
            return;

        var grid = _map.CreateGridEntity(map);
        _transform.SetWorldPosition(grid, origin);

        var tiles = new List<(Vector2i, Tile)>(length);
        for (var i = 0; i < length; i++)
        {
            tiles.Add((step * i, tile));
        }

        _map.SetTiles(grid, tiles);

        foreach (var (indices, _) in tiles)
        {
            Spawn(hunt.BarrierWall, _map.GridTileToLocal(grid, grid, indices));
        }

        _meta.SetEntityName(grid, Loc.GetString(hunt.BarrierName));
        EnsureComp<GreatHuntGraceBarrierComponent>(grid);
        EnsureComp<FTLProximityIgnoreComponent>(grid);
        EnsureComp<PreventPilotComponent>(grid);
        EnsureComp<BlockShipWeaponProjectileGridComponent>(grid);
        _shuttle.SetIFFColor(grid, hunt.BarrierColor);
        _shuttle.AddIFFFlag(grid, IFFFlags.HideLabel);

        // Every new grid comes up as a free-floating shuttle; park it so nothing can shove a wall out of the way.
        _shuttle.Disable(grid);
    }
}
