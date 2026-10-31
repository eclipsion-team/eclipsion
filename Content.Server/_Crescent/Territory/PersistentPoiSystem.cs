using Content.Server.Construction.Components;
using Content.Server.Popups;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Stack;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Crescent.Territory;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Content.Shared.PointCannons;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiSystem : EntitySystem
{
    [Dependency] private readonly SharedHardpointSystem _hardpoints = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentRegionRegisteredEvent>(OnRegionRegistered);
        SubscribeLocalEvent<PersistentPoiGridLoadedEvent>(OnGridLoaded);
        SubscribeLocalEvent<PersistentPoiStructureComponent, MapInitEvent>(OnStructureMapInit);
        SubscribeLocalEvent<PersistentPoiStructureComponent, EntityTerminatingEvent>(OnStructureTerminating);
        SubscribeLocalEvent<PersistentPoiStructureComponent, PersistentPoiBuiltEvent>(OnStructureBuilt);
    }

    private void OnStructureBuilt(Entity<PersistentPoiStructureComponent> ent, ref PersistentPoiBuiltEvent args)
    {
        if (Transform(ent).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            _levels.GetLimit(territory, args.Category) is not { } limit)
        {
            return;
        }

        var finished = IsCounted(ent, args.Category);
        var count = _levels.CountWorksLive(grid, args.Category) + (finished ? 0 : 1);
        if (count <= limit)
            return;

        RefundConstruction(ent);

        if (args.User is { } user)
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-build-over-limit"), ent, user, PopupType.MediumCaution);
            Log.Info($"{ToPrettyString(user)} finished {ToPrettyString(ent)} past territory '{territory.RegionId}''s {args.Category} limit; refunded.");
        }

        if (finished)
            QueueDel(ent);
        else
            Del(ent);
    }

    private bool IsCounted(EntityUid uid, PersistentPoiWorksCategory category)
    {
        if (category == PersistentPoiWorksCategory.PointDefence)
            return HasComp<PointCannonComponent>(uid);

        return TryComp<PersistentPoiWorksComponent>(uid, out var works) && works.Category == category;
    }

    private void RefundConstruction(EntityUid uid)
    {
        if (!TryComp<ConstructionComponent>(uid, out var construction) ||
            !_proto.TryIndex<ConstructionGraphPrototype>(construction.Graph, out var graph) ||
            graph.Start is not { } startNode ||
            !graph.Nodes.TryGetValue(startNode, out var node) ||
            graph.Path(startNode, construction.TargetNode ?? construction.Node) is not { } path)
        {
            return;
        }

        var coordinates = Transform(uid).Coordinates;
        foreach (var next in path)
        {
            if (node.GetEdge(next.Name) is not { } edge)
                break;

            foreach (var step in edge.Steps)
            {
                if (step is MaterialConstructionGraphStep material &&
                    _proto.TryIndex(material.MaterialPrototypeId, out var stack))
                {
                    _stack.Spawn(material.Amount, stack, coordinates);
                }
            }

            node = next;
        }
    }

    private void OnRegionRegistered(ref PersistentRegionRegisteredEvent args)
    {
        Anchor(args.Grid);
        SetPermanentFloor(args.Region, args.Grid);
    }

    private void SetPermanentFloor(EntityUid standard, EntityUid grid)
    {
        if (!TryComp<PersistentPoiGridComponent>(grid, out var territory))
            return;

        territory.ProtectedCenter = _transform.WithEntityId(Transform(standard).Coordinates, grid).Position;
        territory.ProtectedRadius = TryComp<PersistentCaptureRegionComponent>(standard, out var region)
            ? MathF.Max(0f, region.ProtectedCoreRadius)
            : 0f;

        Dirty(grid, territory);
    }

    private void Anchor(EntityUid grid)
    {
        _shuttle.Disable(grid);

        if (TryComp<ShuttleComponent>(grid, out var shuttle))
            shuttle.Enabled = false;

        EnsureComp<PreventPilotComponent>(grid);

        if (TryComp<MapGridComponent>(grid, out var mapGrid))
            mapGrid.CanSplit = false;
    }

    private void OnGridLoaded(ref PersistentPoiGridLoadedEvent args)
    {
        var guns = AllEntityQuery<HardpointAnchorableOnlyComponent, TransformComponent>();
        while (guns.MoveNext(out var uid, out var weapon, out var xform))
        {
            if (xform.GridUid != args.Grid || !xform.Anchored || weapon.anchoredTo != null)
                continue;

            _hardpoints.TryAnchorToHardpoint(uid, weapon);
        }
    }

    private void OnStructureMapInit(Entity<PersistentPoiStructureComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.SpawnOnBuild.Count == 0)
            return;

        var xform = Transform(ent);
        foreach (var proto in ent.Comp.SpawnOnBuild)
        {
            var spawned = SpawnAttachedTo(proto, xform.Coordinates, rotation: xform.LocalRotation);
            ent.Comp.Spawned.Add(spawned);
            EnsureComp<PersistentPoiStructureComponent>(spawned).SpawnedBy = ent;
        }
    }

    private void OnStructureTerminating(Entity<PersistentPoiStructureComponent> ent, ref EntityTerminatingEvent args)
    {
        foreach (var spawned in ent.Comp.Spawned)
        {
            if (!TerminatingOrDeleted(spawned))
                QueueDel(spawned);
        }

        if (ent.Comp.SpawnedBy is { } parent && !TerminatingOrDeleted(parent))
            QueueDel(parent);
    }
}
