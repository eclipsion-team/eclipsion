using Content.Server.DeviceLinking.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Territory;
using Content.Shared.PointCannons;
using Content.Shared.Shuttles.Systems;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryDefenceSystem : EntitySystem
{
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly SharedShuttleSystem _shuttle = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiGridLoadedEvent>(OnGridLoaded);
        SubscribeLocalEvent<PersistentRegionOwnerChangedEvent>(OnOwnerChanged);
        SubscribeLocalEvent<SignalControlComponent, PersistentPoiBuiltEvent>(OnTurretBuilt);
    }

    private void OnGridLoaded(ref PersistentPoiGridLoadedEvent args)
    {
        if (TryComp<PersistentPoiGridComponent>(args.Grid, out var territory))
            ApplyTurrets(args.Grid, territory);
    }

    private void OnOwnerChanged(ref PersistentRegionOwnerChangedEvent args)
    {
        if (!TryComp<PersistentPoiGridComponent>(args.Grid, out var territory))
            return;

        territory.ResetDefenceOrders();
        ApplyTurrets(args.Grid, territory);
    }

    private void OnTurretBuilt(Entity<SignalControlComponent> ent, ref PersistentPoiBuiltEvent args)
    {
        if (args.Category == PersistentPoiWorksCategory.AntiBoarderTurret &&
            Transform(ent).GridUid is { } grid &&
            TryComp<PersistentPoiGridComponent>(grid, out var territory))
        {
            ent.Comp.IsOn = territory.TurretsArmed;
        }
    }

    public void SetOrder(EntityUid grid, PersistentPoiGridComponent territory, TerritoryDefenceOrder order, bool enabled)
    {
        switch (order)
        {
            case TerritoryDefenceOrder.AutoDefense:
                territory.AutoDefense = enabled;
                break;
            case TerritoryDefenceOrder.ExclusionZone:
                territory.ExclusionZone = enabled;
                break;
            case TerritoryDefenceOrder.TurretsArmed:
                territory.TurretsArmed = enabled;
                ApplyTurrets(grid, territory);
                break;
        }
    }

    private void ApplyTurrets(EntityUid grid, PersistentPoiGridComponent territory)
    {
        var turrets = AllEntityQuery<PersistentPoiWorksComponent, SignalControlComponent, TransformComponent>();
        while (turrets.MoveNext(out _, out var works, out var control, out var xform))
        {
            if (works.Category == PersistentPoiWorksCategory.AntiBoarderTurret && xform.GridUid == grid)
                control.IsOn = territory.TurretsArmed;
        }
    }

    public TerritoryDefenceState BuildState(EntityUid grid, PersistentPoiGridComponent territory)
    {
        var state = new TerritoryDefenceState
        {
            AutoDefense = territory.AutoDefense,
            ExclusionZone = territory.ExclusionZone,
            TurretsArmed = territory.TurretsArmed,
            Guns = territory.WorksCounts.GetValueOrDefault(PersistentPoiWorksCategory.PointDefence),
            GunLimit = _levels.GetLimit(territory, PersistentPoiWorksCategory.PointDefence),
            Turrets = territory.WorksCounts.GetValueOrDefault(PersistentPoiWorksCategory.AntiBoarderTurret),
        };

        var consoles = EntityQueryEnumerator<TerritoryAutoDefenseComponent, TransformComponent>();
        while (consoles.MoveNext(out var uid, out var defense, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            state.FireControl++;
            if (this.IsPowered(uid, EntityManager))
                state.FireControlPowered++;

            if (_ui.IsUiOpen(uid, TargetingConsoleUiKey.Key))
                state.Manned = true;

            if (state.Target == null && defense.TargetGrid is { } target && !TerminatingOrDeleted(target))
                state.Target = _shuttle.GetIFFLabel(target) ?? Name(target);
        }

        return state;
    }
}
