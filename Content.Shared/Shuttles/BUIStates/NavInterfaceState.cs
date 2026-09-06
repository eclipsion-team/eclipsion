using System.Numerics;
using Content.Shared._NF.Shuttles.Events;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.BUIStates;

[Serializable, NetSerializable]
public sealed class NavInterfaceState
{
    public Content.Shared.PointCannons.ShipWeaponTargetingMode WeaponTargetingMode;

    public float MaxRange;

    /// <summary>
    /// The relevant coordinates to base the radar around.
    /// </summary>
    public NetCoordinates? Coordinates;

    /// <summary>
    /// The relevant rotation to rotate the angle around.
    /// </summary>
    public double Angle;

    public Dictionary<NetEntity, List<DockingPortState>> Docks;

    /// <summary>
    /// Frontier - the state of the shuttle's inertial dampeners
    /// </summary>
    public InertiaDampeningMode DampeningMode;

    /// <summary>
    /// Hullrot - target console for this UI state
    /// </summary>
    public NetEntity console;

    /// <summary>
    /// Hullrot - keep this aligned to world?
    /// </summary>
    public bool AlignToWorld = false;

    /// <summary>
    /// A settable target to show on radar
    /// </summary>
    public Vector2? Target { get; set; }

    /// <summary>
    /// A settable target to show on radar
    /// </summary>
    public NetEntity? TargetEntity { get; set; }

    /// <summary>
    /// Whether or not to show the target coords
    /// </summary>
    public bool HideTarget = true;


    public NavInterfaceState(
        float maxRange,
        NetCoordinates? coordinates,
        double angle,
        Dictionary<NetEntity, List<DockingPortState>> docks,
        InertiaDampeningMode dampeningMode, // Frontier: add dampeningMode
        NetEntity Console,
        Vector2? target = null,
        NetEntity? targetEntity = null,
        bool hideTarget = true)
    {
        MaxRange = maxRange;
        Coordinates = coordinates;
        Angle = angle;
        Docks = docks;
        DampeningMode = dampeningMode; // Frontier
        console = Console;
        Target = target;
        TargetEntity = targetEntity;
        HideTarget = hideTarget;
    }
}

[Serializable, NetSerializable]
public enum RadarConsoleUiKey : byte
{
    Key
}
 // hullrot added
[Serializable, NetSerializable]
public sealed class NavConsoleGroupPressedMessage(int payload) : BoundUserInterfaceMessage
{
    public int Payload = payload;
}
