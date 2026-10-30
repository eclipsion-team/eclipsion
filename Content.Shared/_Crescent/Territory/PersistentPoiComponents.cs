using System.Numerics;
using Content.Shared.Actions;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PersistentPoiGridComponent : Component
{
    [DataField, AutoNetworkedField]
    public string RegionId = string.Empty;

    [ViewVariables, AutoNetworkedField]
    public Vector2 ProtectedCenter;

    [ViewVariables, AutoNetworkedField]
    public float ProtectedRadius;

    [DataField, AutoNetworkedField]
    public string? Owner;

    [ViewVariables, AutoNetworkedField]
    public int Level = 1;

    [ViewVariables, AutoNetworkedField]
    public ProtoId<PersistentPoiCapabilityPrototype>? Capability;

    [ViewVariables, AutoNetworkedField]
    public Dictionary<PersistentPoiWorksCategory, int> WorksCounts = new();

    [DataField]
    public bool AutoDefense = true;

    [DataField]
    public bool ExclusionZone;

    [DataField]
    public bool TurretsArmed = true;

    [ViewVariables]
    public TimeSpan OvertimeUntil;

    [ViewVariables]
    public float OvertimeSpeed = 1f;

    public void ResetDefenceOrders()
    {
        AutoDefense = true;
        ExclusionZone = false;
        TurretsArmed = true;
    }

    public bool ProtectsTile(Vector2i indices)
    {
        if (ProtectedRadius <= 0f)
            return false;

        var centre = new Vector2(indices.X + 0.5f, indices.Y + 0.5f);
        return Vector2.DistanceSquared(centre, ProtectedCenter) <= ProtectedRadius * ProtectedRadius;
    }
}

[RegisterComponent, NetworkedComponent]
public sealed partial class PersistentPoiStructureComponent : Component
{
    [DataField]
    public List<EntProtoId> SpawnOnBuild = new();

    [DataField]
    public List<EntityUid> Spawned = new();

    [DataField]
    public EntityUid? SpawnedBy;
}

[RegisterComponent]
public sealed partial class PersistentPoiFurnishingComponent : Component;

[RegisterComponent, NetworkedComponent]
public sealed partial class PersistentPoiBuilderComponent : Component
{
    [DataField]
    public EntProtoId Action = "ActionPersistentPoiBuild";

    [DataField]
    public EntityUid? ActionEntity;
}

[RegisterComponent]
public sealed partial class PersistentPoiBuildMenuComponent : Component
{
    [DataField(required: true)]
    public List<PersistentPoiBuildCategory> Categories = new();
}

public sealed partial class PersistentPoiBuildActionEvent : InstantActionEvent;

[Serializable, NetSerializable]
public enum PersistentPoiBuildUiKey : byte
{
    Key,
}
