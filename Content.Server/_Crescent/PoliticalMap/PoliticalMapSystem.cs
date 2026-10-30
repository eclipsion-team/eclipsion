using System.Text;
using Content.Server._Crescent.Territory;
using Content.Shared._Crescent.PoliticalMap;
using Content.Shared._Crescent.Territory;
using Content.Shared.Roles;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.PoliticalMap;

public sealed class PoliticalMapSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly PersistentCaptureRegionSystem _regions = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public static readonly ProtoId<PoliticalMapPrototype> DefaultMap = "Default";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private TimeSpan _sinceRefresh;
    private PoliticalMapData? _data;
    private string? _signature;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<PoliticalMapRequestEvent>(OnRequest);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _sinceRefresh += TimeSpan.FromSeconds(frameTime);
        if (_sinceRefresh < RefreshInterval)
            return;

        _sinceRefresh = TimeSpan.Zero;
        Refresh();
    }

    private void OnRequest(PoliticalMapRequestEvent ev, EntitySessionEventArgs args)
    {
        RaiseNetworkEvent(new PoliticalMapUpdateEvent(GetData()), args.SenderSession);
    }

    public PoliticalMapData GetData()
    {
        _data ??= Build();
        return _data;
    }

    public void Refresh()
    {
        var data = Build();
        var signature = Sign(data);
        _data = data;

        if (signature == _signature)
            return;

        _signature = signature;
        RaiseNetworkEvent(new PoliticalMapUpdateEvent(data), Filter.Broadcast());
    }

    private PoliticalMapData Build()
    {
        var proto = _proto.TryIndex(DefaultMap, out var map) ? map : new PoliticalMapPrototype();
        var data = new PoliticalMapData
        {
            Origin = proto.Origin,
            Size = proto.Size,
            ColumnSpacing = MathF.Max(1f, proto.ColumnSpacing),
            RowSpacing = MathF.Max(1f, proto.RowSpacing),
        };

        var layout = data.Layout;

        var stations = EntityQueryEnumerator<PoliticalMapStationComponent, TransformComponent>();
        while (stations.MoveNext(out var uid, out var station, out var xform))
        {
            if (string.IsNullOrEmpty(station.Faction) || HasComp<PersistentPoiGridComponent>(uid))
                continue;

            data.Anchors.Add(new PoliticalMapAnchor
            {
                Name = PersistentTerritoryFactions.StripOwnerPrefix(Name(uid)),
                Position = _transform.GetWorldPosition(xform),
                Kind = PoliticalMapAnchorKind.Station,
                Owner = station.Faction,
            });
        }

        var regions = EntityQueryEnumerator<PersistentCaptureRegionComponent, TransformComponent>();
        while (regions.MoveNext(out var uid, out var region, out var xform))
        {
            if (!region.ValidRegion || string.IsNullOrEmpty(region.RegionId))
                continue;

            data.Anchors.Add(new PoliticalMapAnchor
            {
                Name = _regions.DisplayName((uid, region)),
                Position = _transform.GetWorldPosition(xform),
                Kind = PoliticalMapAnchorKind.Territory,
                Owner = string.IsNullOrEmpty(region.AppliedOwner) ? null : region.AppliedOwner,
            });
        }

        // stable order, so the same map always signs the same
        data.Anchors.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var anchorsByCell = new Dictionary<(int, int), List<PoliticalMapAnchor>>();
        foreach (var anchor in data.Anchors)
        {
            var cell = layout.CellAt(anchor.Position);
            if (!anchorsByCell.TryGetValue(cell, out var list))
                anchorsByCell[cell] = list = new List<PoliticalMapAnchor>();

            list.Add(anchor);
        }

        foreach (var (q, r) in layout.Cells())
        {
            var cell = new PoliticalMapCell { Q = q, R = r };
            if (anchorsByCell.TryGetValue((q, r), out var anchors))
                Rule(cell, anchors);

            data.Cells.Add(cell);
        }

        foreach (var anchor in data.Anchors)
        {
            if (anchor.Owner is { } owner && !data.FactionColors.ContainsKey(owner))
                data.FactionColors[owner] = FactionColor(proto, owner);
        }

        return data;
    }

    private static void Rule(PoliticalMapCell cell, List<PoliticalMapAnchor> anchors)
    {
        var held = new Dictionary<string, int>();
        foreach (var anchor in anchors)
        {
            if (anchor.Kind == PoliticalMapAnchorKind.Territory && anchor.Owner is { } owner)
                held[owner] = held.GetValueOrDefault(owner) + 1;
        }

        if (held.Count > 0)
        {
            var most = held.Values.Max();
            var leaders = held.Where(pair => pair.Value == most).Select(pair => pair.Key).ToList();
            if (leaders.Count == 1)
                cell.Owner = leaders[0];
            else
                cell.Contested = true;

            return;
        }

        foreach (var anchor in anchors)
        {
            if (anchor.Kind == PoliticalMapAnchorKind.Station && anchor.Owner != null)
            {
                cell.Owner = anchor.Owner;
                return;
            }
        }
    }

    private Color FactionColor(PoliticalMapPrototype map, string faction)
    {
        if (map.FactionColors.TryGetValue(faction, out var color))
            return color;

        if (_proto.TryIndex<FactionPrototype>(faction, out var proto))
            return proto.UiAccent ?? proto.Color;

        return Color.Gray;
    }

    private static string Sign(PoliticalMapData data)
    {
        var builder = new StringBuilder();
        builder.Append($"{data.Origin}|{data.Size}|{data.ColumnSpacing}|{data.RowSpacing};");

        foreach (var cell in data.Cells)
        {
            builder.Append($"{cell.Q},{cell.R},{cell.Owner},{cell.Contested};");
        }

        foreach (var anchor in data.Anchors)
        {
            builder.Append($"{anchor.Name},{anchor.Kind},{anchor.Owner},{MathF.Round(anchor.Position.X / 50f)},{MathF.Round(anchor.Position.Y / 50f)};");
        }

        return builder.ToString();
    }
}
