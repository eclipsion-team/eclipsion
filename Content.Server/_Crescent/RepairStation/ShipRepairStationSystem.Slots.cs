using Content.Server.Atmos.Components;
using Content.Shared.Atmos;
using Content.Shared.Doors.Components;
using Content.Shared.Physics;
using Content.Shared.Tag;
using Content.Shared.Wall;
using Robust.Shared.Physics;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.RepairStation;

public sealed partial class ShipRepairStationSystem
{
    // What part of a tile a structure occupies. Two structures in clashing slots never stand on one tile,
    // so a rebuild into a slot someone else already fills would stack them.
    private enum SlotKind : byte
    {
        None,
        Wall,
        Window,
        DirWindow,
        Grille,
        Machine,
        Table,
    }

    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    private readonly Dictionary<string, (SlotKind Kind, AtmosDirection Air)> _slotCache = new();

    private (SlotKind Kind, AtmosDirection Air) GetSlot(string protoId, Angle rotation)
    {
        if (!_slotCache.TryGetValue(protoId, out var slot))
        {
            slot = _proto.TryIndex<EntityPrototype>(protoId, out var proto) ? ComputeSlot(proto) : (SlotKind.None, AtmosDirection.Invalid);
            _slotCache[protoId] = slot;
        }

        return slot.Kind == SlotKind.DirWindow ? (slot.Kind, RotateAir(slot.Air, rotation)) : slot;
    }

    private (SlotKind, AtmosDirection) ComputeSlot(EntityPrototype proto)
    {
        if (proto.TryComp<AirtightComponent>(out var airtight, _compFactory)
            && (AtmosDirection) airtight.InitialAirBlockedDirection is var air
            && air != AtmosDirection.All && air != AtmosDirection.Invalid)
        {
            return (SlotKind.DirWindow, air);
        }

        if (proto.TryComp<TagComponent>(out var tags, _compFactory) && _tag.HasTag(tags, WallTag)
            || proto.HasComp<DoorComponent>(_compFactory))
        {
            return (SlotKind.Wall, AtmosDirection.Invalid);
        }

        if (airtight != null)
            return (SlotKind.Window, AtmosDirection.Invalid);

        if (!proto.TryComp<FixturesComponent>(out var fixtures, _compFactory) || proto.HasComp<WallMountComponent>(_compFactory))
            return (SlotKind.None, AtmosDirection.Invalid);

        var layer = 0;
        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (fixture.Hard)
                layer |= fixture.CollisionLayer;
        }

        if ((layer & (int) CollisionGroup.GlassLayer) == (int) CollisionGroup.GlassLayer)
            return (SlotKind.Grille, AtmosDirection.Invalid);

        if (layer == (int) CollisionGroup.MachineLayer)
            return (SlotKind.Machine, AtmosDirection.Invalid);

        if (layer == (int) CollisionGroup.TableLayer)
            return (SlotKind.Table, AtmosDirection.Invalid);

        return (SlotKind.None, AtmosDirection.Invalid);
    }

    private static bool SlotsClash((SlotKind Kind, AtmosDirection Air) a, (SlotKind Kind, AtmosDirection Air) b)
    {
        if (a.Kind == SlotKind.None || b.Kind == SlotKind.None)
            return false;

        if (a.Kind == SlotKind.Wall || b.Kind == SlotKind.Wall)
            return true;

        return (a.Kind, b.Kind) switch
        {
            (SlotKind.Window, SlotKind.Window or SlotKind.DirWindow) => true,
            (SlotKind.DirWindow, SlotKind.Window) => true,
            (SlotKind.DirWindow, SlotKind.DirWindow) => (a.Air & b.Air) != 0,
            _ => a.Kind == b.Kind && a.Kind is SlotKind.Grille or SlotKind.Machine or SlotKind.Table,
        };
    }

    /// <summary>
    /// Whether something the slip did not file or build already fills the slot this part would go in.
    /// Wreckage the slip clears does not count.
    /// </summary>
    private bool SlotTaken(EntityUid grid, MapGridComponent gridComp, Vector2i tile, string protoId, Angle rotation, HashSet<EntityUid> filed)
    {
        var slot = GetSlot(protoId, rotation);
        if (slot.Kind == SlotKind.None)
            return false;

        foreach (var ent in _map.GetAnchoredEntities(grid, gridComp, tile))
        {
            if (filed.Contains(ent) || TerminatingOrDeleted(ent) || _drydock.IsClearable(ent))
                continue;

            if (MetaData(ent).EntityPrototype is not { } proto)
                continue;

            if (SlotsClash(slot, GetSlot(proto.ID, Transform(ent).LocalRotation)))
                return true;
        }

        return false;
    }

    private static AtmosDirection RotateAir(AtmosDirection dir, Angle angle)
    {
        if (angle == Angle.Zero)
            return dir;

        var rotated = AtmosDirection.Invalid;
        for (var i = 0; i < Atmospherics.Directions; i++)
        {
            var direction = (AtmosDirection) (1 << i);
            if (dir.IsFlagSet(direction))
                rotated |= (direction.ToAngle() + angle).ToAtmosDirectionCardinal();
        }

        return rotated;
    }
}
