using Content.Shared.Cargo;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Cargo.UI;

public sealed class EconomicConsolePanel : BoxContainer
{
    private static readonly Color PriceUp = Color.FromHex("#7ec850");
    private static readonly Color PriceDown = Color.FromHex("#d4634f");

    private readonly Label _statusLabel;
    private readonly LineEdit _searchBox;
    private readonly ScrollContainer _contentScroll;
    private readonly BoxContainer _contentList;

    private EconomicConsoleBuiState? _state;

    // the server does the filtering
    public event Action<string>? OnSearch;

    public EconomicConsolePanel()
    {
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;
        Margin = new Thickness(4);

        _statusLabel = new Label
        {
            Text = Loc.GetString("economic-console-loading"),
            HorizontalAlignment = HAlignment.Center,
            Margin = new Thickness(0, 4),
        };
        AddChild(_statusLabel);

        var searchContainer = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(0, 4),
        };

        var searchLabel = new Label
        {
            Text = Loc.GetString("economic-console-search") + ":",
            VerticalAlignment = VAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _searchBox = new LineEdit
        {
            HorizontalExpand = true,
            PlaceHolder = Loc.GetString("economic-console-search-placeholder"),
        };
        _searchBox.OnTextChanged += OnSearchChanged;

        searchContainer.AddChild(searchLabel);
        searchContainer.AddChild(_searchBox);
        AddChild(searchContainer);

        _contentScroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
        };

        _contentList = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };

        _contentScroll.AddChild(_contentList);
        AddChild(_contentScroll);
    }

    private void OnSearchChanged(LineEdit.LineEditEventArgs args)
    {
        var search = args.Text.Length > EconomicConsoleSearchMessage.MaxLength
            ? args.Text[..EconomicConsoleSearchMessage.MaxLength]
            : args.Text;

        OnSearch?.Invoke(search);
    }

    public void UpdateState(EconomicConsoleBuiState state)
    {
        _state = state;
        _statusLabel.Text = Loc.GetString("economic-console-status-count", ("count", state.Tracked));
        RefreshContentList();
    }

    private void RefreshContentList()
    {
        _contentList.RemoveAllChildren();

        if (_state is not { } state)
            return;

        if (state.Shuttles.Count > 0)
        {
            var shuttleHeader = new Label
            {
                Text = Loc.GetString("economic-console-shuttles"),
                StyleClasses = { "ConsoleSubHeading" },
                Margin = new Thickness(0, 8, 0, 4),
            };
            _contentList.AddChild(shuttleHeader);

            foreach (var shuttle in state.Shuttles)
            {
                _contentList.AddChild(CreateShuttleRow(shuttle));
            }
        }

        var itemHeader = new Label
        {
            Text = Loc.GetString("economic-console-items"),
            StyleClasses = { "ConsoleSubHeading" },
            Margin = new Thickness(0, 12, 0, 4),
        };
        _contentList.AddChild(itemHeader);

        if (state.Items.Count == 0 && state.Shuttles.Count == 0)
        {
            var noResults = new Label
            {
                Text = Loc.GetString("economic-console-no-results"),
                HorizontalAlignment = HAlignment.Center,
                VerticalAlignment = VAlignment.Center,
                Margin = new Thickness(0, 20),
            };
            _contentList.AddChild(noResults);
            return;
        }

        foreach (var price in state.Items)
        {
            _contentList.AddChild(CreateItemRow(price));
        }

        if (state.MatchingItems > state.Items.Count)
        {
            var moreLabel = new Label
            {
                Text = Loc.GetString("economic-console-more-items", ("count", state.MatchingItems - state.Items.Count)),
                HorizontalAlignment = HAlignment.Center,
                Margin = new Thickness(0, 8),
            };
            _contentList.AddChild(moreLabel);
        }
    }

    private Control CreateShuttleRow(ShuttlePriceInfoData shuttle)
    {
        var container = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(4, 2),
        };

        var nameLabel = new Label
        {
            Text = TruncateString(shuttle.Name, 30),
            StyleClasses = { "ConsoleText" },
            HorizontalExpand = true,
            MinWidth = 180,
        };

        var baseLabel = new Label
        {
            Text = $"{shuttle.BasePrice:N0}cr",
            StyleClasses = { "ConsoleText" },
            MinWidth = 80,
            HorizontalAlignment = HAlignment.Right,
        };

        var currentLabel = new Label
        {
            Text = $"{shuttle.CurrentPrice:N0}cr",
            StyleClasses = { "ConsoleText" },
            MinWidth = 80,
            HorizontalAlignment = HAlignment.Right,
        };

        var changeText = shuttle.PriceChange >= 0 ? "+" : "-";
        var changePercent = Math.Abs(shuttle.PriceChange * 100);
        var changeLabel = new Label
        {
            Text = $"{changeText}{changePercent:F0}%",
            StyleClasses = { "ConsoleText" },
            FontColorOverride = shuttle.PriceChange >= 0 ? PriceUp : PriceDown,
            MinWidth = 60,
            HorizontalAlignment = HAlignment.Right,
        };

        container.AddChild(nameLabel);
        container.AddChild(baseLabel);
        container.AddChild(currentLabel);
        container.AddChild(changeLabel);

        return container;
    }

    private Control CreateItemRow(ItemPriceData price)
    {
        var container = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(0, 2),
        };

        var displayName = !string.IsNullOrEmpty(price.LocalizedName) ? price.LocalizedName : price.ItemId;
        var nameLabel = new Label
        {
            Text = TruncateString(displayName, 30),
            StyleClasses = { "ConsoleText" },
            HorizontalExpand = true,
            MinWidth = 180,
        };

        var baseLabel = new Label
        {
            Text = $"{price.BasePrice:F0}cr",
            StyleClasses = { "ConsoleText" },
            MinWidth = 70,
            HorizontalAlignment = HAlignment.Right,
        };

        var currentLabel = new Label
        {
            Text = $"{price.CurrentPrice:F0}cr",
            StyleClasses = { "ConsoleText" },
            MinWidth = 70,
            HorizontalAlignment = HAlignment.Right,
        };

        var changeText = price.Multiplier >= 1.0f ? "+" : "-";
        var changePercent = Math.Abs((price.Multiplier - 1.0f) * 100);
        var changeLabel = new Label
        {
            Text = $"{changeText}{changePercent:F0}%",
            StyleClasses = { "ConsoleText" },
            FontColorOverride = price.Multiplier >= 1.0f ? PriceUp : PriceDown,
            MinWidth = 60,
            HorizontalAlignment = HAlignment.Right,
        };

        container.AddChild(nameLabel);
        container.AddChild(baseLabel);
        container.AddChild(currentLabel);
        container.AddChild(changeLabel);

        return container;
    }

    private static string TruncateString(string str, int maxLength)
    {
        return str.Length <= maxLength ? str : str[..(maxLength - 2)] + "..";
    }

    public void UpdateStatus(string text)
    {
        _statusLabel.Text = text;
    }
}
