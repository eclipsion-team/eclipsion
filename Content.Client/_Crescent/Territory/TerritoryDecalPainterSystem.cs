using System.Numerics;
using Content.Shared._Crescent.Territory;
using Content.Shared.Decals;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Client._Crescent.Territory;

public sealed class TerritoryDecalPainterSystem : EntitySystem
{
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly InputSystem _inputSystem = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private TerritoryDecalWindow? _window;
    private TerritoryDecalPalettePrototype? _palette;

    private string? _decal;
    private int _color;
    private int _rotation;

    private bool _painting;
    private bool _erasing;
    private (EntityUid Grid, Vector2i Tile)? _lastStroke;

    public bool Active => _window is { IsOpen: true };

    public override void Initialize()
    {
        base.Initialize();

        _overlays.AddOverlay(new TerritoryDecalOverlay(this, _transform, _sprite));

        CommandBinds.Builder
            .Bind(EngineKeyFunctions.EditorPlaceObject, new PointerStateInputCmdHandler(
                (_, _, _) => StartStroke(erase: false),
                (_, _, _) => EndStroke(), true))
            .Bind(EngineKeyFunctions.EditorCancelPlace, new PointerStateInputCmdHandler(
                (_, _, _) => StartStroke(erase: true),
                (_, _, _) => EndStroke(), true))
            .Register<TerritoryDecalPainterSystem>();

        SubscribeLocalEvent<PersistentPoiBuilderComponent, ComponentShutdown>(OnBuilderShutdown);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _overlays.RemoveOverlay<TerritoryDecalOverlay>();
        CommandBinds.Unregister<TerritoryDecalPainterSystem>();
        _window?.Close();
    }

    private void OnBuilderShutdown(Entity<PersistentPoiBuilderComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Owner == _player.LocalEntity)
            _window?.Close();
    }

    public void Open()
    {
        if (_player.LocalEntity is not { } player)
            return;

        var faction = Transform(player).GridUid is { } grid && TryComp<PersistentPoiGridComponent>(grid, out var territory)
            ? territory.Owner
            : PersistentTerritoryFactions.GetTeam(EntityManager, player);

        _palette = TerritoryDecals.GetPalette(_proto, faction);

        if (_window == null)
        {
            _window = new TerritoryDecalWindow(_proto, _sprite);
            _window.DecalPicked += id =>
            {
                _decal = id;
                Refresh();
            };
            _window.ColorPicked += index =>
            {
                _color = index;
                Refresh();
            };
            _window.Rotated += () =>
            {
                _rotation = (_rotation + 1) % 4;
                Refresh();
            };
            _window.OnClose += () =>
            {
                _painting = false;
                _erasing = false;
                _inputSystem.SetEntityContextActive();
            };
        }

        _window.Populate(_palette);
        _color = _palette == null ? 0 : Math.Clamp(_color, 0, Math.Max(0, _palette.Colors.Count - 1));
        Refresh();

        _window.OpenCenteredLeft();
        _input.Contexts.SetActiveContext("editor");
    }

    private void Refresh()
    {
        _window?.Select(_decal, CurrentColor, _rotation);
        _window?.SelectColor(_color);
    }

    public Color CurrentColor =>
        _palette != null && _color < _palette.Colors.Count ? _palette.Colors[_color] : Color.White;

    public Angle CurrentAngle => Angle.FromDegrees(90 * _rotation);

    public DecalPrototype? CurrentDecal =>
        Active && _decal != null && _proto.TryIndex<DecalPrototype>(_decal, out var decal) ? decal : null;

    private bool StartStroke(bool erase)
    {
        if (!Active || (!erase && _decal == null))
            return false;

        _painting = !erase;
        _erasing = erase;
        _lastStroke = null;
        Stroke();
        return true;
    }

    private bool EndStroke()
    {
        if (!Active)
            return false;

        _painting = false;
        _erasing = false;
        return true;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_painting || _erasing)
            Stroke();
    }

    private void Stroke()
    {
        if (!Active || !TryGetHoveredTile(out var grid, out var tile))
            return;

        if (_lastStroke is { } last && last.Grid == grid && last.Tile == tile)
            return;

        _lastStroke = (grid, tile);
        var coordinates = GetNetCoordinates(new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f)));

        if (_erasing)
            RaiseNetworkEvent(new TerritoryDecalEraseRequestEvent(coordinates));
        else if (_decal != null)
            RaiseNetworkEvent(new TerritoryDecalPaintRequestEvent(coordinates, _decal, _color, _rotation));
    }

    public bool TryGetHoveredTile(out EntityUid grid, out Vector2i tile)
    {
        grid = default;
        tile = default;

        var mouse = _eye.PixelToMap(_input.MouseScreenPosition);
        if (mouse.MapId == MapId.Nullspace ||
            !_map.TryFindGridAt(mouse, out var gridUid, out var mapGrid))
        {
            return false;
        }

        grid = gridUid;
        tile = _map.TileIndicesFor(gridUid, mapGrid, mouse);
        return true;
    }

    public bool CanPaint(EntityUid grid, Vector2i tile)
    {
        if (_player.LocalEntity is not { } player ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            Transform(player).GridUid != grid ||
            territory.Owner != PersistentTerritoryFactions.GetTeam(EntityManager, player))
        {
            return false;
        }

        var target = new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f));
        var origin = PersistentPoiMapping.GetReachOrigin(EntityManager, player, TerritoryDecals.Reach, out var reach);
        return _transform.InRange(target, origin, reach);
    }
}

public sealed class TerritoryDecalOverlay : Overlay
{
    private readonly TerritoryDecalPainterSystem _painter;
    private readonly SharedTransformSystem _transform;
    private readonly SpriteSystem _sprite;

    public override OverlaySpace Space => OverlaySpace.WorldSpaceEntities;

    public TerritoryDecalOverlay(TerritoryDecalPainterSystem painter, SharedTransformSystem transform, SpriteSystem sprite)
    {
        _painter = painter;
        _transform = transform;
        _sprite = sprite;
        ZIndex = 1000;
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _painter.Active;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_painter.TryGetHoveredTile(out var grid, out var tile))
            return;

        var handle = args.WorldHandle;
        handle.SetTransform(_transform.GetWorldMatrix(grid));

        var box = Box2.FromDimensions(tile, Vector2.One);
        var allowed = _painter.CanPaint(grid, tile);

        if (_painter.CurrentDecal is { } decal)
        {
            var color = allowed ? _painter.CurrentColor.WithAlpha(0.75f) : Color.Red.WithAlpha(0.6f);
            var rotated = new Box2Rotated(box, _painter.CurrentAngle, box.Center);
            handle.DrawTextureRect(_sprite.Frame0(decal.Sprite), rotated, color);
        }

        handle.DrawRect(box, (allowed ? Color.White : Color.Red).WithAlpha(0.6f), filled: false);
        handle.SetTransform(Matrix3x2.Identity);
    }
}
