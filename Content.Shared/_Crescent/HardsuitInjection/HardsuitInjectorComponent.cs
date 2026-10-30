using Content.Shared.Actions;
using Content.Shared.Alert;
using Content.Shared.FixedPoint;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Crescent.HardsuitInjection;

/// <summary>
/// Gives a hardsuit two injector slots that can inject the wearer manually or when they enter critical condition.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HardsuitInjectorComponent : Component
{
    public const string SlotOneId = "hardsuit-injector-slot-1";
    public const string SlotTwoId = "hardsuit-injector-slot-2";

    [DataField, AutoNetworkedField]
    public EntProtoId SlotOneAction = "ActionHardsuitInjectSlotOne";

    [DataField, AutoNetworkedField]
    public EntProtoId SlotTwoAction = "ActionHardsuitInjectSlotTwo";

    [DataField, AutoNetworkedField]
    public EntityUid? SlotOneActionEntity;

    [DataField, AutoNetworkedField]
    public EntityUid? SlotTwoActionEntity;

    [DataField, AutoNetworkedField]
    public FixedPoint2 SlotOneTransferAmount = FixedPoint2.New(15);

    [DataField, AutoNetworkedField]
    public FixedPoint2 SlotTwoTransferAmount = FixedPoint2.New(15);

    [DataField]
    public FixedPoint2 MinimumTransferAmount = FixedPoint2.New(1);

    [DataField]
    public FixedPoint2 MaximumTransferAmount = FixedPoint2.New(40);

    [DataField]
    public bool AutoInjectOnCritical = true;

    [DataField]
    public ProtoId<AlertPrototype> MissingInjectorAlert = "HardsuitInjectorMissing";
}

/// <summary>
/// Marker used by hardsuit injector slot whitelists. It is inherited by compatible containers such as
/// ChemicalMedipen children and chemistry bottles.
/// </summary>
[RegisterComponent]
public sealed partial class HardsuitInjectableComponent : Component;

public sealed partial class HardsuitInjectActionEvent : InstantActionEvent
{
    [DataField(required: true)]
    public string Slot = string.Empty;
}
