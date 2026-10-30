using Robust.Client.UserInterface.Controls;

namespace Content.Client._Crescent.UI;

[Virtual]
public class HintLabel : RichTextLabel
{
    private string _hint = string.Empty;
    private Color? _color;

    public HintLabel()
    {
        HorizontalExpand = true;
    }

    public string Hint
    {
        get => _hint;
        set
        {
            _hint = value;
            this.SetHint(value, _color);
        }
    }

    public Color? HintColor
    {
        get => _color;
        set
        {
            _color = value;
            this.SetHint(_hint, value);
        }
    }
}
