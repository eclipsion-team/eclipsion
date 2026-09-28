using Content.Shared._Crescent.ShipShields;
using Content.Shared._Crescent.CCvars;
using Content.Server._Crescent.RoundEnd;
using Content.Server.Power.Components;
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
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentShutdown>(OnEmitterShutdown);
		SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentStartup>(OnEmitterStartup); // Rat
        SubscribeLocalEvent<ShipShieldComponent, ComponentShutdown>(OnShieldShutdown);
    }

    /// <summary>
    /// Emitter.Shield is networked, so it must never outlive the shield entity: PVS would try to resolve the
    /// deleted uid every time the emitter is dirtied. A shield can go without the emitter's involvement - most
    /// often its grid being deleted after the emitter has ended up on another grid (split, moved) - so clear
    /// the reference from the shield's side.
    /// </summary>
    private void OnShieldShutdown(Entity<ShipShieldComponent> shield, ref ComponentShutdown args)
    {
        if (shield.Comp.Source is not { } source
            || !TryComp<ShipShieldEmitterComponent>(source, out var emitter)
            || emitter.Shield != shield.Owner)
        {
            return;
        }

        emitter.Shield = null;
        emitter.Shielded = null;

        if (!TerminatingOrDeleted(source))
            Dirty(source, emitter);
    }

    // Rat-start
    private void OnEmitterStartup(EntityUid uid, ShipShieldEmitterComponent component, ComponentStartup args)
    {
        _pvsSys.AddGlobalOverride(uid);

        var grid = Transform(uid).GridUid;
        if (grid != null && HasComp<StationInfestationComponent>(grid.Value))
        {
            SetForcedDisabled(uid, true, component);
            return;
        }

		if (_powerDrawEnabled || !TryComp<ApcPowerReceiverComponent>(uid, out var receiver))
			return;

		receiver.Load = 0f;
		receiver.NeedsPower = false;
    }
    // Rat-end

    private void OnEmitterShutdown(Entity<ShipShieldEmitterComponent> owner, ref ComponentShutdown args)
    {
        _pvsSys.RemoveGlobalOverride(owner.Owner);
        RemoveEmitterShield(owner.Owner, owner.Comp);
    }

    /// <summary>
    /// Removes only the shield owned by this emitter. Uses the stored relationship because an entity being deleted
    /// may already have lost its grid parent by the time its component shuts down.
    /// </summary>
    private void RemoveEmitterShield(EntityUid uid, ShipShieldEmitterComponent emitter)
    {
        var shielded = emitter.Shielded;
        var shield = emitter.Shield;
        emitter.Shielded = null;
        emitter.Shield = null;

        if (shielded is { } grid
            && TryComp<ShipShieldedComponent>(grid, out var shieldedComp)
            && shieldedComp.Source == uid)
        {
            UnshieldEntity(grid, shieldedComp);
            return;
        }

        if (shield is { } shieldUid
            && !TerminatingOrDeleted(shieldUid)
            && TryComp<ShipShieldComponent>(shieldUid, out var shieldComp)
            && shieldComp.Source == uid)
        {
            Del(shieldUid);
        }
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

        // The locale line prints this into a "%" slot, so it has to be scaled here. Handing it the
        // raw 0-1 ratio read a half-wrecked emitter out as "0.5% damaged".
        var percent = component.DamageLimit > 0f
            ? (int) MathF.Round(component.Damage / component.DamageLimit * 100f)
            : 100;

        args.PushMarkup(Loc.GetString("shield-emitter-examine-damaged", ("percent", percent)));
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

        // A hull may carry more than one emitter, and HashSet order is not stable, so taking whatever
        // came out first handed back a different emitter from one call to the next. Lowest EntityUid
        // is arbitrary, but it is the same answer every time for a given ship.
        var emitterEnt = ents.First();
        foreach (var candidate in ents)
        {
            if (candidate.Owner.CompareTo(emitterEnt.Owner) < 0)
                emitterEnt = candidate;
        }

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

        if (disabled)
            RemoveEmitterShield(uid, emitter);

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
