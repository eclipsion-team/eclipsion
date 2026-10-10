using Content.Shared._Crescent.ShipShields;
using Robust.Shared.Physics.Systems;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Content.Server.Power.Components;
using Content.Server._Crescent.UnionfallCapturePoint;
using Content.Shared.Interaction;
using Content.Shared.Preferences;
using Content.Server.Preferences.Managers;
using Robust.Shared.Network;
using Content.Shared._Crescent.HullrotFaction;
using Robust.Shared.Player;
using Content.Server.Announcements.Systems;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Content.Server.Popups;
using Content.Server.DoAfter;
using Content.Shared.Item.ItemToggle.Components;
using Robust.Shared.Serialization;
using Content.Shared.DoAfter;
using Content.Shared._Crescent.UnionfallCapturePoint;
using Robust.Shared.Timing;
using Content.Shared._Crescent.UnionfallShipNode;
using Content.Server.Explosion.EntitySystems;
using Content.Shared.Destructible;
using Content.Server._Crescent.NuclearBlast;


namespace Content.Server._Crescent.UnionfallCapturePoint;

public sealed class UnionfallShipNodeSystem : EntitySystem
{
    [Dependency] private readonly AnnouncerSystem _announcer = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly ExplosionSystem _explosionSystem = default!;
    [Dependency] private readonly NuclearBlastSystem _nuclearBlast = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;

    private ISawmill _sawmill = default!;

    private bool isRoundEnding = false;

    public override void Initialize()
    {
        SubscribeLocalEvent<UnionfallShipNodeComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<UnionfallShipNodeComponent, ActivateInWorldEvent>(OnActivatedInWorld);
        SubscribeLocalEvent<UnionfallShipNodeComponent, UnionfallShipNodeDoAfterEvent>(OnCaptureDoAfter);
        SubscribeLocalEvent<UnionfallShipNodeComponent, DestructionEventArgs>(OnDestruction);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("unionfall.shipnodes");
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        isRoundEnding = false;
    }

    private void OnComponentInit(EntityUid uid, UnionfallShipNodeComponent component, ComponentInit args)
    {

    }

    private (int dsm, int ncwl) CountRemainingNodes(EntityUid destroyed)
    {
        int dsm = 0;
        int ncwl = 0;
        var query = EntityQueryEnumerator<UnionfallShipNodeComponent>();
        while (query.MoveNext(out var uid, out var node))
        {
            // Nodes caught in the same nuclear blast are queued for deletion but not gone yet.
            if (uid == destroyed || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
                continue;

            if (node.OwningFaction == "DSM") dsm++;
            else if (node.OwningFaction == "NCWL") ncwl++;
        }
        return (dsm, ncwl);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<UnionfallShipNodeComponent>();
        while (query.MoveNext(out var uid, out var capturepoint))
        {
            capturepoint.GracePeriod -= frameTime;
            if (capturepoint.GracePeriod > 0f)
                continue;
            if (capturepoint.IsBeingCaptured == false)
                continue;

            capturepoint.CurrentCaptureProgress -= frameTime;

            if (capturepoint.CurrentCaptureProgress <= 0)
            {
                var eventArgs = new DestructionEventArgs();
                RaiseLocalEvent(uid, eventArgs);
                QueueDel(uid);
            }
        }
    }

    private void OnActivatedInWorld(EntityUid uid, UnionfallShipNodeComponent component, ActivateInWorldEvent args)
    {
        if (component.GracePeriod > 0)
        {
            _popup.PopupEntity(Loc.GetString("shipnode-grace-period-fail"), uid, args.User);
            return;
        }

        if (!TryComp<HullrotFactionComponent>(args.User, out var comp))
            return;
        string faction = comp.Faction;

        if (component.OwningFaction == faction & component.IsBeingCaptured == false)
        {
            _popup.PopupEntity(Loc.GetString("shipnode-same-faction-fail"), uid, args.User);
            return;
        }

        if (component.OwningFaction == faction)
            _popup.PopupEntity(Loc.GetString("shipnode-defusing"), uid, args.User);
        else
            _popup.PopupEntity(Loc.GetString("shipnode-sabotaging"), uid, args.User);

        DoAfterArgs doAfterArguments = new DoAfterArgs(EntityManager, args.User, component.DoAfterDelay, new UnionfallShipNodeDoAfterEvent(), uid, uid, null)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
        };

        _doAfter.TryStartDoAfter(doAfterArguments, null);
    }

    private void OnCaptureDoAfter(EntityUid uid, UnionfallShipNodeComponent component, UnionfallShipNodeDoAfterEvent args)
    {
        if (args.Cancelled)
            return;
        if (args.Target is null)
            return;

        if (!TryComp<HullrotFactionComponent>(args.User, out var comp))
            return;
        string faction = comp.Faction;

        if (component.OwningFaction != comp.Faction)
        {
            component.IsBeingCaptured = true;
            _announcer.SendAnnouncement(_announcer.GetAnnouncementId("unionfallPointCapture"), Filter.Broadcast(),
                "A " + component.OwningFaction + " cloner database has been rigged to explode! It will detonate in " + float.Round(component.CurrentCaptureProgress).ToString() + " seconds.");
        }
        else if (component.OwningFaction == faction)
        {
            component.IsBeingCaptured = false;
            component.CurrentCaptureProgress = component.TimeToCapture;
            _announcer.SendAnnouncement(_announcer.GetAnnouncementId("unionfallPointCapture"), Filter.Broadcast(),
                "The " + component.OwningFaction + " cloner database has been defused.");
        }
    }

    private void OnDestruction(EntityUid uid, UnionfallShipNodeComponent capturepoint, DestructionEventArgs args)
    {
        if (!_nuclearBlast.TryDetonate(uid))
            _explosionSystem.TriggerExplosive(uid);
        if (isRoundEnding)
            return;

        var (dsm, ncwl) = CountRemainingNodes(uid);

        if (ncwl <= 0 || dsm <= 0)
        {
            // The blast can take other nodes down with this one, so the side that just ran out is not necessarily
            // the side that owned it.
            var loser = dsm <= 0 && ncwl > 0 ? "DSM"
                : ncwl <= 0 && dsm > 0 ? "NCWL"
                : capturepoint.OwningFaction;

            isRoundEnding = true;
            _announcer.SendAnnouncement(_announcer.GetAnnouncementId("Fallback"), Filter.Broadcast(),
                loser + " has lost all of their warships and cloner databases. They are doomed to a slow death in Taypan.");
            _gameTicker.EndRound("All of " + loser + "'s cloner databases have been destroyed. ROUND OVER");
            capturepoint.CurrentCaptureProgress = 999999;
            // An admin may restart by hand during the countdown; the timer must not then restart the next round too.
            var roundId = _gameTicker.RoundId;
            Timer.Spawn(TimeSpan.FromMinutes(1), () =>
            {
                if (_gameTicker.RoundId == roundId && _gameTicker.RunLevel == GameRunLevel.PostRound)
                    _gameTicker.RestartRound();
            });
        }
        else
        {
            _announcer.SendAnnouncement(_announcer.GetAnnouncementId("Fallback"), Filter.Broadcast(),
                "A " + capturepoint.OwningFaction + " cloner database has been destroyed! | REMAINING FOR DSM: " + dsm + " | REMAINING FOR NCWL: " + ncwl);
        }
    }
}