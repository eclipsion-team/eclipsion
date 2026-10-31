using Content.Shared.Stacks;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[Serializable, NetSerializable]
public enum PersistentPoiWorksCategory : byte
{
    TargetingConsole,
    ProductionConsole,
    AdministrationConsole,

    PointDefence,

    AntiBoarderTurret,
    Factory,

    Shipyard,

    DockingPort,

    Generator,

    RadarConsole,
    ShuttleConsole,

    SpawnBeacon,
    GearRack,
}

[Prototype("persistentPoiLevel")]
public sealed partial class PersistentPoiLevelPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int Level;

    [DataField]
    public Dictionary<ProtoId<StackPrototype>, int> UpgradeCost = new();

    [DataField]
    public Dictionary<PersistentPoiWorksCategory, int> Limits = new();

    [DataField]
    public float ProductionSpeed = 1f;

    [DataField]
    public int ExtraStorage;

    [DataField]
    public float StockRewardMultiplier = 1f;
}

[Prototype("persistentPoiCapability")]
public sealed partial class PersistentPoiCapabilityPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public LocId Description;

    [DataField]
    public int Priority;
}

[RegisterComponent, NetworkedComponent]
public sealed partial class PersistentPoiWorksComponent : Component
{
    [DataField(required: true)]
    public PersistentPoiWorksCategory Category;
}

[RegisterComponent]
public sealed partial class PersistentPoiAdministrationConsoleComponent : Component;

[Serializable, NetSerializable]
public enum PersistentPoiAdministrationUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class PersistentPoiLimitState
{
    public PersistentPoiWorksCategory Category;
    public int Used;

    public int? Limit;

    public int? NextLimit;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiCostState
{
    public string Material = string.Empty;

    public int Deposited;
    public int Required;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiAdministrationState : BoundUserInterfaceState
{
    public string? Owner;
    public string RegionName = string.Empty;
    public int Level;
    public int MaxLevel;

    public string? Capability;

    public List<PersistentPoiLimitState> Limits = new();

    public List<PersistentPoiCostState> NextLevelCost = new();

    public bool CanUpgrade;
}

[Serializable, NetSerializable]
public sealed class PersistentPoiUpgradeMessage : BoundUserInterfaceMessage;
