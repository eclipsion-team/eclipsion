using System.Numerics;
using Content.Server.Destructible;
using Content.Server.Power.Components;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Damage;

/// <summary>
/// Powered machines, consoles and thrusters spark once badly damaged and catch small flames near destruction.
/// </summary>
public sealed class DamagedMachineEffectsSystem : EntitySystem
{
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private const float SparkFraction = 0.4f;
    private const float FlameFraction = 0.7f;

    private static readonly EntProtoId SparkEffect = "EffectSparks";
    private static readonly EntProtoId FlameEffect = "EffectDamagedMachineFlame";

    private static readonly SoundSpecifier SparkSound = new SoundCollectionSpecifier("sparksDeprecated",
        AudioParams.Default.WithVolume(-6f).WithVariation(0.15f).WithMaxDistance(8f));

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApcPowerReceiverComponent, DamageChangedEvent>(OnDamageChanged);
    }

    private void OnDamageChanged(Entity<ApcPowerReceiverComponent> ent, ref DamageChangedEvent args)
    {
        if (HasComp<MobStateComponent>(ent))
            return;

        var destroyedAt = _destructible.DestroyedAt(ent);
        if (destroyedAt == FixedPoint2.MaxValue || destroyedAt <= FixedPoint2.Zero)
            return;

        var fraction = (float) (args.Damageable.TotalDamage / destroyedAt);
        if (fraction < SparkFraction)
        {
            RemComp<ActiveDamagedMachineEffectsComponent>(ent);
            return;
        }

        var active = EnsureComp<ActiveDamagedMachineEffectsComponent>(ent);
        var wasBurning = active.Burning;
        active.Burning = fraction >= FlameFraction;

        // First crossing gives immediate feedback instead of waiting a full cycle.
        if (active.NextEffect == TimeSpan.Zero || active.Burning && !wasBurning)
            active.NextEffect = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat(0.2f, 1f));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ActiveDamagedMachineEffectsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var active, out var xform))
        {
            if (now < active.NextEffect)
                continue;

            var min = active.Burning ? 1.5f : 3f;
            var max = active.Burning ? 4f : 9f;
            active.NextEffect = now + TimeSpan.FromSeconds(_random.NextFloat(min, max));

            if (xform.GridUid == null)
                continue;

            var coords = xform.Coordinates.Offset(new Vector2(_random.NextFloat(-0.3f, 0.3f), _random.NextFloat(-0.3f, 0.3f)));
            Spawn(SparkEffect, coords);
            if (_random.Prob(0.6f))
                _audio.PlayPvs(SparkSound, uid);

            if (active.Burning)
                Spawn(FlameEffect, xform.Coordinates.Offset(new Vector2(_random.NextFloat(-0.2f, 0.2f), _random.NextFloat(-0.15f, 0.25f))));
        }
    }
}

[RegisterComponent]
public sealed partial class ActiveDamagedMachineEffectsComponent : Component
{
    [ViewVariables]
    public TimeSpan NextEffect;

    [ViewVariables]
    public bool Burning;
}
