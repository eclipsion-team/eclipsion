using System.Linq;
using System.Numerics;
using Content.Shared._Crescent.PoliticalMap;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;

namespace Content.Client._Crescent.PoliticalMap;

public sealed class PoliticalMapControl : Control
{
    private static readonly Color Background = Color.FromHex("#101216");
    private static readonly Color EmptyFill = Color.FromHex("#1c1f26");
    private static readonly Color EmptyHover = Color.FromHex("#2a2e38");
    private static readonly Color EmptyEdge = Color.FromHex("#4f5566");
    private static readonly Color ContestedColor = Color.FromHex("#d9c05a");
    private static readonly Color NeutralAnchor = Color.FromHex("#c8c8d0");
    private static readonly Color LabelColor = Color.FromHex("#dddde3");

    private const float Margin = 10f;

    private static readonly Color CellNameColor = Color.FromHex("#8a8f9c");
    private static readonly Color RuledCellNameColor = Color.FromHex("#e6e6ec");

    private const string ColumnLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private readonly Font _font;
    private PoliticalMapData? _data;
    private PoliticalMapCell? _hovered;
    private readonly Dictionary<(int, int), string> _cellNames = new();

    public Vector2? ViewerPosition { get; set; }

    public event Action<PoliticalMapCell?>? HoveredChanged;

    public PoliticalMapControl()
    {
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
        _font = new VectorFont(IoCManager.Resolve<IResourceCache>()
            .GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    public void SetData(PoliticalMapData? data)
    {
        _data = data;
        NameCells();

        var hovered = _hovered is { } previous
            ? data?.Cells.Find(cell => cell.Q == previous.Q && cell.R == previous.R)
            : null;

        _hovered = hovered;
        HoveredChanged?.Invoke(hovered);
    }

    public string CellName(PoliticalMapCell cell)
    {
        return _cellNames.GetValueOrDefault((cell.Q, cell.R), "?");
    }

    private void NameCells()
    {
        _cellNames.Clear();
        if (_data == null || _data.Cells.Count == 0)
            return;

        var minQ = _data.Cells.Min(cell => cell.Q);
        foreach (var column in _data.Cells.GroupBy(cell => cell.Q))
        {
            var index = column.Key - minQ;
            var letter = index < ColumnLetters.Length ? ColumnLetters.Substring(index, 1) : $"{index + 1}-";

            var number = 1;
            foreach (var cell in column.OrderByDescending(cell => cell.R))
            {
                _cellNames[(cell.Q, cell.R)] = $"{letter}{number++}";
            }
        }
    }

    public Color ColorOf(string? faction)
    {
        if (faction == null || _data == null)
            return NeutralAnchor;

        return _data.FactionColors.TryGetValue(faction, out var color) ? color : NeutralAnchor;
    }

    private bool TryGetTransform(out Vector2 worldMin, out float scale, out Vector2 offset, out float worldMaxY)
    {
        worldMin = default;
        scale = 1f;
        offset = default;
        worldMaxY = 0f;

        if (_data == null || _data.Cells.Count == 0 || PixelSize.X <= 2 * Margin || PixelSize.Y <= 2 * Margin)
            return false;

        var layout = _data.Layout;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        var extent = new Vector2(layout.Radius, layout.RowSpacing / 2f);

        foreach (var cell in _data.Cells)
        {
            var center = layout.CenterOf(cell.Q, cell.R);
            min = Vector2.Min(min, center - extent);
            max = Vector2.Max(max, center + extent);
        }

        var size = max - min;
        var available = new Vector2(PixelSize.X - 2 * Margin, PixelSize.Y - 2 * Margin);
        scale = MathF.Min(available.X / size.X, available.Y / size.Y);
        offset = new Vector2(Margin, Margin) + (available - size * scale) / 2f;
        worldMin = min;
        worldMaxY = max.Y;
        return true;
    }

    private static Vector2 ToScreen(Vector2 world, Vector2 worldMin, float worldMaxY, float scale, Vector2 offset)
    {
        return offset + new Vector2((world.X - worldMin.X) * scale, (worldMaxY - world.Y) * scale);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        handle.DrawRect(new UIBox2(Vector2.Zero, PixelSize), Background);

        if (!TryGetTransform(out var worldMin, out var scale, out var offset, out var worldMaxY))
        {
            var text = Loc.GetString("political-map-no-data");
            var dimensions = handle.GetDimensions(_font, text, UIScale);
            handle.DrawString(_font, (PixelSize - dimensions) / 2f, text, UIScale, LabelColor);
            return;
        }

        var layout = _data!.Layout;
        var corners = new Vector2[6];

        foreach (var cell in _data.Cells)
        {
            layout.Corners(layout.CenterOf(cell.Q, cell.R), corners);
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = ToScreen(corners[i], worldMin, worldMaxY, scale, offset);
            }

            var hovered = _hovered != null && _hovered.Q == cell.Q && _hovered.R == cell.R;
            Color fill;
            Color edge;
            if (cell.Owner != null)
            {
                var color = ColorOf(cell.Owner);
                fill = color.WithAlpha(hovered ? 0.55f : 0.38f);
                edge = color.WithAlpha(0.9f);
            }
            else if (cell.Contested)
            {
                fill = ContestedColor.WithAlpha(hovered ? 0.4f : 0.22f);
                edge = ContestedColor.WithAlpha(0.8f);
            }
            else
            {
                fill = hovered ? EmptyHover : EmptyFill;
                edge = EmptyEdge;
            }

            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, corners, fill);
            handle.DrawPrimitives(DrawPrimitiveTopology.LineLoop, corners, edge);

            if (layout.RowSpacing * scale >= 48f)
            {
                var name = CellName(cell);
                var top = ToScreen(layout.CenterOf(cell.Q, cell.R) + new Vector2(0f, layout.RowSpacing * 0.34f),
                    worldMin, worldMaxY, scale, offset);
                var size = handle.GetDimensions(_font, name, UIScale);
                var nameColor = cell.Owner != null || cell.Contested ? RuledCellNameColor : CellNameColor;
                handle.DrawString(_font, top - size / 2f, name, UIScale, nameColor);
            }
        }

        if (_hovered != null)
        {
            var center = ToScreen(layout.CenterOf(_hovered.Q, _hovered.R), worldMin, worldMaxY, scale, offset);
            layout.Corners(layout.CenterOf(_hovered.Q, _hovered.R), corners);
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = ToScreen(corners[i], worldMin, worldMaxY, scale, offset);
            }

            handle.DrawPrimitives(DrawPrimitiveTopology.LineLoop, corners, Color.White);

            var radius = layout.Radius * scale;
            if (radius > 4f)
            {
                var inset = (radius - 1.5f) / radius;
                for (var i = 0; i < corners.Length; i++)
                {
                    corners[i] = center + (corners[i] - center) * inset;
                }

                handle.DrawPrimitives(DrawPrimitiveTopology.LineLoop, corners, Color.White.WithAlpha(0.8f));
            }
        }

        foreach (var anchor in _data.Anchors)
        {
            var position = ToScreen(anchor.Position, worldMin, worldMaxY, scale, offset);
            if (position.X < 0 || position.Y < 0 || position.X > PixelSize.X || position.Y > PixelSize.Y)
                continue;

            var color = anchor.Owner == null ? NeutralAnchor : ColorOf(anchor.Owner);
            var size = 4f * UIScale;

            if (anchor.Kind == PoliticalMapAnchorKind.Station)
            {
                handle.DrawRect(new UIBox2(position - new Vector2(size), position + new Vector2(size)), color);
                handle.DrawRect(new UIBox2(position - new Vector2(size), position + new Vector2(size)), Color.Black, false);
            }
            else
            {
                var diamond = new[]
                {
                    position + new Vector2(0, -size * 1.3f),
                    position + new Vector2(size * 1.3f, 0),
                    position + new Vector2(0, size * 1.3f),
                    position + new Vector2(-size * 1.3f, 0),
                };
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, diamond, color);
                handle.DrawPrimitives(DrawPrimitiveTopology.LineLoop, diamond, Color.Black);
            }

            var dimensions = handle.GetDimensions(_font, anchor.Name, UIScale);
            handle.DrawString(_font,
                position + new Vector2(-dimensions.X / 2f, size * 1.6f),
                anchor.Name,
                UIScale,
                LabelColor.WithAlpha(0.9f));
        }

        if (ViewerPosition is { } viewer)
        {
            var position = ToScreen(viewer, worldMin, worldMaxY, scale, offset);
            var arm = 7f * UIScale;
            handle.DrawLine(position - new Vector2(arm, 0), position + new Vector2(arm, 0), Color.Cyan);
            handle.DrawLine(position - new Vector2(0, arm), position + new Vector2(0, arm), Color.Cyan);
            handle.DrawCircle(position, arm * 0.6f, Color.Cyan, false);
        }
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);

        PoliticalMapCell? found = null;
        if (TryGetTransform(out var worldMin, out var scale, out var offset, out var worldMaxY))
        {
            var pixel = args.RelativePixelPosition;
            var world = new Vector2(
                worldMin.X + (pixel.X - offset.X) / scale,
                worldMaxY - (pixel.Y - offset.Y) / scale);

            var (q, r) = _data!.Layout.CellAt(world);
            found = _data.Cells.Find(cell => cell.Q == q && cell.R == r);
        }

        SetHovered(found);
    }

    protected override void MouseExited()
    {
        base.MouseExited();
        SetHovered(null);
    }

    private void SetHovered(PoliticalMapCell? cell)
    {
        if (ReferenceEquals(cell, _hovered))
            return;

        _hovered = cell;
        HoveredChanged?.Invoke(cell);
    }
}
