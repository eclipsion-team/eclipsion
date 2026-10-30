using System;
using System.Collections.Generic;
using Content.Client.UserInterface.Controls;
using Content.Shared._Crescent.Factions;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client._Crescent.Factions;

/// <summary>
/// Faction muster console. Left: nearby people, one selectable per row (with their current faction). Right: the
/// selected person's role assignment — a button per faction role that enlists/promotes them, plus a dismiss
/// button to remove an existing member. A refresh button re-scans for people who walked up after opening.
/// </summary>
public sealed class FactionRecruitmentWindow : FancyWindow
{
    private readonly BoxContainer _targetList;
    private readonly BoxContainer _rolePanel;
    private readonly RichTextLabel _selectedLabel;

    private List<FactionRecruitmentOption> _options = new();
    private NetEntity? _selected;

    /// <summary>Raised with (target, jobId) when the operator assigns a role.</summary>
    public event Action<NetEntity, string>? OnAssign;

    /// <summary>Raised with the target when the operator dismisses a member.</summary>
    public event Action<NetEntity>? OnDismiss;

    /// <summary>Raised when the operator asks to re-scan for nearby people.</summary>
    public event Action? OnRefresh;

    public FactionRecruitmentWindow()
    {
        Title = Loc.GetString("faction-recruitment-window-title");
        MinSize = new(640, 480);
        SetSize = new(680, 520);

        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new(6),
        };

        // Left column: nearby people.
        var left = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinWidth = 280,
            VerticalExpand = true,
        };
        var leftHeader = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        leftHeader.AddChild(new Label
        {
            Text = Loc.GetString("faction-recruitment-nearby-header"),
            StyleClasses = { "LabelHeading" },
            HorizontalExpand = true,
        });
        var refresh = new Button { Text = Loc.GetString("faction-recruitment-refresh-button") };
        refresh.OnPressed += _ => OnRefresh?.Invoke();
        leftHeader.AddChild(refresh);
        left.AddChild(leftHeader);

        var targetScroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true };
        _targetList = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new(0, 4),
        };
        targetScroll.AddChild(_targetList);
        left.AddChild(targetScroll);
        body.AddChild(left);

        // Right column: role assignment for the selected person.
        var right = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new(8, 0, 0, 0),
        };
        _selectedLabel = new RichTextLabel { HorizontalExpand = true };
        SetSelectedText(Loc.GetString("faction-recruitment-select-prompt"));
        right.AddChild(_selectedLabel);

        var roleScroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true };
        _rolePanel = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new(0, 4),
        };
        roleScroll.AddChild(_rolePanel);
        right.AddChild(roleScroll);
        body.AddChild(right);

        ContentsContainer.AddChild(body);
    }

    public void UpdateState(FactionRecruitmentConsoleState state)
    {
        Title = Loc.GetString("faction-recruitment-window-title-faction", ("faction", state.FactionName));
        _options = state.Options;

        // Drop a selection whose person is no longer in range.
        if (_selected != null && !state.Targets.Exists(t => t.Entity == _selected))
            _selected = null;

        _targetList.RemoveAllChildren();
        if (state.Targets.Count == 0)
        {
            _targetList.AddChild(new Label { Text = Loc.GetString("faction-recruitment-nobody"), Margin = new(4) });
        }
        else
        {
            foreach (var target in state.Targets)
            {
                var button = new Button
                {
                    Text = target.IsMember
                        ? Loc.GetString("faction-recruitment-target-member", ("name", target.Name))
                        : string.IsNullOrEmpty(target.CurrentFactionName)
                            ? target.Name
                            : Loc.GetString("faction-recruitment-target-other", ("name", target.Name), ("faction", target.CurrentFactionName)),
                    ToggleMode = true,
                    Pressed = target.Entity == _selected,
                    HorizontalExpand = true,
                    Margin = new(0, 0, 0, 2),
                };
                var entity = target.Entity;
                button.OnPressed += _ =>
                {
                    _selected = entity;
                    RebuildRolePanel(state);
                };
                _targetList.AddChild(button);
            }
        }

        RebuildRolePanel(state);
    }

    private void RebuildRolePanel(FactionRecruitmentConsoleState state)
    {
        _rolePanel.RemoveAllChildren();

        var selected = _selected != null ? state.Targets.Find(t => t.Entity == _selected) : null;
        if (selected == null)
        {
            SetSelectedText(Loc.GetString("faction-recruitment-select-prompt"));
            return;
        }

        SetSelectedText(Loc.GetString("faction-recruitment-selected", ("name", selected.Name)));

        if (_options.Count == 0)
        {
            _rolePanel.AddChild(new Label { Text = Loc.GetString("faction-recruitment-none"), Margin = new(4) });
        }

        foreach (var opt in _options)
        {
            var panel = new PanelContainer { Margin = new(0, 0, 0, 6) };
            var row = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                Margin = new(8, 6),
                HorizontalExpand = true,
            };
            row.AddChild(new Label { Text = opt.RoleName, StyleClasses = { "LabelHeading" } });

            if (!string.IsNullOrWhiteSpace(opt.Description))
            {
                var desc = new RichTextLabel { HorizontalExpand = true, Margin = new(0, 2, 0, 4) };
                desc.SetMessage(opt.Description);
                row.AddChild(desc);
            }

            var assign = new Button
            {
                Text = selected.IsMember
                    ? Loc.GetString("faction-recruitment-promote-button")
                    : Loc.GetString("faction-recruitment-recruit-button"),
                HorizontalAlignment = HAlignment.Left,
            };
            var jobId = opt.JobId;
            var target = selected.Entity;
            assign.OnPressed += _ => OnAssign?.Invoke(target, jobId);
            row.AddChild(assign);

            panel.AddChild(row);
            _rolePanel.AddChild(panel);
        }

        if (selected.IsMember)
        {
            var dismiss = new Button
            {
                Text = Loc.GetString("faction-recruitment-dismiss-button"),
                StyleClasses = { "ButtonCaution" },
                Margin = new(0, 8, 0, 0),
                HorizontalAlignment = HAlignment.Left,
            };
            var target = selected.Entity;
            dismiss.OnPressed += _ => OnDismiss?.Invoke(target);
            _rolePanel.AddChild(dismiss);
        }
    }

    private void SetSelectedText(string text)
    {
        _selectedLabel.SetMessage(FormattedMessage.FromMarkupOrThrow($"[bold]{FormattedMessage.EscapeText(text)}[/bold]"));
    }
}
