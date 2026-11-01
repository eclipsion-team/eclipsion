using Content.Server.Administration.Logs;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.FactionPortal;
using Content.Shared._Crescent.Territory;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Events;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.FactionPortal;

public sealed class FactionPortalSystem : SharedFactionPortalSystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly PointLightSystem _light = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private const string PortalFixture = "portal";
    private const float StepInRange = 1.25f;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ArrivalGrace = TimeSpan.FromSeconds(1.5);

    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FactionPortalComponent, StartCollideEvent>(OnStartCollide);
        SubscribeLocalEvent<FactionPortalComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<FactionPortalComponent, FactionPortalSelectMessage>(OnSelect);
        SubscribeLocalEvent<FactionPortalComponent, ExaminedEvent>(OnExamined);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextUpdate)
            return;

        _nextUpdate = _timing.CurTime + UpdateInterval;

        var query = EntityQueryEnumerator<FactionPortalComponent>();
        while (query.MoveNext(out var uid, out var portal))
        {
            var active = IsOpen((uid, portal));
            if (active != portal.Active)
            {
                portal.Active = active;
                _appearance.SetData(uid, FactionPortalVisuals.Active, active);
                _light.SetEnabled(uid, active);
            }

            if (_ui.IsUiOpen(uid, FactionPortalUiKey.Key))
                UpdateUi((uid, portal));
        }
    }

    private bool IsOpen(Entity<FactionPortalComponent> ent)
    {
        if (TerminatingOrDeleted(ent) || MetaData(ent).EntityPaused)
            return false;

        var xform = Transform(ent);
        if (!xform.Anchored || xform.GridUid is not { } grid || _container.IsEntityInContainer(ent))
            return false;

        if (GetFaction(ent.Owner).Length == 0)
            return false;

        if (ent.Comp.MinTerritoryLevel > 0
            && (!TryComp<PersistentPoiGridComponent>(grid, out var territory)
                || territory.Owner == null
                || territory.Level < ent.Comp.MinTerritoryLevel))
        {
            return false;
        }

        return !HasComp<ApcPowerReceiverComponent>(ent) || _power.IsPowered(ent);
    }

    private List<FactionPortalDestination> GetDestinations(Entity<FactionPortalComponent> ent)
    {
        var list = new List<FactionPortalDestination>();
        var faction = GetFaction(ent.Owner);
        var map = Transform(ent).MapID;
        if (faction.Length == 0)
            return list;

        var query = EntityQueryEnumerator<FactionPortalComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var portal, out var xform))
        {
            if (uid == ent.Owner || xform.MapID != map || !portal.Active || GetFaction(uid) != faction)
                continue;

            list.Add(new FactionPortalDestination(GetNetEntity(uid), GetName((uid, portal), xform)));
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private string GetName(Entity<FactionPortalComponent> ent, TransformComponent xform)
    {
        var grid = xform.GridUid is { } gridUid ? Name(gridUid) : Name(ent);
        return ent.Comp.Label is { } label ? $"{grid} - {label}" : grid;
    }

    private bool IsDestination(Entity<FactionPortalComponent> from, EntityUid to)
    {
        return to != from.Owner
               && TryComp<FactionPortalComponent>(to, out var target)
               && target.Active
               && Transform(to).MapID == Transform(from).MapID
               && GetFaction(to) == GetFaction(from.Owner);
    }

    private void UpdateUi(Entity<FactionPortalComponent> ent)
    {
        var destinations = GetDestinations(ent);
        if (ent.Comp.LastSent is { } last && ent.Comp.LastSentOpen == ent.Comp.Active && destinations.SequenceEqual(last))
            return;

        ent.Comp.LastSent = destinations;
        ent.Comp.LastSentOpen = ent.Comp.Active;
        _ui.SetUiState(ent.Owner, FactionPortalUiKey.Key, new FactionPortalBuiState(ent.Comp.Active, destinations));
    }

    private void OnUiOpened(Entity<FactionPortalComponent> ent, ref BoundUIOpenedEvent args)
    {
        ent.Comp.LastSent = null;
        UpdateUi(ent);
    }

    private void OnStartCollide(Entity<FactionPortalComponent> ent, ref StartCollideEvent args)
    {
        if (args.OurFixtureId != PortalFixture || !args.OtherFixture.Hard)
            return;

        var user = args.OtherEntity;
        if (!HasComp<ActorComponent>(user) || Transform(user).Anchored)
            return;

        var traveller = EnsureComp<FactionPortalTravellerComponent>(user);
        if (traveller.ArrivedAt == ent.Owner && _timing.CurTime < traveller.ArrivalGraceEnd)
            return;

        if (!ent.Comp.Active)
            return;

        if (!CanUse(ent, user))
        {
            Popup.PopupEntity(Loc.GetString("faction-portal-denied"), ent, user);
            return;
        }

        if (traveller.Portal == ent.Owner && traveller.Destination is { } destination && IsDestination(ent, destination))
        {
            Travel(ent, destination, user, traveller);
            return;
        }

        _ui.OpenUi(ent.Owner, FactionPortalUiKey.Key, user);
        Popup.PopupEntity(Loc.GetString("faction-portal-choose"), ent, user);
    }

    private void OnSelect(Entity<FactionPortalComponent> ent, ref FactionPortalSelectMessage args)
    {
        var user = args.Actor;
        if (!ent.Comp.Active || !CanUse(ent, user))
            return;

        if (!TryGetEntity(args.Destination, out var destination) || !IsDestination(ent, destination.Value))
        {
            Popup.PopupEntity(Loc.GetString("faction-portal-destination-gone"), ent, user);
            UpdateUi(ent);
            return;
        }

        var traveller = EnsureComp<FactionPortalTravellerComponent>(user);
        traveller.Portal = ent.Owner;
        traveller.Destination = destination;

        if (_transform.InRange(Transform(user).Coordinates, Transform(ent).Coordinates, StepInRange))
        {
            _ui.CloseUi(ent.Owner, FactionPortalUiKey.Key, user);
            Travel(ent, destination.Value, user, traveller);
            return;
        }

        Popup.PopupEntity(Loc.GetString("faction-portal-destination-set",
            ("destination", GetName((destination.Value, Comp<FactionPortalComponent>(destination.Value)), Transform(destination.Value)))),
            ent, user);
    }

    public bool TryTravel(Entity<FactionPortalComponent?> from, EntityUid to, EntityUid user)
    {
        if (!Resolve(from, ref from.Comp) || !from.Comp.Active || !CanUse(from, user) || !IsDestination((from, from.Comp), to))
            return false;

        Travel((from, from.Comp), to, user, EnsureComp<FactionPortalTravellerComponent>(user));
        return true;
    }

    private void Travel(Entity<FactionPortalComponent> from, EntityUid to, EntityUid user, FactionPortalTravellerComponent traveller)
    {
        var target = Transform(to).Coordinates;

        // whoever they're dragging goes with them
        EntityUid? dragged = null;
        if (TryComp<PullerComponent>(user, out var puller) && puller.Pulling is { } pulling
            && TryComp<PullableComponent>(pulling, out var pullable))
        {
            _pulling.TryStopPull(pulling, pullable, user, ignoreGrab: true);
            dragged = pulling;
        }

        if (TryComp<PullableComponent>(user, out var userPullable) && userPullable.BeingPulled)
            _pulling.TryStopPull(user, userPullable, ignoreGrab: true);

        _adminLog.Add(LogType.Teleport, LogImpact.Low,
            $"{ToPrettyString(user):player} used faction portal {ToPrettyString(from):entity} to reach {ToPrettyString(to):entity}");

        _audio.PlayPvs(from.Comp.DepartureSound, from);

        traveller.ArrivedAt = to;
        traveller.ArrivalGraceEnd = _timing.CurTime + ArrivalGrace;
        _transform.SetCoordinates(user, target);

        if (dragged is { } draggedUid && !Transform(draggedUid).Anchored)
        {
            var draggedTraveller = EnsureComp<FactionPortalTravellerComponent>(draggedUid);
            draggedTraveller.ArrivedAt = to;
            draggedTraveller.ArrivalGraceEnd = traveller.ArrivalGraceEnd;
            _transform.SetCoordinates(draggedUid, target);
        }

        _audio.PlayPvs(Comp<FactionPortalComponent>(to).ArrivalSound, to);
    }

    private void OnExamined(Entity<FactionPortalComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString(ent.Comp.Active ? "faction-portal-examine-open" : "faction-portal-examine-closed"));
    }
}
