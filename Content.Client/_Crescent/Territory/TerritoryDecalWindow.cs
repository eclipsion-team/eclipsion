using System.Linq;
using System.Numerics;
using Content.Shared._Crescent.Territory;
using Content.Shared.Decals;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;

namespace Content.Client._Crescent.Territory;

public sealed class TerritoryDecalWindow : DefaultWindow
{
    public event Action<string>? DecalPicked;
    public event Action<int>? ColorPicked;
    public event Action? Rotated;

    private const int Columns = 8;
    private static readonly Vector2 DecalButtonSize = new(52, 52);

    private readonly IPrototypeManager _proto;
    private readonly SpriteSystem _sprite;

    private readonly BoxContainer _pages;
    private readonly GridContainer _decals;
    private readonly BoxContainer _colors;
    private readonly Label _rotation;

    private readonly List<(string Id, Button Button, TextureRect Texture)> _decalButtons = new();
    private readonly List<Button> _colorButtons = new();
    private readonly List<Button> _pageButtons = new();

    private List<(string Name, List<string> Decals)> _pageContents = new();
    private Color _color = Color.White;
    private string? _selected;

    public TerritoryDecalWindow(IPrototypeManager proto, SpriteSystem sprite)
    {
        _proto = proto;
        _sprite = sprite;

        Title = Loc.GetString("territory-decal-window-title");
        MinSize = new Vector2(500, 470);

        var body = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 6 };

        body.AddChild(new Label
        {
            Text = Loc.GetString("territory-decal-window-hint"),
            StyleClasses = { "LabelSecondaryColor" },
        });

        _pages = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 2 };
        body.AddChild(_pages);

        _decals = new GridContainer { Columns = Columns };
        body.AddChild(new ScrollContainer
        {
            VerticalExpand = true,
            HScrollEnabled = false,
            MinHeight = 240,
            Children = { _decals },
        });

        body.AddChild(new Label { Text = Loc.GetString("territory-decal-window-colour") });
        _colors = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 4 };
        body.AddChild(_colors);

        var rotationRow = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 8 };
        var rotate = new Button { Text = Loc.GetString("territory-decal-window-rotate") };
        rotate.OnPressed += _ => Rotated?.Invoke();
        _rotation = new Label { VerticalAlignment = VAlignment.Center };
        rotationRow.AddChild(rotate);
        rotationRow.AddChild(_rotation);
        body.AddChild(rotationRow);

        Contents.AddChild(body);
    }

    public void Populate(TerritoryDecalPalettePrototype? palette)
    {
        _pageContents = _proto.EnumeratePrototypes<TerritoryDecalSetPrototype>()
            .OrderBy(set => set.Priority)
            .ThenBy(set => set.ID)
            .Select(set => (Loc.GetString(set.Name), set.Decals.Select(d => d.Id).ToList()))
            .ToList();

        if (palette is { Emblems.Count: > 0 })
            _pageContents.Insert(0, (Loc.GetString("territory-decal-page-emblems"), palette.Emblems.Select(d => d.Id).ToList()));

        _pages.RemoveAllChildren();
        _pageButtons.Clear();
        for (var i = 0; i < _pageContents.Count; i++)
        {
            var page = i;
            var button = new Button { Text = _pageContents[i].Name, ToggleMode = true, StyleClasses = { "OpenBoth" } };
            button.OnPressed += _ => ShowPage(page);
            _pages.AddChild(button);
            _pageButtons.Add(button);
        }

        _colors.RemoveAllChildren();
        _colorButtons.Clear();
        var colors = palette?.Colors ?? new List<Color> { Color.White };
        for (var i = 0; i < colors.Count; i++)
        {
            var index = i;
            var swatch = new Button { ToggleMode = true, SetSize = new Vector2(30, 30) };
            swatch.AddChild(new PanelContainer
            {
                PanelOverride = new StyleBoxFlat { BackgroundColor = colors[i] },
                MinSize = new Vector2(22, 22),
            });
            swatch.OnPressed += _ => ColorPicked?.Invoke(index);
            _colors.AddChild(swatch);
            _colorButtons.Add(swatch);
        }

        if (_pageContents.Count > 0)
            ShowPage(0);
    }

    private void ShowPage(int page)
    {
        for (var i = 0; i < _pageButtons.Count; i++)
        {
            _pageButtons[i].Pressed = i == page;
        }

        _decals.RemoveAllChildren();
        _decalButtons.Clear();

        foreach (var id in _pageContents[page].Decals)
        {
            if (!_proto.TryIndex<DecalPrototype>(id, out var decal))
                continue;

            var button = new Button { ToggleMode = true, SetSize = DecalButtonSize, ToolTip = id };
            var texture = new TextureRect
            {
                Texture = _sprite.Frame0(decal.Sprite),
                Stretch = TextureRect.StretchMode.KeepAspectCentered,
                Modulate = _color,
                MinSize = new Vector2(40, 40),
            };
            button.AddChild(texture);
            button.OnPressed += _ => DecalPicked?.Invoke(id);
            _decals.AddChild(button);
            _decalButtons.Add((id, button, texture));
        }

        Select(_selected, _color, null);
    }

    public void Select(string? decal, Color color, int? quarterTurns)
    {
        _selected = decal;
        _color = color;

        foreach (var (id, button, texture) in _decalButtons)
        {
            button.Pressed = id == decal;
            texture.Modulate = color;
        }

        if (quarterTurns is { } turns)
            _rotation.Text = Loc.GetString("territory-decal-window-facing", ("degrees", turns * 90));
    }

    public void SelectColor(int index)
    {
        for (var i = 0; i < _colorButtons.Count; i++)
        {
            _colorButtons[i].Pressed = i == index;
        }
    }
}
