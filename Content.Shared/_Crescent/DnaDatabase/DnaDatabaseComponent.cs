using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.DnaDatabase;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DnaDatabaseComponent : Component
{
    [DataField]
    public string FactionId = "";

    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    [DataField]
    public EntityUid BoundGrid = EntityUid.Invalid;

    [DataField]
    public EntityUid BoundStation = EntityUid.Invalid;

    [DataField]
    public Dictionary<string, uint?> SavedJobSlots = new();

    /// <summary>
    /// Minimum time between toggles from the console. Every toggle is announced to the whole sector, and
    /// anyone on the grid may use the console, so without this it is a free announcement spammer.
    /// </summary>
    [DataField]
    public TimeSpan ToggleCooldown = TimeSpan.FromSeconds(30);

    [ViewVariables]
    public TimeSpan NextToggle;
}
