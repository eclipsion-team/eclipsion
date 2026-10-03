using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Popups;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Whitelist;
using Content.Shared._Crescent.RoundEnd;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.RoundEnd;

/// <summary>
/// Backs the faction round-end mission computer. Clicking the computer with a mission item in hand consumes the
/// item immediately and permanently, advancing that mission's progress. Once every requirement is met an
/// authorized member turns the mission in: the (placeholder) reward is granted via
/// <see cref="FactionMissionCompletedEvent"/>. Each mission is one-and-done. Once a console's whole directive set
/// is finished it broadcasts the faction's payoff message to everyone in bold orange (server chat style).
/// </summary>
public sealed class FactionRoundEndComputerSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;

    private EntityQuery<StackComponent> _stackQuery;

    public override void Initialize()
    {
        base.Initialize();

        _stackQuery = GetEntityQuery<StackComponent>();

        SubscribeLocalEvent<FactionRoundEndComputerComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<FactionRoundEndComputerComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FactionRoundEndComputerComponent, FactionRoundEndSubmitMessage>(OnSubmit);
        SubscribeLocalEvent<FactionRoundEndComputerComponent, ExaminedEvent>(OnExamine);
    }

    private void OnExamine(EntityUid uid, FactionRoundEndComputerComponent comp, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("faction-roundend-examine"));
    }

    private void OnOpened(EntityUid uid, FactionRoundEndComputerComponent comp, BoundUIOpenedEvent args)
    {
        UpdateUi(uid, comp);
    }

    /// <summary>
    /// Feed a mission item in. The item is destroyed for good and its count advances the first matching, not-yet-full
    /// requirement of each mission. Items nothing wants are left in hand so normal tool use still works.
    /// </summary>
    private void OnInteractUsing(EntityUid uid, FactionRoundEndComputerComponent comp, InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        var count = _stackQuery.CompOrNull(args.Used)?.Count ?? 1;
        var advanced = false;

        foreach (var missionId in comp.Missions)
        {
            if (comp.Completed.Contains(missionId) || !_proto.TryIndex(missionId, out var mission))
                continue;

            var progress = EnsureProgress(comp, mission);
            for (var i = 0; i < mission.Entries.Count; i++)
            {
                var entry = mission.Entries[i];
                if (progress[i] >= entry.Amount || !MatchesEntry(args.Used, entry))
                    continue;

                progress[i] = Math.Min(entry.Amount, progress[i] + count);
                advanced = true;
                break; // one requirement per mission per item
            }
        }

        if (!advanced)
            return;

        QueueDel(args.Used);
        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("faction-roundend-item-accepted"), uid, args.User);
        UpdateUi(uid, comp);
    }

    private void OnSubmit(EntityUid uid, FactionRoundEndComputerComponent comp, FactionRoundEndSubmitMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (TryComp<AccessReaderComponent>(uid, out var reader) && !_accessReader.IsAllowed(actor, uid, reader))
        {
            _popup.PopupEntity(Loc.GetString("faction-roundend-denied"), uid, actor);
            return;
        }

        if (comp.Completed.Contains(args.MissionId))
            return;

        if (!comp.Missions.Contains(args.MissionId) || !_proto.TryIndex<FactionMissionPrototype>(args.MissionId, out var mission))
            return;

        if (!IsComplete(comp, mission))
        {
            _popup.PopupEntity(Loc.GetString("faction-roundend-incomplete"), uid, actor);
            UpdateUi(uid, comp);
            return;
        }

        comp.Completed.Add(mission.ID);

        // Reward hook (placeholder): any configured spawns, then the event a dedicated reward system can react to.
        var coords = Transform(uid).Coordinates;
        foreach (var spawn in mission.RewardSpawns)
            Spawn(spawn, coords);

        foreach (var rule in mission.RewardGameRules)
            _gameTicker.StartGameRule(rule);

        var ev = new FactionMissionCompletedEvent(uid, actor, comp.Faction, mission);
        RaiseLocalEvent(uid, ref ev);

        _adminLogger.Add(LogType.Action, LogImpact.High,
            $"{ToPrettyString(actor):player} completed faction mission {mission.ID} ({comp.Faction}) at {ToPrettyString(uid):console}");

        // The faction's payoff beat, in bold orange to everyone — only once the whole directive set is done.
        if (comp.FinaleAnnouncement is { } finale && AllMissionsComplete(comp))
            _chat.DispatchServerAnnouncement(Loc.GetString(finale));

        _popup.PopupEntity(Loc.GetString("faction-roundend-completed"), actor, actor);
        UpdateUi(uid, comp);
    }

    private void UpdateUi(EntityUid uid, FactionRoundEndComputerComponent comp)
    {
        var missions = new List<FactionMissionView>();
        foreach (var missionId in comp.Missions)
        {
            if (_proto.TryIndex(missionId, out var mission))
                missions.Add(BuildView(comp, mission));
        }

        _ui.SetUiState(uid, FactionRoundEndUiKey.Key, new FactionRoundEndConsoleState(comp.Faction, missions));
    }

    private FactionMissionView BuildView(FactionRoundEndComputerComponent comp, FactionMissionPrototype mission)
    {
        var view = new FactionMissionView
        {
            MissionId = mission.ID,
            Name = !string.IsNullOrEmpty(mission.Name.Id) ? Loc.GetString(mission.Name) : mission.ID,
            Description = !string.IsNullOrEmpty(mission.Description.Id) ? Loc.GetString(mission.Description) : string.Empty,
            Completed = comp.Completed.Contains(mission.ID),
        };

        var progress = comp.Delivered.GetValueOrDefault(mission.ID);
        var ready = true;
        for (var i = 0; i < mission.Entries.Count; i++)
        {
            var entry = mission.Entries[i];
            var delivered = progress != null && i < progress.Count ? progress[i] : 0;
            if (delivered < entry.Amount)
                ready = false;

            view.Items.Add(new FactionMissionItemView
            {
                Name = !string.IsNullOrEmpty(entry.Name.Id) ? Loc.GetString(entry.Name) : "?",
                Required = entry.Amount,
                Delivered = Math.Min(delivered, entry.Amount),
            });
        }

        view.Ready = ready && !view.Completed;
        return view;
    }

    /// <summary>Whether every directive this console offers has been turned in.</summary>
    private bool AllMissionsComplete(FactionRoundEndComputerComponent comp)
    {
        foreach (var missionId in comp.Missions)
        {
            if (!comp.Completed.Contains(missionId))
                return false;
        }

        return true;
    }

    private bool IsComplete(FactionRoundEndComputerComponent comp, FactionMissionPrototype mission)
    {
        var progress = comp.Delivered.GetValueOrDefault(mission.ID);
        if (progress == null)
            return mission.Entries.Count == 0;

        for (var i = 0; i < mission.Entries.Count; i++)
        {
            if (i >= progress.Count || progress[i] < mission.Entries[i].Amount)
                return false;
        }

        return true;
    }

    /// <summary>Gets (creating/resizing if needed) the per-entry delivered counters for a mission.</summary>
    private List<int> EnsureProgress(FactionRoundEndComputerComponent comp, FactionMissionPrototype mission)
    {
        if (comp.Delivered.TryGetValue(mission.ID, out var list) && list.Count == mission.Entries.Count)
            return list;

        list = new List<int>(mission.Entries.Count);
        for (var i = 0; i < mission.Entries.Count; i++)
            list.Add(0);

        comp.Delivered[mission.ID] = list;
        return list;
    }

    private bool MatchesEntry(EntityUid item, FactionMissionItemEntry entry)
    {
        if (!_whitelist.IsWhitelistPass(entry.Whitelist, item))
            return false;

        if (entry.Blacklist != null && _whitelist.IsBlacklistPass(entry.Blacklist, item))
            return false;

        return true;
    }
}
