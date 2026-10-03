using System.Numerics;
using Content.Client.Stylesheets;
using Content.Shared._Crescent.Religion;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._Crescent.Religion;

/// <summary>
///     Lists everyone the Prophet has brought into the Covenant, and lets them chastise bound members who stray.
/// </summary>
public sealed class VeiledHostWindow : DefaultWindow
{
    private static readonly Color BoundColor = Color.FromHex("#B57BFF");
    private static readonly Color FaithfulColor = Color.FromHex("#7FC97F");
    private static readonly Color CriticalColor = Color.FromHex("#FAB325");
    private static readonly Color DeadColor = Color.FromHex("#9F3344");

    private readonly BoxContainer _members;
    private readonly Label _empty;

    public event Action<NetEntity>? OnChastise;
    public event Action? OnRefresh;

    public VeiledHostWindow()
    {
        Title = Loc.GetString("religion-veiled-host-title");
        MinSize = new Vector2(480, 320);
        SetSize = new Vector2(520, 420);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
        };
        Contents.AddChild(root);

        var header = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        header.AddChild(new Label
        {
            Text = Loc.GetString("religion-veiled-host-subtitle"),
            HorizontalExpand = true,
            StyleClasses = { StyleBase.StyleClassLabelSubText },
        });

        var refresh = new Button { Text = Loc.GetString("religion-veiled-host-refresh") };
        refresh.OnPressed += _ => OnRefresh?.Invoke();
        header.AddChild(refresh);
        root.AddChild(header);

        _empty = new Label
        {
            Text = Loc.GetString("religion-veiled-host-empty"),
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            VerticalExpand = true,
            StyleClasses = { StyleBase.StyleClassLabelSubText },
        };
        root.AddChild(_empty);

        var scroll = new ScrollContainer
        {
            VerticalExpand = true,
            HScrollEnabled = false,
        };
        _members = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
        };
        scroll.AddChild(_members);
        root.AddChild(scroll);
    }

    public void UpdateState(VeiledHostEuiState state)
    {
        _members.RemoveAllChildren();
        _empty.Visible = state.Members.Count == 0;

        foreach (var member in state.Members)
            _members.AddChild(BuildRow(member));
    }

    private Control BuildRow(VeiledHostMember member)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 8,
        };

        var info = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };

        info.AddChild(new Label
        {
            Text = member.Name,
            FontColorOverride = member.Bound ? BoundColor : FaithfulColor,
        });

        var status = member.Status switch
        {
            VeiledHostMemberStatus.Dead => (Loc.GetString("religion-veiled-host-status-dead"), DeadColor),
            VeiledHostMemberStatus.Critical => (Loc.GetString("religion-veiled-host-status-critical"), CriticalColor),
            _ => (Loc.GetString("religion-veiled-host-status-alive"), Color.LightGray),
        };

        var tie = Loc.GetString(member.Bound ? "religion-veiled-host-bound" : "religion-veiled-host-faithful");
        var where = member.Distance is { } distance
            ? Loc.GetString("religion-veiled-host-location",
                ("distance", (int) MathF.Round(distance)),
                ("direction", member.Direction ?? Loc.GetString("religion-veiled-host-here")))
            : Loc.GetString("religion-veiled-host-far");

        info.AddChild(new Label
        {
            Text = $"{tie} · {status.Item1} · {where}",
            FontColorOverride = status.Item2,
            StyleClasses = { StyleBase.StyleClassLabelSubText },
        });

        row.AddChild(info);

        if (member.Bound)
        {
            var chastise = new Button
            {
                Text = Loc.GetString("religion-veiled-host-chastise"),
                ToolTip = Loc.GetString("religion-veiled-host-chastise-tooltip"),
                Disabled = !member.CanChastise,
                VerticalAlignment = VAlignment.Center,
            };
            chastise.AddStyleClass(StyleBase.ButtonCaution);
            chastise.OnPressed += _ => OnChastise?.Invoke(member.Entity);
            row.AddChild(chastise);
        }

        var panel = new PanelContainer
        {
            PanelOverride = new Robust.Client.Graphics.StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#1E1A26"),
                ContentMarginLeftOverride = 8,
                ContentMarginRightOverride = 8,
                ContentMarginTopOverride = 4,
                ContentMarginBottomOverride = 4,
            },
        };
        panel.AddChild(row);
        return panel;
    }
}
