namespace Content.Shared.DeltaV.Paper;

/// <summary>
/// Lets a writing utensil change the font and color of signatures made with it.
/// Pens without this component sign with the signer's font and the default color.
/// </summary>
[RegisterComponent]
public sealed partial class SignatureWriterComponent : Component
{
    /// <summary>
    /// If this is set, the defined font will be forced for the signature.
    /// </summary>
    [DataField]
    public string? Font;

    /// <summary>
    /// The list of fonts that can be selected from, for pens with multiple fonts.
    /// </summary>
    [DataField]
    public Dictionary<string, string> FontList = new();

    /// <summary>
    /// The color used for the signature.
    /// </summary>
    [DataField]
    public Color Color = Color.DarkSlateGray;

    /// <summary>
    /// The list of colors that can be selected from, for pens with multiple colors.
    /// </summary>
    [DataField]
    public Dictionary<string, Color> ColorList = new();
}
