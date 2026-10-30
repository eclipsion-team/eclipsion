using Content.Shared.Abilities.Psionics;
using Content.Shared.StatusEffect;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Server.Beam;
using Content.Shared.Actions.Events;

namespace Content.Server.Abilities.Psionics
{
    public sealed class NoosphericZapPowerSystem : EntitySystem
    {
        [Dependency] private readonly SharedPsionicAbilitiesSystem _psionics = default!;
        [Dependency] private readonly StaminaSystem _stamina = default!;
        [Dependency] private readonly StatusEffectsSystem _statusEffectsSystem = default!;
        [Dependency] private readonly BeamSystem _beam = default!;

        private const float StaminaFraction = 0.8f;


        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<NoosphericZapPowerActionEvent>(OnPowerUsed);
        }

        private void OnPowerUsed(NoosphericZapPowerActionEvent args)
        {
            if (!_psionics.OnAttemptPowerUse(args.Performer, args.Target, "noospheric zap", true))
                return;

            _beam.TryCreateBeam(args.Performer, args.Target, "LightningNoospheric");

            if (TryComp<StaminaComponent>(args.Target, out var stamina))
                _stamina.TakeStaminaDamage(args.Target, stamina.CritThreshold * StaminaFraction, stamina, args.Performer);

            _statusEffectsSystem.TryAddStatusEffect(args.Target, "Stutter", TimeSpan.FromSeconds(5), false, "StutteringAccent");

            _psionics.LogPowerUsed(args.Performer, "noospheric zap");
            args.Handled = true;
        }
    }
}
