using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Events;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Gives the IFF consoles of a <see cref="GreatHuntCloakedVesselComponent"/> station an unlimited cloak once the
/// station has its grids.
/// </summary>
public sealed class GreatHuntCloakedVesselSystem : EntitySystem
{
    [Dependency] private readonly ShuttleSystem _shuttle = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GreatHuntCloakedVesselComponent, StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(Entity<GreatHuntCloakedVesselComponent> ent, ref StationPostInitEvent args)
    {
        var grids = args.Station.Comp.Grids;
        var query = EntityQueryEnumerator<IFFConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var console, out var xform))
        {
            if (xform.GridUid is not { } grid || !grids.Contains(grid))
                continue;

            _shuttle.SetUnlimitedCloak(uid, console, ent.Comp.StartCloaked);
        }
    }
}
