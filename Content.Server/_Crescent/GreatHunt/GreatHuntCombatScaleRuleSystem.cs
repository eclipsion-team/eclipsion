using Content.Server.GameTicking.Rules;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Events;
using Content.Shared.Damage.Systems;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Runs <see cref="GreatHuntCombatScaleRuleComponent"/>: while one is active, melee and thrown weapon damage and the
/// stamina damage attackers deal are multiplied by its values. Damage is scaled where the weapon's damage is worked
/// out, so examining a weapon shows the scaled numbers too.
/// </summary>
public sealed class GreatHuntCombatScaleRuleSystem : GameRuleSystem<GreatHuntCombatScaleRuleComponent>
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GetMeleeDamageEvent>(OnGetMeleeDamage);
        SubscribeLocalEvent<GetThrowingDamageEvent>(OnGetThrowingDamage);
        SubscribeLocalEvent<StaminaComponent, BeforeStaminaDamageEvent>(OnBeforeStaminaDamage);
    }

    private void OnGetMeleeDamage(ref GetMeleeDamageEvent args)
    {
        if (TryGetScale(out var scale))
            args.Damage *= scale.DamageMultiplier;
    }

    private void OnGetThrowingDamage(ref GetThrowingDamageEvent args)
    {
        if (TryGetScale(out var scale))
            args.Damage *= scale.DamageMultiplier;
    }

    private void OnBeforeStaminaDamage(Entity<StaminaComponent> ent, ref BeforeStaminaDamageEvent args)
    {
        // Only damage someone else dealt: not one's own swing or sprint costs, not recovery, not stamina already
        // scaled through the damage it came from.
        if (args.Value <= 0f || args.FromDamage || args.Source is not { } source || source == ent.Owner)
            return;

        if (TryGetScale(out var scale))
            args.Value *= scale.StaminaDamageMultiplier;
    }

    private bool TryGetScale(out GreatHuntCombatScaleRuleComponent scale)
    {
        var query = QueryActiveRules();
        while (query.MoveNext(out _, out var rule, out _))
        {
            scale = rule;
            return true;
        }

        scale = default!;
        return false;
    }
}
