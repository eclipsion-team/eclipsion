using Content.Shared.Crescent.Psionics;
using System.Numerics;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Content.Shared.Damage;
using Content.Shared.Explosion;
using Content.Shared.Popups;
using Robust.Shared.Timing;

namespace Content.Server.Abilities.Psionics;

/// <summary>
/// Implements the defense tree's passive armor reinforcement and temporary energy shields.
/// </summary>
public sealed class PsionicDefensePowerSystem : EntitySystem
{
    private static readonly DamageModifierSet ArmorReinforcement = new()
    {
        Coefficients =
        {
            ["Blunt"] = 0.93f,
            ["Slash"] = 0.93f,
            ["Piercing"] = 0.93f,
            ["Heat"] = 0.93f,
            ["Cold"] = 0.93f,
            ["Shock"] = 0.93f,
            ["Caustic"] = 0.93f,
            ["Poison"] = 0.93f,
            ["Radiation"] = 0.93f,
        },
    };

    private static readonly TimeSpan ShieldDuration = TimeSpan.FromSeconds(10);

    /// <summary>Keeps a burst of hits from stacking flares on top of each other.</summary>
    private static readonly TimeSpan ImpactCooldown = TimeSpan.FromSeconds(0.35);

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPsionicAbilitiesSystem _psionics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PsionicSelfShieldActionEvent>(OnSelfShield);
        SubscribeLocalEvent<PsionicArmorUpgradeComponent, DamageModifyEvent>(OnArmorDamage);
        SubscribeLocalEvent<PsionicEnergyShieldComponent, DamageModifyEvent>(OnShieldDamage);
        SubscribeLocalEvent<PsionicEnergyShieldComponent, GetExplosionResistanceEvent>(OnExplosionResistance);
        SubscribeLocalEvent<PsionicEnergyShieldComponent, ComponentShutdown>(OnShieldShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<PsionicEnergyShieldComponent>();
        while (query.MoveNext(out var uid, out var shield))
        {
            if (_timing.CurTime >= shield.ExpiresAt)
                RemCompDeferred<PsionicEnergyShieldComponent>(uid);
        }
    }

    private void OnSelfShield(PsionicSelfShieldActionEvent args)
    {
        if (args.Handled || !_psionics.OnAttemptPowerUse(args.Performer, "energy aegis", true))
            return;

        ApplyShield(args.Performer, args.Performer);
        _psionics.LogPowerUsed(args.Performer, "energy aegis", 4, 6);
        args.Handled = true;
    }

    private void ApplyShield(EntityUid target, EntityUid caster)
    {
        var shield = EnsureComp<PsionicEnergyShieldComponent>(target);
        shield.ExpiresAt = _timing.CurTime + ShieldDuration;

        if (shield.Visual is not { } visual || !Exists(visual))
        {
            visual = Spawn("EffectPsionicEnergyShield", Transform(target).Coordinates);
            _transform.SetParent(visual, target);
            _transform.SetLocalPosition(visual, Vector2.Zero);
            shield.Visual = visual;
        }

        Dirty(target, shield);
        _popup.PopupEntity(
            Loc.GetString("psionic-energy-shield-applied", ("target", target)),
            target,
            caster,
            PopupType.Medium);
    }

    private void OnArmorDamage(
        EntityUid uid,
        PsionicArmorUpgradeComponent component,
        DamageModifyEvent args)
    {
        // The reinforcement is the power itself, and inside a null field there is no power to hold it up.
        if (HasComp<PsionicallyNullifiedComponent>(uid))
            return;

        args.Damage = DamageSpecifier.ApplyModifierSet(args.Damage, ArmorReinforcement);
    }

    private void OnShieldDamage(
        EntityUid uid,
        PsionicEnergyShieldComponent component,
        DamageModifyEvent args)
    {
        if (!args.Damage.DamageDict.TryGetValue("Heat", out var heat) || heat <= 0)
            return;

        args.Damage.DamageDict["Heat"] = heat * Math.Max(0f, component.HeatCoefficient);
        FlareShield(uid, component);
    }

    private void OnExplosionResistance(
        EntityUid uid,
        PsionicEnergyShieldComponent component,
        ref GetExplosionResistanceEvent args)
    {
        args.DamageCoefficient *= Math.Max(0f, component.ExplosionCoefficient);
        FlareShield(uid, component);
    }

    /// <summary>
    /// Flashes the barrier so onlookers can tell the hit was actually absorbed.
    /// </summary>
    private void FlareShield(EntityUid uid, PsionicEnergyShieldComponent component)
    {
        if (_timing.CurTime < component.NextImpact)
            return;

        component.NextImpact = _timing.CurTime + ImpactCooldown;

        var flare = Spawn("EffectPsionicShieldImpact", Transform(uid).Coordinates);
        _transform.SetParent(flare, uid);
        _transform.SetLocalPosition(flare, Vector2.Zero);
    }

    private void OnShieldShutdown(
        EntityUid uid,
        PsionicEnergyShieldComponent component,
        ComponentShutdown args)
    {
        if (component.Visual is { } visual && Exists(visual))
            QueueDel(visual);
    }
}
