using Content.Client.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Client.Placement;
using Robust.Shared.Map;

namespace Content.Client._Crescent.Territory;

/// <summary>
/// Places a ghost as usual, unless the territory's material silo can cover the whole recipe: then it asks the
/// server to build it outright from the silo.
/// </summary>
public sealed class PersistentPoiPlacementHijack : PlacementHijack
{
    private readonly PersistentPoiBuildSystem _build;
    private readonly ConstructionPlacementHijack _ghost;
    private readonly ConstructionPrototype _recipe;

    public PersistentPoiPlacementHijack(PersistentPoiBuildSystem build, ConstructionPlacementHijack ghost, ConstructionPrototype recipe)
    {
        _build = build;
        _ghost = ghost;
        _recipe = recipe;
    }

    public override bool CanRotate => _ghost.CanRotate;

    public override bool HijackPlacementRequest(EntityCoordinates coordinates)
    {
        // from the works console there is no body to raise a ghost with, so it is the silo or nothing
        if (!_build.IsViewing && !_build.SiloCovers(_recipe))
            return _ghost.HijackPlacementRequest(coordinates);

        _build.RequestSiloBuild(_recipe, coordinates, Manager.Direction);
        return true;
    }

    public override bool HijackDeletion(EntityUid entity)
    {
        return _ghost.HijackDeletion(entity);
    }

    public override void StartHijack(PlacementManager manager)
    {
        base.StartHijack(manager);
        _ghost.StartHijack(manager);
    }
}
