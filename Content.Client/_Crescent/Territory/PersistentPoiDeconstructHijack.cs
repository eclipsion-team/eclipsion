using Content.Client.Gameplay;
using Robust.Client.Placement;
using Robust.Client.State;
using Robust.Shared.Map;

namespace Content.Client._Crescent.Territory;

public sealed class PersistentPoiDeconstructHijack : PlacementHijack
{
    private readonly PersistentPoiBuildSystem _build;
    private readonly IStateManager _state;
    private readonly SharedTransformSystem _transform;

    public PersistentPoiDeconstructHijack(PersistentPoiBuildSystem build, IStateManager state, SharedTransformSystem transform)
    {
        _build = build;
        _state = state;
        _transform = transform;
    }

    public override bool CanRotate => false;

    // bare floor: nothing clickable under the mouse, so the tile itself comes up
    public override bool HijackDeletion(EntityCoordinates coordinates)
    {
        if (_state.CurrentState is GameplayStateBase gameplay &&
            gameplay.GetClickedEntity(_transform.ToMapCoordinates(coordinates)) != null)
        {
            return false;
        }

        _build.RequestDeconstructTile(coordinates);
        return true;
    }

    public override bool HijackDeletion(EntityUid entity)
    {
        _build.RequestDeconstruct(entity);
        return true;
    }
}
