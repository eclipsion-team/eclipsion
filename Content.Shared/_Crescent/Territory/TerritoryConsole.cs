using Content.Shared.Access;
using Content.Shared.CaptureFlag;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[RegisterComponent]
public sealed partial class TerritoryConsoleComponent : Component
{
    [DataField]
    public string? Faction;

    [DataField]
    public List<HashSet<ProtoId<AccessLevelPrototype>>> CommandAccess = new();

    [ViewVariables]
    public string? Selected;

    [ViewVariables]
    public TimeSpan NextRename;
}

[Serializable, NetSerializable]
public enum TerritoryConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class TerritoryConsoleState : BoundUserInterfaceState
{
    public string? Faction;

    public bool CanHoldTerritory;

    public List<TerritorySummary> Territories = new();

    public string? Selected;

    public PersistentPoiAdministrationState? Administration;

    public PersistentPoiProductionConsoleState? Production;

    public bool ProductionLinked;

    public TerritoryDefenceState? Defence;

    public string? CustomName;

    public string? NamePrefix;

    public bool SelectedHeld;

    public bool CanCommand;
}

[Serializable, NetSerializable]
public sealed class TerritoryDefenceState
{
    public bool AutoDefense;

    public bool ExclusionZone;

    public bool TurretsArmed;

    public int FireControl;

    public int FireControlPowered;

    public bool Manned;

    public string? Target;

    public int Guns;

    public int? GunLimit;

    public int Turrets;

    public bool RemoteFireAvailable;
}

[Serializable, NetSerializable]
public sealed class TerritoryConsoleRemoteFireMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public enum TerritoryDefenceOrder : byte
{
    AutoDefense,
    ExclusionZone,
    TurretsArmed,
}

[Serializable, NetSerializable]
public sealed class TerritoryConsoleDefenceMessage(TerritoryDefenceOrder order, bool enabled) : BoundUserInterfaceMessage
{
    public readonly TerritoryDefenceOrder Order = order;
    public readonly bool Enabled = enabled;
}

[Serializable, NetSerializable]
public sealed class TerritoryConsoleRenameMessage(string name) : BoundUserInterfaceMessage
{
    public readonly string Name = name;
}

[Serializable, NetSerializable]
public sealed class TerritoryConsoleAbandonMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class TerritorySummary
{
    public string RegionId = string.Empty;
    public string Name = string.Empty;
    public string? Owner;
    public int Level = 1;

    public bool Loaded;

    public CaptureFlagStage Stage;

    public string? Attacker;

    public float Progress;

    public int Works;

    public int WorksRunning;

    public int Stored;
}

[Serializable, NetSerializable]
public sealed class TerritoryConsoleSelectMessage(string regionId) : BoundUserInterfaceMessage
{
    public readonly string RegionId = regionId;
}
