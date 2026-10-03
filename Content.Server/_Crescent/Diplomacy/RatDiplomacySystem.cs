using System.Linq;
using Content.Server.Announcements.Systems;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Diplomacy;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Diplomacy;

public sealed partial class RatDiplomacySystem : EntitySystem
{
    [Dependency] private readonly AnnouncerSystem _announcer = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    // The legacy TFSC id represents the TFCF's shared external diplomacy. IPM, SAW, GSC and CD retain their
    // internal leadership, but their jobs write HullrotFaction "TFSC" so Federation relations stay unified.
    // Load() drops relations naming a faction that is no longer on this roster, so old saves are fine.
    private static readonly string[] AllFactions =
        ["DSM", "NCWL", "SHI", "SRM", "TAP", "TFSC", "CMM"];

    /// <summary>
    /// Faction pairs locked into permanent war. They start at war and no peace, alliance or
    /// trade proposal between them can ever be created or accepted, so the war can't be ended.
    /// Pairs are unordered.
    /// </summary>
    private static readonly (string A, string B)[] PermanentEnemyPairs =
    {
        ("DSM", "NCWL"),
        ("SRM", "TAP"),
    };

    private static bool IsPermanentEnemyPair(string f1, string f2)
    {
        foreach (var (a, b) in PermanentEnemyPairs)
        {
            if ((f1 == a && f2 == b) || (f1 == b && f2 == a))
                return true;
        }

        return false;
    }

    private readonly Dictionary<string, Dictionary<string, FactionRelation>> _relations = new();
    private readonly Dictionary<string, List<PendingProposal>> _pending = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DiplomacyConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyDeclareWarMessage>(OnDeclareWar);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyProposePeaceMessage>(OnProposePeace);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyProposeAllianceMessage>(OnProposeAlliance);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyProposeTradeMessage>(OnProposeTrade);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyBreakTradeMessage>(OnBreakTrade);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyAcceptProposalMessage>(OnAcceptProposal);
        SubscribeLocalEvent<DiplomacyConsoleComponent, DiplomacyRejectProposalMessage>(OnRejectProposal);

        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

        InitializePersistence();

        // Defaults first, then last round's wars replayed over the top of them.
        InitRelations();
        Load();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    private void InitRelations()
    {
        foreach (var factionId in AllFactions)
        {
            _relations[factionId] = new Dictionary<string, FactionRelation>();
            foreach (var other in AllFactions)
            {
                if (factionId != other)
                    _relations[factionId][other] = FactionRelation.Neutral;
            }

            _pending[factionId] = new List<PendingProposal>();
        }

        // The TFCF's strategic conflict is with Shinohara; its four member organizations remain commercial actors
        // that need customers elsewhere in the sector. Everyone else starts neutral and Federation-wide relations
        // are settled at the diplomacy console under the Administrator's joint mandate.
        _relations["TFSC"]["SHI"] = FactionRelation.War;
        _relations["SHI"]["TFSC"] = FactionRelation.War;

        // Lock permanent-enemy pairs (e.g. DSM vs NCWL) into war.
        foreach (var (a, b) in PermanentEnemyPairs)
        {
            _relations[a][b] = FactionRelation.War;
            _relations[b][a] = FactionRelation.War;
        }
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.InGame)
            return;

        var session = args.Session;
        Timer.Spawn(500, () => SendPlayerFaction(session));
    }

    private void SendPlayerFaction(ICommonSession session)
    {
        // Runs off a timer, so the player may have disconnected in the meantime; sending to a dead channel throws.
        if (session.Status != SessionStatus.InGame)
            return;

        var faction = GetPlayerFaction(session.AttachedEntity);
        if (faction != null)
            RaiseNetworkEvent(new PlayerFactionUpdatedEvent(faction), session);

        // The HUD widget has no relations of its own to fall back on, and relations now carry between rounds —
        // without this a joining player sees a blank board until someone happens to work a console. Sent even
        // without a faction: players join in the lobby with no body, and faction HUDs need the board regardless.
        RaiseNetworkEvent(BuildRelationsEvent(), session);
    }

    private void OnUiOpened(EntityUid uid, DiplomacyConsoleComponent comp, BoundUIOpenedEvent args)
    {
        var faction = GetPlayerFaction(args.Actor);
        if (faction == null)
            return;

        SendConsoleState(uid, faction);
    }

    private void OnDeclareWar(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyDeclareWarMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null || myFaction == msg.TargetFactionId)
            return;

        // Check if war is already declared
        if (_relations.TryGetValue(myFaction, out var myRels) &&
            myRels.TryGetValue(msg.TargetFactionId, out var rel) &&
            rel == FactionRelation.War)
            return;

        SetRelation(myFaction, msg.TargetFactionId, FactionRelation.War);

        var fromName = Loc.GetString($"diplomacy-faction-{myFaction}");
        var toName = Loc.GetString($"diplomacy-faction-{msg.TargetFactionId}");
        Announce(Loc.GetString("diplomacy-announce-war", ("faction1", fromName), ("faction2", toName)),
            new Color(1f, 0.27f, 0.27f), _announcer.GetAnnouncementId("diplomacy-war"));

        RefreshAll();
    }

    private void OnProposePeace(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyProposePeaceMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null || myFaction == msg.TargetFactionId)
            return;

        // Permanent enemies can never make peace.
        if (IsPermanentEnemyPair(myFaction, msg.TargetFactionId))
            return;

        // Check if peace or alliance is already active
        if (_relations.TryGetValue(myFaction, out var myRels) &&
            myRels.TryGetValue(msg.TargetFactionId, out var rel) &&
            (rel == FactionRelation.Peace || rel == FactionRelation.Alliance))
            return;

        // The target id arrives from the client, so an unknown one must bounce rather than index the
        // dictionary blind — a faction that has been retired from the roster would throw here.
        if (!_pending.TryGetValue(msg.TargetFactionId, out var targetPending))
            return;

        if (targetPending.Any(p => p.FromFactionId == myFaction && p.Type == PendingProposalType.Peace))
            return;

        targetPending.Add(new PendingProposal
        {
            FromFactionId = myFaction,
            ToFactionId = msg.TargetFactionId,
            Type = PendingProposalType.Peace
        });

        var fromName = Loc.GetString($"diplomacy-faction-{myFaction}");
        var toName = Loc.GetString($"diplomacy-faction-{msg.TargetFactionId}");
        Announce(Loc.GetString("diplomacy-announce-peace-proposal", ("from", fromName), ("to", toName)),
            new Color(0.53f, 0.8f, 1f));

        RefreshAll();
    }

    private void OnProposeAlliance(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyProposeAllianceMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null || myFaction == msg.TargetFactionId)
            return;

        // Permanent enemies can never ally.
        if (IsPermanentEnemyPair(myFaction, msg.TargetFactionId))
            return;

        // Check if alliance is already active
        if (_relations.TryGetValue(myFaction, out var myRels) &&
            myRels.TryGetValue(msg.TargetFactionId, out var rel) &&
            rel == FactionRelation.Alliance)
            return;

        if (!_pending.TryGetValue(msg.TargetFactionId, out var targetPending))
            return;

        if (targetPending.Any(p => p.FromFactionId == myFaction && p.Type == PendingProposalType.Alliance))
            return;

        targetPending.Add(new PendingProposal
        {
            FromFactionId = myFaction,
            ToFactionId = msg.TargetFactionId,
            Type = PendingProposalType.Alliance
        });

        var fromName = Loc.GetString($"diplomacy-faction-{myFaction}");
        var toName = Loc.GetString($"diplomacy-faction-{msg.TargetFactionId}");
        Announce(Loc.GetString("diplomacy-announce-alliance-proposal", ("from", fromName), ("to", toName)),
            new Color(0.53f, 0.8f, 1f));

        RefreshAll();
    }

    private void OnProposeTrade(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyProposeTradeMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null || myFaction == msg.TargetFactionId)
            return;

        // Permanent enemies can never trade.
        if (IsPermanentEnemyPair(myFaction, msg.TargetFactionId))
            return;

        // Check if trade is already active
        if (_relations.TryGetValue(myFaction, out var myRels) &&
            myRels.TryGetValue(msg.TargetFactionId, out var rel) &&
            rel == FactionRelation.Trade)
            return;

        if (!_pending.TryGetValue(msg.TargetFactionId, out var targetPending))
            return;

        if (targetPending.Any(p => p.FromFactionId == myFaction && p.Type == PendingProposalType.Trade))
            return;

        targetPending.Add(new PendingProposal
        {
            FromFactionId = myFaction,
            ToFactionId = msg.TargetFactionId,
            Type = PendingProposalType.Trade
        });

        // Trade proposals are deliberately silent — no announcement.
        RefreshAll();
    }

    private void OnBreakTrade(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyBreakTradeMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null || myFaction == msg.TargetFactionId)
            return;

        // Check if trade is not active
        if (!_relations.TryGetValue(myFaction, out var myRels) ||
            !myRels.TryGetValue(msg.TargetFactionId, out var rel) ||
            rel != FactionRelation.Trade)
            return;

        SetRelation(myFaction, msg.TargetFactionId, FactionRelation.Neutral);

        // Breaking a trade deal is deliberately silent — no announcement.
        RefreshAll();
    }

    private void OnAcceptProposal(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyAcceptProposalMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null)
            return;

        if (!_pending.TryGetValue(myFaction, out var myPending))
            return;

        var proposal = myPending.FirstOrDefault(p =>
            p.FromFactionId == msg.FromFactionId && p.Type == msg.Type);

        if (proposal == null)
            return;

        myPending.Remove(proposal);

        // A proposal predating the lock (or a crafted one) must never lift a permanent war.
        if (IsPermanentEnemyPair(myFaction, proposal.FromFactionId))
        {
            RefreshAll();
            return;
        }

        var newRelation = msg.Type switch
        {
            PendingProposalType.Peace => FactionRelation.Peace,
            PendingProposalType.Alliance => FactionRelation.Alliance,
            PendingProposalType.Trade => FactionRelation.Trade,
            _ => FactionRelation.Neutral
        };
        SetRelation(myFaction, proposal.FromFactionId, newRelation);

        var fromName = Loc.GetString($"diplomacy-faction-{proposal.FromFactionId}");
        var toName = Loc.GetString($"diplomacy-faction-{myFaction}");
        var (key, soundId) = msg.Type switch
        {
            PendingProposalType.Peace => ("diplomacy-announce-peace-accepted", _announcer.GetAnnouncementId("diplomacy-peace")),
            PendingProposalType.Alliance => ("diplomacy-announce-alliance-accepted", _announcer.GetAnnouncementId("diplomacy-alliance")),
            // Trade deals are deliberately silent — no announcement.
            _ => (null, null)
        };
        if (key != null)
            Announce(Loc.GetString(key, ("faction1", fromName), ("faction2", toName)),
                new Color(0.27f, 1f, 0.53f), soundId);

        RefreshAll();
    }

    private void OnRejectProposal(EntityUid uid, DiplomacyConsoleComponent comp, DiplomacyRejectProposalMessage msg)
    {
        var myFaction = GetPlayerFaction(msg.Actor);
        if (myFaction == null)
            return;

        if (!_pending.TryGetValue(myFaction, out var myPending))
            return;

        var proposal = myPending.FirstOrDefault(p =>
            p.FromFactionId == msg.FromFactionId && p.Type == msg.Type);

        if (proposal == null)
            return;

        myPending.Remove(proposal);

        var fromName = Loc.GetString($"diplomacy-faction-{proposal.FromFactionId}");
        var toName = Loc.GetString($"diplomacy-faction-{myFaction}");
        var key = msg.Type switch
        {
            PendingProposalType.Peace => "diplomacy-announce-peace-rejected",
            PendingProposalType.Alliance => "diplomacy-announce-alliance-rejected",
            // Trade deals are deliberately silent — no announcement.
            _ => null
        };
        if (key != null)
            Announce(Loc.GetString(key, ("from", fromName), ("to", toName)),
                new Color(1f, 0.53f, 0.27f));

        RefreshAll();
    }

    internal void SetRelation(string f1, string f2, FactionRelation rel, bool persist = true)
    {
        // Permanent enemies stay at war no matter what tries to change them.
        if (IsPermanentEnemyPair(f1, f2))
            rel = FactionRelation.War;

        if (!_relations.ContainsKey(f1)) _relations[f1] = new();
        if (!_relations.ContainsKey(f2)) _relations[f2] = new();

        var previous = _relations[f1].GetValueOrDefault(f2, FactionRelation.Neutral);

        _relations[f1][f2] = rel;
        _relations[f2][f1] = rel;

        // Written out immediately: a server that dies mid-round must not forget who was at war.
        if (persist)
            Save();

        // Only the crossing into war is announced, and never while replaying the save file — otherwise
        // every server start would re-declare last round's wars.
        if (rel == FactionRelation.War && previous != FactionRelation.War && !_loading)
        {
            var ev = new FactionsWentToWarEvent(f1, f2);
            RaiseLocalEvent(ref ev);
        }
    }

    /// <summary>
    /// Whether <paramref name="faction"/> has a seat at the diplomacy table at all. The spacers' IND does not, so
    /// no treaty ever speaks for them.
    /// </summary>
    public bool IsDiplomaticFaction(string faction)
    {
        return AllFactions.Contains(faction);
    }

    /// <summary>The relation currently in force between two factions.</summary>
    public FactionRelation GetRelation(string f1, string f2)
    {
        if (f1 == f2)
            return FactionRelation.Alliance;

        return _relations.TryGetValue(f1, out var relations)
            ? relations.GetValueOrDefault(f2, FactionRelation.Neutral)
            : FactionRelation.Neutral;
    }

    private string? GetPlayerFaction(EntityUid? entity)
    {
        if (entity == null || !entity.Value.Valid)
            return null;
        return TryComp<HullrotFactionComponent>(entity.Value, out var factionComp)
            ? factionComp.Faction
            : null;
    }

    private void Announce(string msg, Color color, string? announcementId = null)
    {
        if (announcementId != null)
        {
            _announcer.SendAnnouncement(announcementId, Filter.Broadcast(), msg,
                sender: Loc.GetString("diplomacy-announcer-name"), colorOverride: color);
        }
        else
        {
            _announcer.SendAnnouncementMessage("fallback", msg, sender: Loc.GetString("diplomacy-announcer-name"), colorOverride: color);
        }
    }

    private void RefreshAll()
    {
        BroadcastRelations();

        foreach (var session in _playerManager.Sessions)
        {
            var faction = GetPlayerFaction(session.AttachedEntity);
            if (faction != null)
                RaiseNetworkEvent(new PlayerFactionUpdatedEvent(faction), session);
        }

        var consoleQuery = EntityQueryEnumerator<DiplomacyConsoleComponent>();
        while (consoleQuery.MoveNext(out var uid, out _))
        {
            var actors = _ui.GetActors(uid, DiplomacyConsoleUiKey.Key);
            foreach (var actorUid in actors)
            {
                var faction = GetPlayerFaction(actorUid);
                if (faction != null)
                    SendConsoleState(uid, faction);
            }
        }
    }

    private void SendConsoleState(EntityUid consoleUid, string faction)
    {
        var relations = _relations.GetValueOrDefault(faction, new Dictionary<string, FactionRelation>());
        var pending = _pending.GetValueOrDefault(faction, new List<PendingProposal>());

        _ui.SetUiState(consoleUid, DiplomacyConsoleUiKey.Key,
            new DiplomacyConsoleBoundUserInterfaceState(relations, faction, pending));
    }

    private void BroadcastRelations()
    {
        RaiseNetworkEvent(BuildRelationsEvent(), Filter.Broadcast(), false);
    }

    private AllFactionRelationsUpdatedEvent BuildRelationsEvent()
    {
        return new AllFactionRelationsUpdatedEvent(
            _relations.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<string, FactionRelation>(kvp.Value)),
            _pending.ToDictionary(kvp => kvp.Key, kvp => new List<PendingProposal>(kvp.Value)));
    }
}
