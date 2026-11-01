using System.Numerics;
using Content.Client.Gameplay;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Placement;
using Robust.Client.ResourceManagement;
using Robust.Client.State;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Client._Crescent.Territory;

/// <summary>
/// Marks what the construction console's dismantle mode would tear down: the tile under the mouse in red and a label
/// beside the cursor.
/// </summary>
public sealed class PersistentPoiDeconstructOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IPlacementManager _placement = default!;
    [Dependency] private readonly IStateManager _state = default!;

    private static readonly Color Fill = new(1f, 0.2f, 0.2f, 0.25f);
    private static readonly Color Edge = new(1f, 0.3f, 0.3f, 0.9f);

    private readonly SharedMapSystem _maps;
    private readonly SharedTransformSystem _transform;
    private readonly Font _font;

    public override OverlaySpace Space => OverlaySpace.WorldSpace | OverlaySpace.ScreenSpace;

    public PersistentPoiDeconstructOverlay()
    {
        IoCManager.InjectDependencies(this);
        _maps = _entMan.System<SharedMapSystem>();
        _transform = _entMan.System<SharedTransformSystem>();
        _font = new VectorFont(IoCManager.Resolve<IResourceCache>().GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 11);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _placement is PlacementManager { Eraser: true, Hijack: PersistentPoiDeconstructHijack } &&
               _input.MouseScreenPosition.IsValid;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var mouse = _eye.PixelToMap(_input.MouseScreenPosition);
        if (mouse.MapId == MapId.Nullspace || mouse.MapId != args.MapId)
            return;

        var target = _state.CurrentState is GameplayStateBase gameplay ? gameplay.GetClickedEntity(mouse) : null;
        if (!TryGetTile(mouse, target, out var grid, out var mapGrid, out var tile))
            return;

        if (args.Space == OverlaySpace.WorldSpace)
        {
            var handle = args.WorldHandle;
            handle.SetTransform(_transform.GetWorldMatrix(grid));
            var box = Box2.UnitCentered.Translated((tile + Vector2Helpers.Half) * mapGrid.TileSize);
            handle.DrawRect(box, Fill);
            handle.DrawRect(box, Edge, false);
            handle.SetTransform(Matrix3x2.Identity);
            return;
        }

        var text = target is { } entity
            ? Loc.GetString("persistent-poi-deconstruct-cursor", ("target", _entMan.GetComponent<MetaDataComponent>(entity).EntityName))
            : Loc.GetString("persistent-poi-deconstruct-cursor-tile");
        args.ScreenHandle.DrawString(_font, _input.MouseScreenPosition.Position + new Vector2(18f, 14f), text, Edge);
    }

    private bool TryGetTile(MapCoordinates mouse, EntityUid? target, out EntityUid grid, out MapGridComponent mapGrid, out Vector2i tile)
    {
        tile = default;
        mapGrid = default!;
        grid = default;

        if (target is { } entity &&
            _entMan.GetComponent<TransformComponent>(entity).GridUid is { } targetGrid &&
            _entMan.TryGetComponent(targetGrid, out MapGridComponent? targetMapGrid))
        {
            grid = targetGrid;
            mapGrid = targetMapGrid;
            tile = _maps.LocalToTile(grid, mapGrid, _entMan.GetComponent<TransformComponent>(entity).Coordinates);
            return true;
        }

        if (!_maps.TryFindGridAt(mouse, out grid, out var found))
            return false;

        mapGrid = found;
        tile = _maps.WorldToTile(grid, mapGrid, mouse.Position);
        return _maps.TryGetTileRef(grid, mapGrid, tile, out var tileRef) && !tileRef.Tile.IsEmpty;
    }
}
