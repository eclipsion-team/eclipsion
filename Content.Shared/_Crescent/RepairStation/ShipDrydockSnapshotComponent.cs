using System.Numerics;
using Content.Shared.Decals;
using Robust.Shared.Prototypes;

namespace Content.Shared._Crescent.RepairStation;

/// <summary>
/// The repair slip's own file of a hull, covering everything the scope prototypes take that the
/// hand-held repair device does not already track.
/// </summary>
/// <remarks>
/// Server-side only on purpose. It is neither networked nor saved with the map, so widening the
/// drydock's reach costs no bandwidth and leaves the device's snapshot - which every client does
/// receive - exactly the size it was.
/// </remarks>
[RegisterComponent]
public sealed partial class ShipDrydockSnapshotComponent : Component
{
    [ViewVariables]
    public List<DrydockPart> Parts = new();

    /// <summary>
    /// The deck markings the hull wore when it was last surveyed. Painted stripes, hazard blocks and
    /// the like go with the plating they were laid on, and the slip lays them back down with it.
    /// </summary>
    /// <remarks>
    /// Only markings that stay put are on file: nothing cleanable, since blood and soot are mess
    /// rather than livery, and nothing the directional tiling system draws, since that redraws itself
    /// the moment the plating is back.
    /// </remarks>
    [ViewVariables]
    public List<Decal> Decals = new();
}

/// <summary>
/// One structure as it stood when the hull was last surveyed.
/// </summary>
public sealed class DrydockPart
{
    public EntProtoId Proto;

    /// <summary>
    /// Tile the structure sat on, used to check whether something already stands in its place.
    /// </summary>
    public Vector2i Tile;

    public Vector2 LocalPosition;
    public Angle Rotation;

    /// <summary>
    /// The structure itself, while it lasts. Re-pointed at the replacement when the slip rebuilds it.
    /// </summary>
    public EntityUid? Original;

    /// <summary>
    /// Whether this is a weapon hardpoint. A gun only hunts for its mount as it spawns, so mounts are
    /// rebuilt before anything else that stands on the same tile.
    /// </summary>
    public bool IsMount;

    /// <summary>
    /// Whether this part's exact recorded entity must be destroyed before the slip may replace it.
    /// Captured with the snapshot so removing or changing components on the live entity cannot turn
    /// valuable equipment into an apparently unprotected missing part.
    /// </summary>
    public bool ReplacementRequiresDestruction;

    /// <summary>
    /// Signal links out of this part into other parts on file, with every port pair between the two.
    /// </summary>
    public List<(DrydockPart Sink, List<(string Source, string Sink)> Ports)> Links = new();

    /// <summary>
    /// Parts this one keeps in its device list (air alarms and the like).
    /// </summary>
    public List<DrydockPart> ListedDevices = new();
}
