namespace Content.Shared.DeltaV.Paper;

/// <summary>
/// Used to determine which font is used when signing paper, if the pen doesn't force one.
/// </summary>
[RegisterComponent]
public sealed partial class SignatureFontComponent : Component
{
    /// <summary>
    /// ID of the font prototype to sign with.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public string? Font;
}
