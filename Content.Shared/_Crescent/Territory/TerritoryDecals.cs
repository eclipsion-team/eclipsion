using Content.Shared.Decals;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Prototype("territoryDecalSet")]
public sealed partial class TerritoryDecalSetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public int Priority;

    [DataField(required: true)]
    public List<ProtoId<DecalPrototype>> Decals = new();
}

[Prototype("territoryDecalPalette")]
public sealed partial class TerritoryDecalPalettePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<Color> Colors = new();

    [DataField]
    public List<ProtoId<DecalPrototype>> Emblems = new();
}

public static class TerritoryDecals
{
    public const string DefaultPalette = "Default";

    public const float Reach = 10f;

    public const int MaxPerTile = 4;

    public const int MaxPerGrid = 4000;

    public const int ZIndex = 2;

    public static TerritoryDecalPalettePrototype? GetPalette(IPrototypeManager prototypes, string? faction)
    {
        if (faction != null && prototypes.TryIndex<TerritoryDecalPalettePrototype>(faction, out var palette))
            return palette;

        return prototypes.TryIndex<TerritoryDecalPalettePrototype>(DefaultPalette, out var fallback) ? fallback : null;
    }

    public static bool IsAllowed(IPrototypeManager prototypes, string? faction, string decal)
    {
        if (GetPalette(prototypes, faction) is { } palette && palette.Emblems.Contains(decal))
            return true;

        foreach (var set in prototypes.EnumeratePrototypes<TerritoryDecalSetPrototype>())
        {
            if (set.Decals.Contains(decal))
                return true;
        }

        return false;
    }
}

[Serializable, NetSerializable]
public sealed class TerritoryDecalPaintRequestEvent(NetCoordinates coordinates, string decal, int color, int rotation)
    : EntityEventArgs
{
    public readonly NetCoordinates Coordinates = coordinates;
    public readonly string Decal = decal;

    public readonly int Color = color;

    public readonly int Rotation = rotation;
}

[Serializable, NetSerializable]
public sealed class TerritoryDecalEraseRequestEvent(NetCoordinates coordinates) : EntityEventArgs
{
    public readonly NetCoordinates Coordinates = coordinates;
}
