using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.Territory;
using Content.Shared.GameTicking;
using Content.Shared.PointCannons;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryRemoteFireSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly FactionMachineSystem _factionMachines = default!;
    [Dependency] private readonly PvsOverrideSystem _pvs = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private const float LocalRange = 2f;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan UnsendDelay = TimeSpan.FromSeconds(3);

    private readonly List<(EntityUid Console, ICommonSession Session, TimeSpan At)> _pendingUnsend = new();

    private readonly Dictionary<EntityUid, Dictionary<EntityUid, EntityUid>> _operators = new();

    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TerritoryAutoDefenseComponent, BoundUserInterfaceMessageAttempt>(OnMessageAttempt);
        SubscribeLocalEvent<TerritoryAutoDefenseComponent, BoundUIClosedEvent>(OnClosed);
        SubscribeLocalEvent<TerritoryAutoDefenseComponent, EntityTerminatingEvent>(OnConsoleTerminating);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _operators.Clear();
            _pendingUnsend.Clear();
        });
    }

    public bool TryGetConsole(EntityUid grid, string faction, out EntityUid console, out bool unpowered)
    {
        console = default;
        unpowered = false;

        var consoles = EntityQueryEnumerator<TerritoryAutoDefenseComponent, TransformComponent>();
        while (consoles.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != grid ||
                !_ui.HasUi(uid, TargetingConsoleUiKey.Key) ||
                !string.Equals(_factionMachines.GetFaction(uid), faction, StringComparison.Ordinal))
            {
                continue;
            }

            if (!this.IsPowered(uid, EntityManager))
            {
                unpowered = true;
                continue;
            }

            console = uid;
            return true;
        }

        return false;
    }

    public bool TryTakeControl(
        EntityUid mainframe,
        string faction,
        EntityUid grid,
        EntityUid actor,
        [NotNullWhen(false)] out string? error)
    {
        if (!TryComp<ActorComponent>(actor, out var player))
        {
            error = "territory-remote-fire-no-console";
            return false;
        }

        if (!TryGetConsole(grid, faction, out var console, out var unpowered))
        {
            error = unpowered ? "territory-remote-fire-unpowered" : "territory-remote-fire-no-console";
            return false;
        }

        error = null;
        if (_ui.IsUiOpen(console, TargetingConsoleUiKey.Key, actor))
            return true;

        if (!_operators.TryGetValue(console, out var operators))
            _operators[console] = operators = new Dictionary<EntityUid, EntityUid>();

        operators[actor] = mainframe;
        _pvs.AddForceSend(console, player.PlayerSession);
        _ui.OpenUi(console, TargetingConsoleUiKey.Key, actor);
        return true;
    }

    public bool IsRemoteOperator(EntityUid console, EntityUid actor)
    {
        return _operators.TryGetValue(console, out var operators) && operators.ContainsKey(actor);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;

        for (var i = _pendingUnsend.Count - 1; i >= 0; i--)
        {
            var (console, session, at) = _pendingUnsend[i];
            if (now < at)
                continue;

            _pendingUnsend.RemoveAt(i);

            // taken up again in the meantime: it stays
            if (!TerminatingOrDeleted(console) &&
                !(session.AttachedEntity is { } actor && IsRemoteOperator(console, actor)))
            {
                _pvs.RemoveForceSend(console, session);
            }
        }

        var consoles = EntityQueryEnumerator<TerritoryAutoDefenseComponent>();
        while (consoles.MoveNext(out var console, out _))
        {
            if (!_ui.IsUiOpen(console, TargetingConsoleUiKey.Key))
                continue;

            foreach (var actor in _ui.GetActors(console, TargetingConsoleUiKey.Key).ToArray())
            {
                if (!IsAllowed(console, actor))
                    _ui.CloseUi(console, TargetingConsoleUiKey.Key, actor);
            }
        }
    }

    private void OnMessageAttempt(Entity<TerritoryAutoDefenseComponent> ent, ref BoundUserInterfaceMessageAttempt args)
    {
        if (!args.Cancelled && TargetingConsoleUiKey.Key.Equals(args.UiKey) && !IsAllowed(ent, args.Actor))
            args.Cancel();
    }

    private bool IsAllowed(EntityUid console, EntityUid actor)
    {
        return _operators.TryGetValue(console, out var operators) && operators.TryGetValue(actor, out var mainframe)
            ? CanStillOperate(console, actor, mainframe)
            : _transform.InRange(actor, console, LocalRange);
    }

    private bool CanStillOperate(EntityUid console, EntityUid actor, EntityUid mainframe)
    {
        return !TerminatingOrDeleted(mainframe) &&
               _ui.IsUiOpen(mainframe, TerritoryConsoleUiKey.Key, actor) &&
               TryComp<TerritoryConsoleComponent>(mainframe, out var command) &&
               command.Faction is { } faction &&
               Transform(console).GridUid is { } grid &&
               TryComp<PersistentPoiGridComponent>(grid, out var territory) &&
               string.Equals(territory.Owner, faction, StringComparison.Ordinal) &&
               this.IsPowered(console, EntityManager);
    }

    private void OnClosed(Entity<TerritoryAutoDefenseComponent> ent, ref BoundUIClosedEvent args)
    {
        if (!TargetingConsoleUiKey.Key.Equals(args.UiKey) ||
            !_operators.TryGetValue(ent, out var operators) ||
            !operators.Remove(args.Actor))
        {
            return;
        }

        if (operators.Count == 0)
            _operators.Remove(ent);

        if (TryComp<ActorComponent>(args.Actor, out var player))
            _pendingUnsend.Add((ent, player.PlayerSession, _timing.CurTime + UnsendDelay));
    }

    private void OnConsoleTerminating(Entity<TerritoryAutoDefenseComponent> ent, ref EntityTerminatingEvent args)
    {
        _operators.Remove(ent);
    }
}
