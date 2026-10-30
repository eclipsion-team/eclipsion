using Content.Shared._Crescent.Economy;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Prototype("persistentPoiProductionRecipe")]
public sealed partial class PersistentPoiProductionRecipePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public float Duration = 600f;

    [DataField(required: true)]
    public Dictionary<EntProtoId, int> Outputs = new();
}

[RegisterComponent]
public sealed partial class PersistentPoiProductionMachineComponent : Component
{
    [DataField(required: true)]
    public ProtoId<PersistentPoiCapabilityPrototype> Capability;

    [DataField(required: true)]
    public List<ProtoId<PersistentPoiProductionRecipePrototype>> Recipes = new();

    [DataField]
    public ProtoId<PersistentPoiProductionRecipePrototype>? SelectedRecipe;

    [DataField]
    public bool Enabled = true;

    [DataField]
    public float Progress;

    [DataField]
    public int Stored;

    [DataField]
    public int MaxStored = 4;

    [DataField]
    public float PowerDraw = 25000f;

    [ViewVariables]
    public PersistentPoiProductionStatus Status = PersistentPoiProductionStatus.Unclaimed;

    [ViewVariables]
    public float PowerRatio;
}

[RegisterComponent]
public sealed partial class PersistentPoiProductionConsoleComponent : Component
{
    [DataField]
    public float RushMarkup = 1.5f;

    [DataField]
    public int RushPricePerMinute = 400;

    [DataField]
    public int MinimumRushPrice = 250;

    [DataField]
    public float OvertimeSpeed = 2f;

    [DataField]
    public TimeSpan OvertimeDuration = TimeSpan.FromMinutes(10);

    [DataField]
    public TimeSpan MaxOvertime = TimeSpan.FromMinutes(30);

    [DataField]
    public float OvertimeMarkup = 1.2f;

    [DataField]
    public int MinimumOvertimePrice = 500;

    [DataField]
    public float TreasuryShare = 0.25f;
}

[Serializable, NetSerializable]
public enum PersistentPoiProductionStatus : byte
{
    Running,
    Disabled,
    NoRecipe,
    Unclaimed,
    Unpowered,
    NoConsole,
    Full,

    WrongCapability,

    OverLimit,

    Starting,

    LowPower,
}

public static class PersistentPoiProductionStatusExt
{
    public static string GetLocId(this PersistentPoiProductionStatus status)
    {
        return status switch
        {
            PersistentPoiProductionStatus.Running => "persistent-poi-production-status-running",
            PersistentPoiProductionStatus.Disabled => "persistent-poi-production-status-disabled",
            PersistentPoiProductionStatus.NoRecipe => "persistent-poi-production-status-no-recipe",
            PersistentPoiProductionStatus.Unpowered => "persistent-poi-production-status-unpowered",
            PersistentPoiProductionStatus.NoConsole => "persistent-poi-production-status-no-console",
            PersistentPoiProductionStatus.Full => "persistent-poi-production-status-full",
            PersistentPoiProductionStatus.WrongCapability => "persistent-poi-production-status-wrong-capability",
            PersistentPoiProductionStatus.OverLimit => "persistent-poi-production-status-over-limit",
            PersistentPoiProductionStatus.Starting => "persistent-poi-production-status-starting",
            PersistentPoiProductionStatus.LowPower => "persistent-poi-production-status-low-power",
            _ => "persistent-poi-production-status-unclaimed",
        };
    }
}

[Serializable, NetSerializable]
public enum PersistentPoiProductionUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionMachineState
{
    public NetEntity Machine;
    public string Name = string.Empty;
    public bool Enabled;
    public PersistentPoiProductionStatus Status;
    public string? SelectedRecipe;
    public List<string> RecipeIds = new();
    public List<string> RecipeNames = new();

    public float Progress;

    public float Power;

    public int Stored;
    public int MaxStored;

    public string Output = string.Empty;

    public int RushPrice;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiCapabilityOption
{
    public string Id = string.Empty;
    public string Name = string.Empty;
    public string Description = string.Empty;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionConsoleState : BoundUserInterfaceState
{
    public string? Owner;
    public int Level;

    public string? Capability;

    public List<PersistentPoiCapabilityOption> Capabilities = new();

    public int Factories;
    public int? FactoryLimit;

    public List<PersistentPoiProductionMachineState> Machines = new();

    public TerritoryPaymentState? Payment;

    public float OvertimeSeconds;

    public int OvertimePrice;

    public float OvertimeSpeed = 1f;

    public int OvertimeMinutes;

    public bool OvertimeMaxed;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionRushMessage(NetEntity machine, PaymentSource source) : BoundUserInterfaceMessage
{
    public NetEntity Machine { get; private set; } = machine;

    public PaymentSource Source { get; private set; } = source;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionOvertimeMessage(PaymentSource source) : BoundUserInterfaceMessage
{
    public PaymentSource Source { get; private set; } = source;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionSetCapabilityMessage(string capability) : BoundUserInterfaceMessage
{
    public string Capability { get; private set; } = capability;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionDecommissionMessage(NetEntity machine) : BoundUserInterfaceMessage
{
    public NetEntity Machine { get; private set; } = machine;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionToggleMessage(NetEntity machine) : BoundUserInterfaceMessage
{
    public NetEntity Machine { get; private set; } = machine;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionSelectRecipeMessage(NetEntity machine, string recipe) : BoundUserInterfaceMessage
{
    public NetEntity Machine { get; private set; } = machine;

    public string Recipe { get; private set; } = recipe;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiProductionDispenseMessage(NetEntity machine) : BoundUserInterfaceMessage
{
    public NetEntity Machine { get; private set; } = machine;
}
