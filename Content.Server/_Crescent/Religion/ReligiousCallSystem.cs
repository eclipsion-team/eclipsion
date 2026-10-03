using Content.Server._Crescent.Religion.Components;
using Content.Shared._Crescent.Religion;
using Content.Shared.Actions;
using Content.Shared.Bed.Sleep;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Religion;

/// <summary>
///     Faith figures call out to their faith, and the believers around them answer on their own. A character can lie
///     about their faith when asked, but not stay silent through a call without being noticed.
/// </summary>
public sealed partial class ReligiousCallSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReligionSystem _religion = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    ///     Answers are staggered so a crowd does not reply in one frame.
    /// </summary>
    private const float MinResponseDelay = 0.8f;
    private const float MaxResponseDelay = 3f;

    private readonly List<PendingResponse> _pending = new();

    private record struct PendingResponse(
        EntityUid Believer,
        ProtoId<ReligionPrototype> Religion,
        LocId Line,
        TimeSpan At);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ReligiousCallerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ReligiousCallerComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ReligiousCallerComponent, ReligiousCallActionEvent>(OnCall);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _pending.Clear());
    }

    private void OnStartup(Entity<ReligiousCallerComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent, ref ent.Comp.ActionEntity, ent.Comp.Action);
    }

    private void OnShutdown(Entity<ReligiousCallerComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent, ent.Comp.ActionEntity);
    }

    private void OnCall(Entity<ReligiousCallerComponent> ent, ref ReligiousCallActionEvent args)
    {
        if (args.Handled
            || !_proto.TryIndex(args.Religion, out var religion)
            || religion.CallLines.Count == 0
            || !_mobState.IsAlive(ent))
            return;

        args.Handled = true;
        _religion.SpeakRiteLine(ent, _random.Pick(religion.CallLines));

        if (religion.ResponseLines.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var believer in _lookup.GetEntitiesInRange<BelieverComponent>(Transform(ent).Coordinates,
                     religion.CallRange))
        {
            if (believer.Owner == ent.Owner || believer.Comp.Religion != religion.ID)
                continue;

            var delay = TimeSpan.FromSeconds(_random.NextFloat(MinResponseDelay, MaxResponseDelay));
            _pending.Add(new PendingResponse(believer, religion.ID, _random.Pick(religion.ResponseLines), now + delay));
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pending.Count == 0)
            return;

        var now = _timing.CurTime;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var response = _pending[i];
            if (response.At > now)
                continue;

            _pending.RemoveAt(i);

            // Whoever lost the faith, or went down, in the meantime has nothing to say. Bodies with nobody in them
            // or an SSD player still answer: the faith is the character's, not the player's.
            if (TerminatingOrDeleted(response.Believer)
                || !_religion.HoldsReligion(response.Believer, response.Religion)
                || _mobState.IsIncapacitated(response.Believer))
                continue;

            // A sleeper mumbles the answer anyway, so dozing off is no way to hide what you believe.
            _religion.SpeakRiteLine(response.Believer,
                response.Line,
                ignoreActionBlocker: HasComp<SleepingComponent>(response.Believer));
        }
    }
}
