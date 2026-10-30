using Content.Server.Explosion.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Radio;
using Content.Server.EventScheduler;
using Content.Shared.Emp;
using Content.Shared.Examine;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server.Emp;

public sealed class EmpSystem : SharedEmpSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly EventSchedulerSystem _eventScheduler = default!;

    public const string EmpPulseEffectPrototype = "EffectEmpPulse";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EmpDisabledComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<EmpOnTriggerComponent, TriggerEvent>(HandleEmpTrigger);
        SubscribeLocalEvent<EmpDisabledComponent, EmpDisabledRemoved>(OnEmpDisabledRemoved);

        SubscribeLocalEvent<EmpDisabledComponent, RadioSendAttemptEvent>(OnRadioSendAttempt);
        SubscribeLocalEvent<EmpDisabledComponent, RadioReceiveAttemptEvent>(OnRadioReceiveAttempt);
    }

    /// <summary>
    ///   Triggers an EMP pulse at the given location, by first raising an <see cref="EmpAttemptEvent"/>, then a raising <see cref="EmpPulseEvent"/> on all entities in range.
    /// </summary>
    /// <param name="coordinates">The location to trigger the EMP pulse at.</param>
    /// <param name="range">The range of the EMP pulse.</param>
    /// <param name="energyConsumption">The amount of energy consumed by the EMP pulse.</param>
    /// <param name="duration">The duration of the EMP effects.</param>
    /// <param name="maxDrainFraction">Most of a battery's max charge one pulse can take.</param>
    /// <param name="hitsToDisable">Pulses within <see cref="BuildupWindow"/> before a target gets disabled.</param>
    public void EmpPulse(MapCoordinates coordinates, float range, float energyConsumption, float duration,
        float maxDrainFraction = 1f, int hitsToDisable = 1)
    {
        foreach (var uid in _lookup.GetEntitiesInRange(coordinates, range))
        {
            TryEmpEffects(uid, energyConsumption, duration, maxDrainFraction, hitsToDisable);
        }
        Spawn(EmpPulseEffectPrototype, coordinates);
    }

    // eclipsion - hits further apart than this start the count over
    public static readonly TimeSpan BuildupWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    ///    Attempts to apply the effects of an EMP pulse onto an entity by first raising an <see cref="EmpAttemptEvent"/>, followed by raising a <see cref="EmpPulseEvent"/> on it.
    /// </summary>
    /// <param name="uid">The entity to apply the EMP effects on.</param>
    /// <param name="energyConsumption">The amount of energy consumed by the EMP.</param>
    /// <param name="duration">The duration of the EMP effects.</param>
    public void TryEmpEffects(EntityUid uid, float energyConsumption, float duration,
        float maxDrainFraction = 1f, int hitsToDisable = 1)
    {
        var attemptEv = new EmpAttemptEvent();
        RaiseLocalEvent(uid, attemptEv);
        if (attemptEv.Cancelled)
            return;

        DoEmpEffects(uid, energyConsumption, duration, maxDrainFraction, hitsToDisable);
    }

    /// <summary>
    ///    Applies the effects of an EMP pulse onto an entity by raising a <see cref="EmpPulseEvent"/> on it.
    /// </summary>
    /// <param name="uid">The entity to apply the EMP effects on.</param>
    /// <param name="energyConsumption">The amount of energy consumed by the EMP.</param>
    /// <param name="duration">The duration of the EMP effects.</param>
    public void DoEmpEffects(EntityUid uid, float energyConsumption, float duration,
        float maxDrainFraction = 1f, int hitsToDisable = 1)
    {
        TimeSpan delay = TimeSpan.FromSeconds(duration);

        var ev = new EmpPulseEvent(energyConsumption, false, false, delay) { MaxDrainFraction = maxDrainFraction };
        RaiseLocalEvent(uid, ref ev);
        if (ev.Affected)
        {
            Spawn(EmpDisabledEffectPrototype, Transform(uid).Coordinates);
        }
        if (ev.Disabled && BuildUp(uid, hitsToDisable))
        {
            var disabled = EnsureComp<EmpDisabledComponent>(uid);
            var until = Timing.CurTime + delay;
            // eclipsion - postponing added the whole delay on every hit, so a burst of rounds kept things dead for
            // minutes. a new hit now only pushes the end out to its own duration
            if (disabled.LastDelayedEvent != null)
            {
                if (until > disabled.TargetTime && _eventScheduler.TryRescheduleDelayedEvent(disabled.LastDelayedEvent, until))
                    disabled.TargetTime = until;
            }
            else
            {

                var dEv = new EmpDisabledRemoved();
                disabled.LastDelayedEvent = _eventScheduler.DelayEvent(uid, ref dEv, delay);
                disabled.TargetTime = until;
            }

            /// i tried my best to go through the Pow3r server code but i literally couldn't find in relation to PowerNetworkBatteryComponent that uses the event system
            /// the code is otherwise too esoteric for my innocent eyes
            if (TryComp<PowerNetworkBatteryComponent>(uid, out var powerNetBattery))
            {
                powerNetBattery.CanCharge = false;
            }
        }
    }

    // true once this hit is the one that shuts the target down
    private bool BuildUp(EntityUid uid, int hitsToDisable)
    {
        if (hitsToDisable <= 1 || HasComp<EmpDisabledComponent>(uid))
            return true;

        var buildup = EnsureComp<EmpBuildupComponent>(uid);
        if (Timing.CurTime - buildup.LastHit > BuildupWindow)
            buildup.Hits = 0;

        buildup.LastHit = Timing.CurTime;
        if (++buildup.Hits < hitsToDisable)
            return false;

        RemComp<EmpBuildupComponent>(uid);
        return true;
    }

    private void OnEmpDisabledRemoved(EntityUid uid, EmpDisabledComponent component, EmpDisabledRemoved args)
    {
        RemComp<EmpDisabledComponent>(uid);
        var ev = new EmpDisabledRemoved();
        RaiseLocalEvent(uid, ref ev);

        if (TryComp<PowerNetworkBatteryComponent>(uid, out var powerNetBattery))
        {
            powerNetBattery.CanCharge = true;
        }
    }

    private void OnExamine(EntityUid uid, EmpDisabledComponent component, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("emp-disabled-comp-on-examine"));
    }

    private void HandleEmpTrigger(EntityUid uid, EmpOnTriggerComponent comp, TriggerEvent args)
    {
        EmpPulse(_transform.GetMapCoordinates(uid), comp.Range, comp.EnergyConsumption, comp.DisableDuration,
            comp.MaxDrainFraction, comp.HitsToDisable);
        args.Handled = true;
    }

    private void OnRadioSendAttempt(EntityUid uid, EmpDisabledComponent component, ref RadioSendAttemptEvent args)
    {
        args.Cancelled = true;
    }

    private void OnRadioReceiveAttempt(EntityUid uid, EmpDisabledComponent component, ref RadioReceiveAttemptEvent args)
    {
        args.Cancelled = true;
    }
}

/// <summary>
/// Raised on an entity before <see cref="EmpPulseEvent"/>. Cancel this to prevent the emp event being raised.
/// </summary>
public sealed partial class EmpAttemptEvent : CancellableEntityEventArgs
{
}

[ByRefEvent]
public record struct EmpPulseEvent(float EnergyConsumption, bool Affected, bool Disabled, TimeSpan Duration)
{
    // eclipsion - most of a battery's max charge this pulse may take
    public float MaxDrainFraction { get; init; } = 1f;
}

[ByRefEvent]
public record struct EmpDisabledRemoved();
