using System.Linq;
using Robust.Shared.Serialization;

namespace Content.Shared.Cargo;

// filtered and trimmed by the server: a mainframe resends every tab's state whenever one changes, and the
// whole price table was close to a megabyte going out every second to everyone nearby
[Serializable, NetSerializable]
public sealed class EconomicConsoleBuiState : BoundUserInterfaceState
{
    public const int MaxItems = 50;

    public List<ItemPriceData> Items;

    /// <summary>
    /// Items matching the search, including the ones past <see cref="MaxItems"/>.
    /// </summary>
    public int MatchingItems;

    public List<ShuttlePriceInfoData> Shuttles;

    public int Tracked;

    public EconomicConsoleBuiState(List<ItemPriceData> items, int matchingItems, List<ShuttlePriceInfoData> shuttles, int tracked)
    {
        Items = items;
        MatchingItems = matchingItems;
        Shuttles = shuttles;
        Tracked = tracked;
    }

    // lets the server skip an unchanged state and the client skip the rebuild
    public override bool Equals(object? obj)
    {
        return obj is EconomicConsoleBuiState other &&
               MatchingItems == other.MatchingItems &&
               Tracked == other.Tracked &&
               Items.SequenceEqual(other.Items) &&
               Shuttles.SequenceEqual(other.Shuttles);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(MatchingItems, Tracked, Items.Count, Shuttles.Count);
    }
}

[Serializable, NetSerializable]
public sealed class EconomicConsoleSearchMessage(string search) : BoundUserInterfaceMessage
{
    public const int MaxLength = 64;

    public readonly string Search = search;
}

[Serializable, NetSerializable]
public record struct ItemPriceData(
    string ItemId,
    string LocalizedName,
    double BasePrice,
    double CurrentPrice,
    float Multiplier
);

[Serializable, NetSerializable]
public record struct ShuttlePriceInfoData(
    string Name,
    int BasePrice,
    int CurrentPrice,
    float PriceChange
);
