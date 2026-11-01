using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.Territory;

/// <summary>
/// An independent's own territory owner id (IND1, IND2...), kept per player across rounds. Only read while the body
/// is still in the IND faction.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TerritoryIndependentComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Owner = string.Empty;
}
