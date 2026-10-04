using Content.Client.Resources;
using Content.Client.Shuttles.UI;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.UI.MapObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Client.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    [Dependency] private readonly IResourceCache _resource = default!;

    /// <summary>
    /// Gets the parallax to use for the specified map or uses the fallback if not available.
    /// </summary>
    public Texture GetTexture(Entity<ShuttleMapParallaxComponent?> entity)
    {
        if (!Resolve(entity, ref entity.Comp, false))
        {
            return _resource.GetTexture(ShuttleMapParallaxComponent.FallbackTexture);
        }

        return _resource.GetTexture(entity.Comp.TexturePath);
    }

    /// <summary>
    /// Gets the map coordinates of a map object.
    /// </summary>
    public MapCoordinates GetMapCoordinates(IMapObject mapObj)
    {
        switch (mapObj)
        {
            case ShuttleBeaconObject beacon:
                try
                {
                    var coords = GetCoordinates(beacon.Coordinates);
                    if (coords.EntityId == EntityUid.Invalid || !coords.IsValid(EntityManager))
                    {
                        return MapCoordinates.Nullspace;
                    }
                    return XformSystem.ToMapCoordinates(coords);
                }
                catch (KeyNotFoundException)
                {
                    return MapCoordinates.Nullspace;
                }
            case ShuttleExclusionObject exclusion:
                try
                {
                    var coords = GetCoordinates(exclusion.Coordinates);
                    if (coords.EntityId == EntityUid.Invalid || !coords.IsValid(EntityManager))
                    {
                        return MapCoordinates.Nullspace;
                    }
                    return XformSystem.ToMapCoordinates(coords);
                }
                catch (KeyNotFoundException)
                {
                    return MapCoordinates.Nullspace;
                }
            case GridMapObject grid:
                // The grid can leave PVS or get deleted while the map is still drawing it.
                if (!TryComp(grid.Entity, out TransformComponent? gridXform))
                    return MapCoordinates.Nullspace;

                if (HasComp<MapComponent>(grid.Entity))
                {
                    return new MapCoordinates(gridXform.LocalPosition, gridXform.MapID);
                }

                Entity<PhysicsComponent?, TransformComponent?> gridEnt = (grid.Entity, null, gridXform);
                return new MapCoordinates(Maps.GetGridPosition(gridEnt), gridXform.MapID);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
