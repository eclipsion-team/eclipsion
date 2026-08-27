using Content.Shared._Crescent.ShipShields;
using Content.Shared._Crescent.CCvars;
using Content.Server.Power.Components;
using Content.Server._Crescent.RoundEnd; //ec
using Robust.Shared.Configuration;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Components;
using Content.Server.Emp;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Station.Systems;
using Robust.Shared.Audio.Systems;
using Content.Shared.Examine;
using Content.Server.Explosion.Components;
using Robust.Shared.GameObjects; // Rat
using System.Linq; // Rat
using System.Diagnostics.CodeAnalysis; // Rat


namespace Content.Server._Crescent.ShipShields;
public partial class ShipShieldsSystem
{
    private const float MAX_EMP_DAMAGE = 10000f;
    [Dependency] private readonly TriggerSystem _trigger = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
	[Dependency] private readonly IConfigurationManager _config = default!;
	[Dependency] private readonly EntityLookupSystem _lookup = default!; // Rat
	private bool _powerDrawEnabled;

    public void InitializeEmitters()
    {
		_powerDrawEnabled = _config.GetCVar(CrescentCVars.ShipSystemsPowerDrawEnabled);

        SubscribeLocalEvent<ShipShieldEmitterComponent, ShieldDeflectedEvent>(OnShieldDeflected);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentRemove>(OnRemoved);
		SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentStartup>(OnEmitterStartup); // Rat
    }

    // Rat-start
    private void OnEmitterStartup(EntityUid uid, ShipShieldEmitterComponent component, ComponentStartup args)
    {
        var grid = Transform(uid).GridUid;
        _pvsSys.AddGlobalOverride(uid);

       if (grid != null && HasComp<StationInfestationComponent>(grid.Value))
            SetForcedDisabled(uid, true, component);
            return;

		if (_powerDrawEnabled || !TryComp<ApcPowerReceiverComponent>(uid, out var receiver))
			return;

		receiver.Load = 0f;
		receiver.NeedsPower = false;
    }
    // Rat-end

    private void OnRemoved(Entity<ShipShieldEmitterComponent> owner,ref ComponentRemove remove)
    {
        _pvsSys.RemoveGlobalOverride(owner.Owner);
		var parent = Transform(owner.Owner).GridUid;
        if (parent is null)
            return;
        UnshieldEntity(parent.Value, null);
    }

    private void OnShieldDeflected(EntityUid uid, ShipShieldEmitterComponent component, ShieldDeflectedEvent args)
    {
        if (TryComp<EmpOnTriggerComponent>(args.Deflected, out var emp))
        {
            component.Damage += Math.Clamp(emp.EnergyConsumption, 0f, MAX_EMP_DAMAGE);
            _trigger.Trigger(args.Deflected);
        }

        if (TryComp<ExplosiveComponent>(args.Deflected, out var exp))
        {
            component.Damage += exp.TotalIntensity / 15; //after mlg intensity explosion changes, 1 intensity = 1 dmg, instead of 1 intensity = 15 dmg;
        }

        if (TryComp<ProjectileComponent>(args.Deflected, out var proj))
        {
            component.Damage += (float) proj.Damage.GetTotal();
            proj.DamagedEntity = true;
        }
        else if (TryComp<PhysicsComponent>(args.Deflected, out var phys))
        {
            component.Damage += phys.FixturesMass;
        }

        Dirty(uid, component);
		QueueDel(args.Deflected);
    }

    private void OnExamined(EntityUid uid, ShipShieldEmitterComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        if (component.Damage == 0f)
        {
            args.PushMarkup(Loc.GetString("shield-emitter-examine-undamaged"));
            return;
        }

        var ratio = component.Damage / component.DamageLimit;

        args.PushMarkup(Loc.GetString("shield-emitter-examine-damaged", ("percent", ratio)));
    }

    // Rat-start
    public bool TryGetShieldEmitter(EntityUid grid, [NotNullWhen(true)] out EntityUid? emitter, [NotNullWhen(true)] out ShipShieldEmitterComponent? emitterComp)
    {
        emitter = null;
        emitterComp = null;

        if (TryComp<ShipShieldedComponent>(grid, out var shielded)
            && shielded.Source != null
            && TryComp(shielded.Source, out emitterComp))
        {
            emitter = shielded.Source.Value;
            return true;
        }

        var ents = new HashSet<Entity<ShipShieldEmitterComponent>>();
        _lookup.GetGridEntities(grid, ents);

        if (ents.Count < 1)
            return false;

        var emitterEnt = ents.First();
        emitter = emitterEnt;
        emitterComp = emitterEnt.Comp;
        return true;
    }

    /// <summary>
    /// Keeps an emitter down independently of its power state. Disabling immediately removes an active shield;
    /// enabling lets the normal update loop rebuild it on its next pass.
    /// </summary>
    public bool SetForcedDisabled(EntityUid uid, bool disabled, ShipShieldEmitterComponent? emitter = null)
    {
        if (!Resolve(uid, ref emitter, false) || emitter.ForcedDisabled == disabled)
            return false;

        emitter.ForcedDisabled = disabled;

        if (disabled && emitter.Shielded is { } shielded)
        {
            UnshieldEntity(shielded);
            emitter.Shield = null;
            emitter.Shielded = null;
        }

        Dirty(uid, emitter);
        return true;
    }
    // Rat-end

    // .2 - 2025. commented out because shields draw a fixed amount of power now
    // private void AdjustEmitterLoad(EntityUid uid, ShipShieldEmitterComponent? emitter = null, ApcPowerReceiverComponent? receiver = null)
    // {
    //     if (!Resolve(uid, ref emitter, ref receiver))
    //         return;

    //     /// Raise damage to the power of the growth exponent
    //     var additionalLoad = (float) Math.Clamp(Math.Pow(emitter.Damage, emitter.DamageExp), 0f, emitter.MaxDraw);

    //     receiver.Load = emitter.BaseDraw + additionalLoad;
    // }
}
