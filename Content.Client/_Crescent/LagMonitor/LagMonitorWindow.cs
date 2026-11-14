using System.Linq;
using System.Numerics;
using Content.Shared._Crescent.LagMonitor;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;

namespace Content.Client._Crescent.LagMonitor;

public sealed class LagMonitorWindow : DefaultWindow
{
    public event Action? ClearPressed;

    private readonly Button _nowButton;
    private readonly Button _peakButton;
    private readonly Label _status;
    private readonly GridContainer _stats;
    private readonly BoxContainer _findings;
    private readonly LoadGraph _graph;
    private readonly GridContainer _systems;
    private readonly GridContainer _parts;
    private readonly BoxContainer _hitches;
    private readonly RichTextLabel _pings;

    private LagMonitorEuiState? _state;
    private bool _showPeak;

    public LagMonitorWindow()
    {
        Title = Loc.GetString("lag-monitor-window-title");
        MinSize = new Vector2(760, 640);
        SetSize = new Vector2(820, 760);

        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 6 };
        Contents.AddChild(root);

        var top = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 4 };
        _nowButton = new Button { Text = Loc.GetString("lag-monitor-view-now"), ToggleMode = true, Pressed = true };
        _peakButton = new Button { Text = Loc.GetString("lag-monitor-view-peak"), ToggleMode = true };
        _status = new Label { HorizontalExpand = true, Align = Label.AlignMode.Right, FontColorOverride = Color.Gray };
        var clear = new Button { Text = Loc.GetString("lag-monitor-clear") };
        top.AddChild(_nowButton);
        top.AddChild(_peakButton);
        top.AddChild(_status);
        top.AddChild(clear);
        root.AddChild(top);

        _nowButton.OnPressed += _ => SetView(false);
        _peakButton.OnPressed += _ => SetView(true);
        clear.OnPressed += _ => ClearPressed?.Invoke();

        var scroll = new ScrollContainer { VerticalExpand = true, HScrollEnabled = false };
        root.AddChild(scroll);

        var body = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8, Margin = new Thickness(0, 0, 8, 0) };
        scroll.AddChild(body);

        // engine grid uses the H separation between rows too, keep them equal or the rows spill out
        _stats = new GridContainer { Columns = 4, HSeparationOverride = 4, VSeparationOverride = 4 };
        body.AddChild(_stats);

        body.AddChild(Header("lag-monitor-findings"));
        _findings = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 2 };
        body.AddChild(_findings);

        body.AddChild(Header("lag-monitor-history"));
        _graph = new LoadGraph { MinSize = new Vector2(0, 90), HorizontalExpand = true };
        body.AddChild(_graph);

        var columns = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 16 };
        body.AddChild(columns);

        var left = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        left.AddChild(Header("lag-monitor-systems"));
        _systems = new GridContainer { Columns = 2, HSeparationOverride = 4, VSeparationOverride = 4 };
        left.AddChild(_systems);
        columns.AddChild(left);

        var right = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        right.AddChild(Header("lag-monitor-parts"));
        _parts = new GridContainer { Columns = 2, HSeparationOverride = 4, VSeparationOverride = 4 };
        right.AddChild(_parts);
        columns.AddChild(right);

        body.AddChild(Header("lag-monitor-hitches"));
        _hitches = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 2 };
        body.AddChild(_hitches);

        body.AddChild(Header("lag-monitor-pings"));
        _pings = new RichTextLabel { HorizontalExpand = true };
        body.AddChild(_pings);
    }

    private static Label Header(string loc)
    {
        return new Label
        {
            Text = Loc.GetString(loc),
            FontColorOverride = Color.FromHex("#8fb8e8"),
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    private void SetView(bool peak)
    {
        _showPeak = peak;
        _nowButton.Pressed = !peak;
        _peakButton.Pressed = peak;
        Refresh();
    }

    public void UpdateState(LagMonitorEuiState state)
    {
        _state = state;
        Refresh();
    }

    private void Refresh()
    {
        if (_state == null)
            return;

        var sample = _showPeak ? _state.Peak : _state.Current;

        if (!_state.Enabled)
            _status.Text = Loc.GetString("lag-monitor-disabled");
        else if (sample == null)
            _status.Text = Loc.GetString("lag-monitor-waiting");
        else
            _status.Text = Loc.GetString("lag-monitor-peak-at", ("time", sample.Time.ToString("HH:mm:ss")));

        _findings.RemoveAllChildren();
        foreach (var finding in _state.Findings)
        {
            var ok = finding == Loc.GetString("lag-monitor-finding-ok");
            var label = new RichTextLabel { HorizontalExpand = true };
            label.SetMessage(FormattedMessage.FromUnformatted("• " + finding),
                defaultColor: ok ? Color.FromHex("#7ccf7c") : Color.FromHex("#f0b050"));
            _findings.AddChild(label);
        }

        _graph.Values = _state.LoadHistory;

        _stats.RemoveAllChildren();
        _systems.RemoveAllChildren();
        _parts.RemoveAllChildren();

        if (sample != null)
        {
            var budget = 1000f / Math.Max(1f, sample.TargetTickRate);
            var behind = sample.Load >= 0.9f || sample.TickRate < sample.TargetTickRate * 0.9f;

            Stat("lag-monitor-load", $"{sample.Load * 100:0}%", LoadColor(sample.Load));
            Stat("lag-monitor-tickrate", $"{sample.TickRate:0.0} / {sample.TargetTickRate}", behind ? Color.FromHex("#e06060") : null);
            Stat("lag-monitor-tickms", $"{sample.TickMs:0.0} ({Loc.GetString("lag-monitor-budget", ("ms", budget.ToString("0.0")))})");
            Stat("lag-monitor-players", sample.Players.ToString());
            Stat("lag-monitor-entities", sample.Entities.ToString());
            Stat("lag-monitor-npcs", $"{sample.ActiveNpcs} / {sample.Npcs}");
            Stat("lag-monitor-grids", sample.Grids.ToString());
            Stat("lag-monitor-drones", sample.Drones.ToString());
            Stat("lag-monitor-bodies", sample.AwakeBodies.ToString());
            Stat("lag-monitor-net", $"{sample.NetOutKb:0} / {sample.NetInKb:0} KB/s");
            Stat("lag-monitor-gc-pause", $"{sample.GcPauseMs:0} ms ({sample.Gen2} gen2)", sample.GcPauseMs >= 100 ? Color.FromHex("#f0b050") : null);
            Stat("lag-monitor-self", $"{sample.MonitorMs:0.000} ms");

            foreach (var e in sample.Systems)
            {
                Row(_systems, e, budget);
            }

            foreach (var e in sample.Parts.Where(p => p.Ms >= 0.01f))
            {
                Row(_parts, e, budget);
            }
        }

        _hitches.RemoveAllChildren();
        if (_state.Hitches.Count == 0)
        {
            _hitches.AddChild(new Label { Text = Loc.GetString("lag-monitor-no-hitches"), FontColorOverride = Color.Gray });
        }

        foreach (var hitch in _state.Hitches)
        {
            var causes = hitch.Top.Count == 0
                ? Loc.GetString("lag-monitor-unknown-cause")
                : string.Join(", ", hitch.Top.Select(e => $"{e.Name} {e.Ms:0} ms"));

            var text = $"{hitch.Time:HH:mm:ss}  {hitch.Ms:0} ms  ";
            if (hitch.RoundLoading)
                text += Loc.GetString("lag-monitor-round-start") + "  ";

            var label = new RichTextLabel { HorizontalExpand = true };
            label.SetMessage(FormattedMessage.FromUnformatted(text + causes),
                defaultColor: hitch.RoundLoading ? Color.Gray : hitch.Ms >= 500 ? Color.FromHex("#e06060") : Color.FromHex("#f0b050"));
            _hitches.AddChild(label);
        }

        var pings = new FormattedMessage();
        foreach (var ping in _state.Pings)
        {
            if (!pings.IsEmpty)
                pings.AddText("    ");

            pings.PushColor(ping.Ping >= 250 ? Color.FromHex("#e06060") : Color.White);
            pings.AddText($"{ping.Name}: {ping.Ping} ms");
            pings.Pop();
        }

        _pings.SetMessage(pings);
    }

    private void Stat(string loc, string value, Color? color = null)
    {
        _stats.AddChild(new Label { Text = Loc.GetString(loc), FontColorOverride = Color.Gray });
        _stats.AddChild(new Label { Text = value, FontColorOverride = color, Margin = new Thickness(6, 0, 16, 0) });
    }

    private static void Row(GridContainer grid, LagEntry entry, float budget)
    {
        var share = entry.Ms / budget;
        grid.AddChild(new Label
        {
            Text = $"{entry.Ms:0.00}",
            MinWidth = 50,
            Align = Label.AlignMode.Right,
            FontColorOverride = share >= 0.25f ? Color.FromHex("#e06060") : share >= 0.1f ? Color.FromHex("#f0b050") : null,
        });
        grid.AddChild(new Label { Text = entry.Name, ClipText = true, HorizontalExpand = true, Margin = new Thickness(6, 0, 0, 0) });
    }

    private static Color? LoadColor(float load)
    {
        if (load >= 0.9f)
            return Color.FromHex("#e06060");

        if (load >= 0.6f)
            return Color.FromHex("#f0b050");

        return Color.FromHex("#7ccf7c");
    }

    private sealed class LoadGraph : Control
    {
        public float[] Values = Array.Empty<float>();

        private const int Slots = 120;

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);

            var box = PixelSizeBox;
            handle.DrawRect(box, Color.FromHex("#15171c"));

            // y scale goes to 150% so an overloaded server still fits
            const float max = 1.5f;
            var height = box.Height - 4;
            float Y(float v) => box.Bottom - 2 - Math.Clamp(v / max, 0f, 1f) * height;

            var full = Y(1f);
            handle.DrawLine(new Vector2(box.Left, full), new Vector2(box.Right, full), Color.FromHex("#e06060").WithAlpha(0.6f));
            var half = Y(0.5f);
            handle.DrawLine(new Vector2(box.Left, half), new Vector2(box.Right, half), Color.FromHex("#ffffff").WithAlpha(0.1f));

            if (Values.Length < 2)
                return;

            var step = box.Width / (Slots - 1f);
            var start = box.Right - step * (Values.Length - 1);
            for (var i = 1; i < Values.Length; i++)
            {
                var a = new Vector2(start + step * (i - 1), Y(Values[i - 1]));
                var b = new Vector2(start + step * i, Y(Values[i]));
                var color = LoadColor(Math.Max(Values[i - 1], Values[i])) ?? Color.White;
                handle.DrawLine(a, b, color);
                handle.DrawLine(a + new Vector2(0, 1), b + new Vector2(0, 1), color);
            }
        }
    }
}
