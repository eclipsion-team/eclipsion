using System.Diagnostics.CodeAnalysis;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
// Rat-start
using Content.Server.GameTicking.Events;
using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Prometheus;
using Robust.Server.Player;
using Robust.Shared.Map;
// Rat-end

namespace Content.Server.NPC.Systems
{
    /// <summary>
    ///     Handles NPCs running every tick.
    /// </summary>
    public sealed partial class NPCSystem : EntitySystem
    {
        [Dependency] private readonly IConfigurationManager _configurationManager = default!;
        [Dependency] private readonly HTNSystem _htn = default!;
        [Dependency] private readonly MobStateSystem _mobState = default!;

        /// <summary>
        /// Whether any NPCs are allowed to run at all.
        /// </summary>
        public bool Enabled { get; set; } = true;

        private int _maxUpdates;

        private int _count;

        // Rat-start
        private static readonly Gauge ActiveGauge = Metrics.CreateGauge(
            "npc_active_count",
            "Amount of NPCs that are actively processing");

        [Dependency] private readonly IPlayerManager _playerManager = default!;

        private bool _pauseWhenNoPlayersInRange;
        private float _playerPauseDistance;
        private float _playerDistanceCheckTimer;
        // Crescent: short so an NPC wakes up soon after a player walks up to it.
        private const float PlayerDistanceCheckInterval = 0.5f;

        private readonly List<(EntityUid Entity, EntityCoordinates Coords)> _playerPauseCandidates = new();
        private readonly HashSet<EntityUid> _activePlayers = new();
        private readonly object _lock = new();
        // Rat-end

        /// <inheritdoc />
        public override void Initialize()
        {
            base.Initialize();

            Subs.CVar(_configurationManager, CCVars.NPCEnabled, value => Enabled = value, true);
            Subs.CVar(_configurationManager, CCVars.NPCMaxUpdates, obj => _maxUpdates = obj, true);
            // Rat-start
            Subs.CVar(_configurationManager, CCVars.NPCPauseWhenNoPlayersInRange, value => _pauseWhenNoPlayersInRange = value, true);
            Subs.CVar(_configurationManager, CCVars.NPCPlayerPauseDistance, value => _playerPauseDistance = value, true);
            // Events
            SubscribeLocalEvent<RoundStartingEvent>(OnRoundStart);
            SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
            SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
            // Rat-end
        }

        public void OnPlayerNPCAttach(EntityUid uid, HTNComponent component, PlayerAttachedEvent args)
        {
            SleepNPC(uid, component);
        }

        public void OnPlayerNPCDetach(EntityUid uid, HTNComponent component, PlayerDetachedEvent args)
        {
            if (_mobState.IsIncapacitated(uid) || TerminatingOrDeleted(uid))
                return;

            // This NPC has an attached mind, so it should not wake up.
            if (TryComp<MindContainerComponent>(uid, out var mindContainer) && mindContainer.HasMind)
                return;

            WakeNPC(uid, component);
        }

        public void OnNPCMapInit(EntityUid uid, HTNComponent component, MapInitEvent args)
        {
            component.Blackboard.SetValue(NPCBlackboard.Owner, uid);
            WakeNPC(uid, component);
        }

        public void OnNPCShutdown(EntityUid uid, HTNComponent component, ComponentShutdown args)
        {
            SleepNPC(uid, component);
        }

        // Rat-start
        public void OnRoundStart(RoundStartingEvent ev)
        {
            lock (_lock)
            {
                _activePlayers.Clear();
            }
        }

        public void OnPlayerAttached(PlayerAttachedEvent args)
        {
            var playerUid = args.Entity;

            if (playerUid.IsValid() &&
             !HasComp<GhostComponent>(playerUid))
            {
                lock (_lock)
                {
                    _activePlayers.Add(playerUid);
                }
            }
        }

        public void OnPlayerDetached(PlayerDetachedEvent args)
        {
            var playerUid = args.Entity;
            
            if (playerUid.IsValid() &&
             !HasComp<GhostComponent>(playerUid))
            {
                lock (_lock)
                {
                    _activePlayers.Remove(playerUid);
                }
            }
        }
        // Rat-end

        /// <summary>
        /// Is the NPC awake and updating?
        /// </summary>
        public bool IsAwake(EntityUid uid, HTNComponent component, ActiveNPCComponent? active = null)
        {
            return Resolve(uid, ref active, false);
        }

        public bool TryGetNpc(EntityUid uid, [NotNullWhen(true)] out NPCComponent? component)
        {
            // If you add your own NPC components then add them here.

            if (TryComp<HTNComponent>(uid, out var htn))
            {
                component = htn;
                return true;
            }

            component = null;
            return false;
        }

        /// <summary>
        /// Allows the NPC to actively be updated.
        /// </summary>
        public void WakeNPC(EntityUid uid, HTNComponent? component = null)
        {
            if (!Resolve(uid, ref component, false))
            {
                return;
            }

            Log.Debug($"Waking {ToPrettyString(uid)}");
            EnsureComp<ActiveNPCComponent>(uid);
        }

        public void SleepNPC(EntityUid uid, HTNComponent? component = null)
        {
            if (!Resolve(uid, ref component, false))
            {
                return;
            }

            // Don't bother with an event
            if (TryComp<HTNComponent>(uid, out var htn))
            {
                if (htn.Plan != null)
                {
                    var currentOperator = htn.Plan.CurrentOperator;
                    _htn.ShutdownTask(currentOperator, htn.Blackboard, HTNOperatorStatus.Failed);
                    _htn.ShutdownPlan(htn);
                    htn.Plan = null;
                }
            }

            Log.Debug($"Sleeping {ToPrettyString(uid)}");
            RemComp<ActiveNPCComponent>(uid);
        }

        // Rat-start
        /// <inheritdoc />
        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            if (!Enabled)
                return;

            if (_pauseWhenNoPlayersInRange)
            {
                _playerDistanceCheckTimer += frameTime;
                if (_playerDistanceCheckTimer >= PlayerDistanceCheckInterval)
                {
                    _playerDistanceCheckTimer = 0f;
                    CheckPlayerDistancesAndPauseNPCs();
                }
            }

            _count = 0;
            _htn.UpdateNPC(ref _count, _maxUpdates, frameTime);

            ActiveGauge.Set(Count<ActiveNPCComponent>());
        }
        // Rat-end

        // Rat-start
        private void CheckPlayerDistancesAndPauseNPCs()
        {
            _playerPauseCandidates.Clear();
            lock (_lock)
            {
                foreach (var playerEnt in _activePlayers)
                {
                    if (!playerEnt.Valid || TerminatingOrDeleted(playerEnt))
                        continue;

                    // Crescent: only dead players stop counting. A crit / soft-crit player is still there, and the
                    // NPC that downed them shouldn't fall asleep on top of them.
                    if (TryComp<MobStateComponent>(playerEnt, out var state) && state.CurrentState == MobState.Dead)
                        continue;

                    _playerPauseCandidates.Add((playerEnt, Transform(playerEnt).Coordinates));
                }
            }


            var anyPlayers = _playerPauseCandidates.Count > 0;

            var npcQuery = EntityQueryEnumerator<HTNComponent, TransformComponent>();

            while (npcQuery.MoveNext(out var npcUid, out var htn, out var npcTransform))
            {
                if (HasComp<ActorComponent>(npcUid) ||
                    TryComp<MindContainerComponent>(npcUid, out var mindContainer) && mindContainer.HasMind)
                    continue;
                if (_mobState.IsIncapacitated(npcUid))
                    continue;

                var minDistance = htn.SleepPlayerCheckRangeOverride ?? _playerPauseDistance;
                var npcCoords = npcTransform.Coordinates;
                var hasNearbyPlayer = false;

                if (anyPlayers)
                {
                    foreach (var (_, playerCoords) in _playerPauseCandidates)
                    {
                        if (npcCoords.TryDistance(EntityManager, playerCoords, out var distance) &&
                            distance <= minDistance)
                        {
                            hasNearbyPlayer = true;
                            break;
                        }
                    }
                }

                var isAwake = IsAwake(npcUid, htn);

                if (!hasNearbyPlayer)
                {
                    if (isAwake)
                        SleepNPC(npcUid, htn);
                }
                else
                {
                    if (!isAwake)
                        WakeNPC(npcUid, htn);
                }
            }
        }
        // Rat-end

        public void OnMobStateChange(EntityUid uid, HTNComponent component, MobStateChangedEvent args)
        {
            if (HasComp<ActorComponent>(uid))
                return;

            switch (args.NewMobState)
            {
                case MobState.Alive:
                    WakeNPC(uid, component);
                    break;
                case MobState.Critical:
                case MobState.Dead:
                    SleepNPC(uid, component);
                    break;
            }
        }
    }
}
