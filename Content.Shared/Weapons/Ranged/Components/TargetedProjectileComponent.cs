using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Weapons.Ranged.Components;

// State is handled manually in SharedGunSystem: the target is often deleted while the projectile is still in
// flight, and the auto-generated state would call GetNetEntity on the dead uid for every player, every send.
[RegisterComponent, NetworkedComponent]
[Access(typeof(SharedGunSystem))]
public sealed partial class TargetedProjectileComponent : Component
{
    [DataField]
    public EntityUid? Target; // Goob edit - if null it hits everything
}

[Serializable, NetSerializable]
public sealed class TargetedProjectileComponentState : ComponentState
{
    public readonly NetEntity? Target;

    public TargetedProjectileComponentState(NetEntity? target)
    {
        Target = target;
    }
}
