using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Cargo.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedEconomicConsoleSystem))]
public sealed partial class EconomicConsoleComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField, AutoNetworkedField]
    public float RefreshInterval = 300f;

    [ViewVariables]
    public float LastUpdateTimer;

    [ViewVariables(VVAccess.ReadWrite)]
    [DataField, AutoNetworkedField]
    public bool AutoUpdateEnabled = true;

    // lower-cased, set by the viewer
    [ViewVariables]
    public string Search = string.Empty;
}

public abstract class SharedEconomicConsoleSystem : EntitySystem
{
}
