using System.Numerics;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.PoliticalMap;

[Prototype("politicalMap")]
public sealed partial class PoliticalMapPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public Vector2 Origin = Vector2.Zero;

    [DataField]
    public Vector2 Size = new(10000f, 10000f);

    [DataField]
    public float ColumnSpacing = 2500f;

    [DataField]
    public float RowSpacing = 2500f;

    [DataField]
    public Dictionary<string, Color> FactionColors = new();
}

public readonly record struct PoliticalHexLayout(Vector2 Origin, Vector2 Size, float ColumnSpacing, float RowSpacing)
{
    public float Radius => ColumnSpacing * 2f / 3f;

    public Vector2 CenterOf(int q, int r)
    {
        var shift = (q & 1) != 0 ? RowSpacing / 2f : 0f;
        return Origin + new Vector2(q * ColumnSpacing, r * RowSpacing + shift);
    }

    public void Corners(Vector2 center, Span<Vector2> corners)
    {
        var radius = Radius;
        var half = RowSpacing / 2f;
        corners[0] = center + new Vector2(radius, 0f);
        corners[1] = center + new Vector2(radius / 2f, half);
        corners[2] = center + new Vector2(-radius / 2f, half);
        corners[3] = center + new Vector2(-radius, 0f);
        corners[4] = center + new Vector2(-radius / 2f, -half);
        corners[5] = center + new Vector2(radius / 2f, -half);
    }

    public IEnumerable<(int Q, int R)> Cells()
    {
        var half = Size / 2f;
        var maxQ = (int) MathF.Ceiling(half.X / ColumnSpacing) + 1;
        var maxR = (int) MathF.Ceiling(half.Y / RowSpacing) + 1;
        const float slack = 0.5f;

        for (var q = -maxQ; q <= maxQ; q++)
        {
            for (var r = -maxR; r <= maxR; r++)
            {
                var offset = CenterOf(q, r) - Origin;
                if (MathF.Abs(offset.X) <= half.X + slack && MathF.Abs(offset.Y) <= half.Y + slack)
                    yield return (q, r);
            }
        }
    }

    public (int Q, int R) CellAt(Vector2 position)
    {
        var local = position - Origin;
        var q0 = (int) MathF.Round(local.X / ColumnSpacing);
        var corners = new Vector2[6];

        for (var q = q0 - 1; q <= q0 + 1; q++)
        {
            var shift = (q & 1) != 0 ? RowSpacing / 2f : 0f;
            var r0 = (int) MathF.Round((local.Y - shift) / RowSpacing);
            for (var r = r0 - 1; r <= r0 + 1; r++)
            {
                Corners(CenterOf(q, r), corners);
                if (Contains(corners, position))
                    return (q, r);
            }
        }

        var nearShift = (q0 & 1) != 0 ? RowSpacing / 2f : 0f;
        return (q0, (int) MathF.Round((local.Y - nearShift) / RowSpacing));
    }

    private static bool Contains(ReadOnlySpan<Vector2> polygon, Vector2 point)
    {
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (cross < -0.001f)
                return false;
        }

        return true;
    }
}

[Serializable, NetSerializable]
public enum PoliticalMapAnchorKind : byte
{
    Station,

    Territory,
}

[Serializable, NetSerializable]
public sealed class PoliticalMapAnchor
{
    public string Name = string.Empty;
    public Vector2 Position;
    public PoliticalMapAnchorKind Kind;
    public string? Owner;
}

[Serializable, NetSerializable]
public sealed class PoliticalMapCell
{
    public int Q;
    public int R;

    public string? Owner;

    public bool Contested;
}

[Serializable, NetSerializable]
public sealed class PoliticalMapData
{
    public Vector2 Origin;
    public Vector2 Size;
    public float ColumnSpacing;
    public float RowSpacing;
    public List<PoliticalMapCell> Cells = new();
    public List<PoliticalMapAnchor> Anchors = new();
    public Dictionary<string, Color> FactionColors = new();

    public PoliticalHexLayout Layout => new(Origin, Size, ColumnSpacing, RowSpacing);
}

[Serializable, NetSerializable]
public sealed class PoliticalMapUpdateEvent(PoliticalMapData data) : EntityEventArgs
{
    public readonly PoliticalMapData Data = data;
}

[Serializable, NetSerializable]
public sealed class PoliticalMapRequestEvent : EntityEventArgs;

[Serializable, NetSerializable]
public enum PoliticalMapUiKey : byte
{
    Key,
}
