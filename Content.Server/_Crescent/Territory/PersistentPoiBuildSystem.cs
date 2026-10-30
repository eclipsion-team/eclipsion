using Content.Server.Popups;
using Content.Shared._Crescent.Territory;
using Content.Shared.Actions;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class PersistentPoiBuildSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private const string BuildMenuUi = "PersistentPoiBuildBoundUserInterface";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiBuilderComponent, ComponentShutdown>(OnBuilderShutdown);
        SubscribeLocalEvent<PersistentPoiBuilderComponent, PersistentPoiBuildActionEvent>(OnBuildAction);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshInterval;

        var players = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (players.MoveNext(out var uid, out _, out var xform))
        {
            if (CanBuild(uid, xform))
                EnsureBuilder(uid);
            else if (HasComp<PersistentPoiBuilderComponent>(uid))
                RemCompDeferred<PersistentPoiBuilderComponent>(uid);
        }

        var builders = EntityQueryEnumerator<PersistentPoiBuilderComponent>();
        while (builders.MoveNext(out var uid, out _))
        {
            if (!HasComp<ActorComponent>(uid))
                RemCompDeferred<PersistentPoiBuilderComponent>(uid);
        }
    }

    public bool CanBuild(EntityUid uid, TransformComponent? xform = null)
    {
        if (!Resolve(uid, ref xform) ||
            xform.GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner == null)
        {
            return false;
        }

        if (_mobState.IsIncapacitated(uid))
            return false;

        return string.Equals(PersistentTerritoryFactions.GetTeam(EntityManager, uid), territory.Owner, StringComparison.Ordinal);
    }

    private void EnsureBuilder(EntityUid uid)
    {
        if (HasComp<PersistentPoiBuilderComponent>(uid))
            return;

        var builder = AddComp<PersistentPoiBuilderComponent>(uid);
        _actions.AddAction(uid, ref builder.ActionEntity, builder.Action);

        _ui.SetUi(uid, PersistentPoiBuildUiKey.Key, new InterfaceData(BuildMenuUi, interactionRange: -1f));
    }

    private void OnBuilderShutdown(Entity<PersistentPoiBuilderComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent))
            return;

        _actions.RemoveAction(ent.Owner, ent.Comp.ActionEntity);
        ent.Comp.ActionEntity = null;
        _ui.CloseUi(ent.Owner, PersistentPoiBuildUiKey.Key);
    }

    private void OnBuildAction(Entity<PersistentPoiBuilderComponent> ent, ref PersistentPoiBuildActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        if (!CanBuild(ent))
        {
            _popup.PopupEntity(Loc.GetString("persistent-poi-build-not-held"), ent, ent, PopupType.SmallCaution);
            return;
        }

        _ui.TryToggleUi(ent.Owner, PersistentPoiBuildUiKey.Key, ent.Owner);
    }
}
