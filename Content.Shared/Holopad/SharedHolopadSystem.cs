using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Content.Shared.Interaction;
using Content.Shared.UserInterface;
using Robust.Shared.Timing;

namespace Content.Shared.Holopad;

public abstract class SharedHolopadSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ISharedAdminManager _admin = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Eclipsion - clicking a pad while mapping renames it instead of opening the call window
        SubscribeLocalEvent<HolopadComponent, ActivateInWorldEvent>(OnMappingActivate, before: [typeof(ActivatableUISystem)]);
    }

    private void OnMappingActivate(Entity<HolopadComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !CanMappingRename(ent, args.User))
            return;

        args.Handled = true;
        OpenRenameDialog(ent, args.User);
    }

    public bool CanMappingRename(EntityUid pad, EntityUid user)
    {
        var mapUid = Transform(pad).MapUid;
        if (mapUid == null || _map.IsInitialized(mapUid))
            return false;

        return _admin.HasAdminFlag(user, AdminFlags.Mapping);
    }

    protected virtual void OpenRenameDialog(Entity<HolopadComponent> ent, EntityUid user)
    {
    }

    public bool IsHolopadControlLocked(Entity<HolopadComponent> entity, EntityUid? user = null)
    {
        if (entity.Comp.ControlLockoutStartTime == TimeSpan.Zero)
            return false;

        if (entity.Comp.ControlLockoutStartTime + TimeSpan.FromSeconds(entity.Comp.ControlLockoutDuration) < _timing.CurTime)
            return false;

        if (entity.Comp.ControlLockoutOwner == null || entity.Comp.ControlLockoutOwner == user)
            return false;

        return true;
    }

    public TimeSpan GetHolopadControlLockedPeriod(Entity<HolopadComponent> entity)
    {
        return entity.Comp.ControlLockoutStartTime + TimeSpan.FromSeconds(entity.Comp.ControlLockoutDuration) - _timing.CurTime;
    }

    public bool IsHolopadBroadcastOnCoolDown(Entity<HolopadComponent> entity)
    {
        if (entity.Comp.ControlLockoutStartTime == TimeSpan.Zero)
            return false;

        if (entity.Comp.ControlLockoutStartTime + TimeSpan.FromSeconds(entity.Comp.ControlLockoutCoolDown) < _timing.CurTime)
            return false;

        return true;
    }

    public TimeSpan GetHolopadBroadcastCoolDown(Entity<HolopadComponent> entity)
    {
        return entity.Comp.ControlLockoutStartTime + TimeSpan.FromSeconds(entity.Comp.ControlLockoutCoolDown) - _timing.CurTime;
    }
}
