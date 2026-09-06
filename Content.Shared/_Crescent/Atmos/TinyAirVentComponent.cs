using Content.Shared.Atmos;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Atmos;

/// <summary>
/// A portable vent that runs off a gas tank slotted into it instead of a pipe net.
/// Wrench it down anywhere and it bleeds the tank into the tile it sits on until the room reaches
/// <see cref="TargetPressure"/>, the tank can no longer push against the room, or someone unwrenches it.
/// Handled by TinyAirVentSystem on the server.
/// </summary>
[RegisterComponent]
public sealed partial class TinyAirVentComponent : Component
{
    /// <summary>
    /// Container id the gas tank sits in. Must match the ContainerContainer entry in yaml.
    /// </summary>
    [DataField]
    public string TankSlotId = "tank_slot";

    /// <summary>
    /// The slot the gas tank goes into.
    /// </summary>
    [DataField]
    public ItemSlot TankSlot = new();

    /// <summary>
    /// Stop releasing once the surrounding tile reaches this pressure, in kPa.
    /// </summary>
    [DataField]
    public float TargetPressure = Atmospherics.OneAtmosphere;

    /// <summary>
    /// How much the vent tries to raise the surrounding pressure by, in kPa per second.
    /// Deliberately slow - this is a field patch, not a replacement for a real atmos loop.
    /// </summary>
    [DataField]
    public float PressureRate = 15f;

    /// <summary>
    /// Ratio of room pressure to tank pressure the compressor can hold, same idea as the pipe vent's
    /// PumpPower. The vent keeps working until the room reaches tank pressure * this, so a tank does
    /// not stall out with most of its gas still stranded inside it.
    /// </summary>
    [DataField]
    public float PumpPower = 3f;

    /// <summary>
    /// What the vent did on its last atmos tick. Drives the sprite, ambience and examine text.
    /// </summary>
    [ViewVariables]
    public TinyAirVentState State = TinyAirVentState.Off;
}

[Serializable, NetSerializable]
public enum TinyAirVentVisuals : byte
{
    State,
}

[Serializable, NetSerializable]
public enum TinyAirVentState : byte
{
    /// <summary>
    /// Unanchored, no tank, room already full, or tank too weak to push any more gas out.
    /// </summary>
    Off,

    /// <summary>
    /// Actively emptying its tank into the room.
    /// </summary>
    Venting,
}
