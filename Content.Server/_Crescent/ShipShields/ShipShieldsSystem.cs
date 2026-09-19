using System.Numerics;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Random;
using Robust.Server.GameStates;
using Content.Server.Power.Components;
using Robust.Shared.Physics;
using Content.Shared._Crescent.SpaceArtillery;
using Content.Shared.Projectiles;
using Content.Shared._RMC14.Weapons.Ranged.Prediction;


namespace Content.Server._Crescent.ShipShields;
public sealed partial class ShipShieldsSystem : EntitySystem
{
    private const string ShipShieldPrototype = "ShipShield";
    private const float Padding = 10f;
    private const float CollisionThreshold = 50f;
    //private const float DeflectionSpread = 25f;
    private const float EmitterUpdateRate = 1f; //mlg changed this to 1.5. setting it back to 1

    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;

    [Dependency] private readonly FixtureSystem _fixtureSystem = default!;

    [Dependency] private readonly PhysicsSystem _physicsSystem = default!;

    [Dependency] private readonly PvsOverrideSystem _pvsSys = default!;

    private ISawmill _sawmill = default!;

    // Crescent: deflections are queued by OnCollide and resolved from Update.
    // OnCollide runs inside SharedPhysicsSystem.CollideContacts, and resolving a deflection deletes the projectile
    // (which purges its contacts on the spot) and can detonate it. Doing that while the engine is still walking its
    // pooled Contact[] hands a live contact back to the pool mid-iteration.
    private readonly List<(EntityUid Emitter, EntityUid Deflected)> _pendingDeflections = new();
    private readonly List<(EntityUid Emitter, EntityUid Deflected)> _processingDeflections = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateDeflections();

        var query = EntityQueryEnumerator<ShipShieldEmitterComponent, ApcPowerReceiverComponent>();
        while (query.MoveNext(out var uid, out var emitter, out var power))
        {
            if (emitter.ForcedDisabled)
                continue;

            //.2 | 2025 here to untangle this mess

            // emitter only runs its code every EmitterUpdateRate. emitter.Accumulator just adds up to 1.5 each time and...
            emitter.Accumulator += frameTime;

            //cancels the whole op for this shield if it's not over 1.5. after it is, it resets.
            if (emitter.Accumulator < EmitterUpdateRate)
                continue;

            // if emitter.DamageIveTakenSoFar^emitter.DamageMultiplier is bigger than the power draw the emitter can take, then the emitter's down and is recharging
            // commented because shields draw a flat amount of power now
            // if ((float) Math.Pow(emitter.Damage, emitter.DamageExp) >= emitter.MaxDraw)
            //     emitter.Recharging = true;

            // OR if the emitter is not powered, then it must be down, and we should be recharging as soon as it wakes back up.
            // if (!power.Powered)
            //     emitter.Recharging = true;

            // if we're here, then .Accumulator >= 1.5. dunk it back down so we're ready to count up to 1.5 again.
            emitter.Accumulator -= EmitterUpdateRate;

            // OverloadAccumulator is over 0 when our shield's popped. it's effectively the timer counting down until the shield goes back up.
            if (emitter.OverloadAccumulator > 0)
            {
                emitter.OverloadAccumulator -= EmitterUpdateRate;
            }

            //this is the value that the emitter will heal up by each 1.5s, decided by .HealPerSecond. 
            float healed = emitter.HealPerSecond * EmitterUpdateRate;

            //if our shield is down/recharging, then we should heal it's hp back up by this amount.
            if (emitter.Recharging)
                healed *= emitter.UnpoweredBonus;

            emitter.Damage -= healed; //line of code that actually does the healing

            // if we healed, our damage taken is MOST LIKELY under 0. fix it by setting it to 0.
            if (emitter.Damage < 0)
            {
                emitter.Damage = 0;

                //if we reached this check, then our emitter must be fully healed and should be back on
                if (power.Powered)
                    emitter.Recharging = false;
            }

            //this adjusts how much power the emitter is drawing, each update tick. commented because we only draw a fixed amount now.
            //AdjustEmitterLoad(uid, emitter, power);

            //this checks if the shield the ship is attached to actually exists. if it's completely gone, don't bother calculating shields for this "grid"
            var parent = Transform(uid).GridUid;
            if (parent == null)
            {
                RemoveEmitterShield(uid, emitter);
                continue;
            }

            // filter is needed to play the power down / power up noise for ONLY those on the ship grid
            var filter = _station.GetInOwningStation(uid);

            // if our emitter's .DamageTaken is over the .DamageLimit, then set the OverloadAccumulator to the DamageOverloadTimePunishment.
            if (emitter.Damage > emitter.DamageLimit)
                emitter.OverloadAccumulator = emitter.DamageOverloadTimePunishment;

            // if our shield is gone, AND the OverloadAccumulator is done counting down (with one tick of
            // padding - a literal 1.5 here was left over from when EmitterUpdateRate was 1.5, and let the
            // shield back up a tick and a half before the punishment had actually been served), then...
            if (emitter.Shield is null && emitter.OverloadAccumulator < EmitterUpdateRate && power.Powered) //put the shield back up!
            {
                emitter.Recharging = false; //stop boosting hp recharge now that it's up
                var shield = ShieldEntity(parent.Value, source: uid);
                if (shield != EntityUid.Invalid
                    && TryComp<ShipShieldComponent>(shield, out var shieldComp)
                    && shieldComp.Source == uid)
                {
                    emitter.Shield = shield;
                    emitter.Shielded = parent.Value;
                    _audio.PlayGlobal(emitter.PowerUpSound, filter, true, emitter.PowerUpSound.Params);
                }
            }
            // if our emitter is Overloaded, AND the shield is active, shut down the shield.
            else if (emitter.OverloadAccumulator > 0 && emitter.Shield is not null)
            {
                emitter.Recharging = true; //boost hp recharge when it's down
                UnshieldEntity(parent.Value);
                emitter.Shield = null;
                emitter.Shielded = null;
                _audio.PlayGlobal(emitter.PowerDownSound, filter, true, emitter.PowerUpSound.Params);
            }

            if (!power.Powered && emitter.Shield is not null) // if shield is depowered then unshield the ship
            {
                emitter.Recharging = true; //boost hp recharge when it's down
                UnshieldEntity(parent.Value);
                emitter.Shield = null;
                emitter.Shielded = null;
                _audio.PlayGlobal(emitter.PowerDownSound, filter, true, emitter.PowerUpSound.Params);
            }

            // SHIELDS POWERING UP / DOWN BECAUSE OF POWER DRAW IS HANDLED SOMEWHERE ELSE
			
		    Dirty(uid, emitter); // Rat

        }

        // Remove orphaned shield state. In particular, an emitter can be deleted after its transform has already
        // detached from the grid, so cleanup must validate the stored source instead of just checking for null.
        var cleanupQuery = EntityQueryEnumerator<ShipShieldedComponent>();
        while (cleanupQuery.MoveNext(out var uid, out var shieldedComp))
        {
            if (shieldedComp.Source is not { } source
                || TerminatingOrDeleted(source)
                || !TryComp<ShipShieldEmitterComponent>(source, out var emitter))
            {
                UnshieldEntity(uid, shieldedComp);
                continue;
            }

            var shieldValid = !TerminatingOrDeleted(shieldedComp.Shield)
                && TryComp<ShipShieldComponent>(shieldedComp.Shield, out var shield)
                && shield.Source == shieldedComp.Source
                && shield.Shielded == uid;

            if (shieldValid)
                continue;

            emitter.Shield = null;
            emitter.Shielded = null;
            Dirty(source, emitter);

            UnshieldEntity(uid, shieldedComp);
        }
    }
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShipShieldComponent, StartCollideEvent>(OnCollide);

        InitializeCommands();
        InitializeEmitters();
        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("crescent.shipshields.server");
    }

    private void OnCollide(EntityUid uid, ShipShieldComponent component, StartCollideEvent args)
    {
        TryQueueDeflection((uid, component), args.OtherEntity);
    }

    /// <summary>
    /// Routes a ship projectile into the shield damage pipeline. This is also called by phase prevention because
    /// the engine's raycast deliberately excludes the shield's soft collision sensor.
    /// </summary>
    public bool TryQueueDeflection(Entity<ShipShieldComponent> shield, EntityUid deflected)
    {
        if (Transform(deflected).Anchored
            || !HasComp<ShipWeaponProjectileComponent>(deflected)
            || HasComp<IgnoresHullrotShieldsComponent>(deflected)
            || !TryComp<ProjectileComponent>(deflected, out var projectile))
        {
            return false;
        }

        if (projectile.DamagedEntity)
            return true;

        if (projectile.Weapon is { } weapon
            && !TerminatingOrDeleted(weapon)
            && shield.Comp.Shielded == Transform(weapon).GridUid)
        {
            return false;
        }

        // .2 | 2025. this code used to make some projectiles ignore shields if their velocity was low enough.
        // i commented this out and replaced it by the IgnoresHullrotShields component.
        // var ourVelocity = ourPhysics.LinearVelocity;
        // var velocity = theirPhysics.LinearVelocity;

        // var collisionSpeedVector = Vector2.Subtract(ourVelocity, velocity);

        // if (Math.Abs(collisionSpeedVector.Length()) < CollisionThreshold)
        // {
        //     return;
        // }


        //if (TryComp<TimedDespawnComponent>(args.OtherEntity, out var despawn))
        //    despawn.Lifetime += despawn.Lifetime;

        // I originally tried reflection but the math is too hard with the fucked coordinate system in this game (WorldRotation can be negative. Vector to Angle conversion loses information. Etc etc.)
        // Might try again at some point using just vector math with this (https://math.stackexchange.com/questions/13261/how-to-get-a-reflection-vector)
        //var deflectionVector = Transform(args.OtherEntity).WorldPosition - Transform(uid).WorldPosition;
        //var angle = _random.NextFloat(DeflectionSpread);

        //if (_random.Prob(0.5f))
        //    angle = -angle;

        //deflectionVector = new Vector2((float) (Math.Cos(angle) * deflectionVector.X - Math.Sin(angle) * deflectionVector.Y), (float) (Math.Sin(angle) * deflectionVector.X - Math.Cos(angle) * deflectionVector.Y));

        // instead of reflecting the projectile, just delete it. this works better for gameplay and intuiting what is going on in a fight.
        //_gun.ShootProjectile(args.OtherEntity, deflectionVector, _physicsSystem.GetMapLinearVelocity(uid), uid, null, velocity.Length());

        if (shield.Comp.Source is not { } source || TerminatingOrDeleted(source))
            return false;

        // Stop the round dead right now - it must not go on to damage the hull this tick - but leave deleting and
        // detonating it to UpdateDeflections, once the physics step is over.
        projectile.DamagedEntity = true;

        _pendingDeflections.Add((source, deflected));
        return true;
    }

    /// <summary>
    /// Resolves deflections queued by <see cref="OnCollide"/>, outside the physics step.
    /// </summary>
    private void UpdateDeflections()
    {
        if (_pendingDeflections.Count == 0)
            return;

        // Handling a deflection can detonate the round, which may deflect further rounds off the same shield.
        _processingDeflections.Clear();
        _processingDeflections.AddRange(_pendingDeflections);
        _pendingDeflections.Clear();

        foreach (var (emitter, deflected) in _processingDeflections)
        {
            if (TerminatingOrDeleted(emitter) || TerminatingOrDeleted(deflected) || EntityManager.IsQueuedForDeletion(deflected))
                continue;

            var ev = new ShieldDeflectedEvent(deflected);
            RaiseLocalEvent(emitter, ref ev);
        }

        _processingDeflections.Clear();
    }

    /// <summary>
    /// Produces a shield around a grid entity, if it doesn't already exist.
    /// </summary>
    /// <param name="entity">The entity being shielded.</param>
    /// <param name="mapGrid">The map grid component of the entity being shielded.</param>
    /// <param name="source">A shield generator or similar providing the shield for the entity</param>
    /// <returns>The shield entity.</returns>
    private EntityUid ShieldEntity(EntityUid entity, MapGridComponent? mapGrid = null, EntityUid? source = null)
    {
        if (TryComp<ShipShieldedComponent>(entity, out var existingShielded))
            return existingShielded.Shield;

        if (!Resolve(entity, ref mapGrid, false))
            return EntityUid.Invalid;

        var prototype = ShipShieldPrototype;

        var shield = Spawn(prototype, Transform(entity).Coordinates);
        var shieldPhysics = AddComp<PhysicsComponent>(shield);
        var shieldComp = EnsureComp<ShipShieldComponent>(shield);
        shieldComp.Shielded = entity;
        shieldComp.Source = source;

        _transformSystem.SetLocalPosition(shield, mapGrid.LocalAABB.Center);
        _transformSystem.SetWorldRotation(shield, _transformSystem.GetWorldRotation(entity));
        _transformSystem.SetParent(shield, entity);

        var chain = GenerateOvalFixture(shield, "shield", shieldPhysics, mapGrid);

        List<Vector2> roughPoly = new();

        var interval = chain.Count / PhysicsConstants.MaxPolygonVertices;

        int i = 0;

        while (i < PhysicsConstants.MaxPolygonVertices)
        {
            roughPoly.Add(chain.Vertices[i * interval]);
            i++;
        }

        var internalPoly = new PolygonShape();
        internalPoly.Set(roughPoly);

        _fixtureSystem.TryCreateFixture(shield, internalPoly, "internalShield",
            hard: false,
            collisionLayer: (int) CollisionGroup.FullTileLayer,
            body: shieldPhysics);

        _physicsSystem.WakeBody(shield, body: shieldPhysics);
        _physicsSystem.SetSleepingAllowed(shield, shieldPhysics, false);

        _pvsSys.AddGlobalOverride(shield);

        var shieldedComp = EnsureComp<ShipShieldedComponent>(entity);
        shieldedComp.Shield = shield;
        shieldedComp.Source = source;

        return shield;
    }

    private bool UnshieldEntity(EntityUid uid, ShipShieldedComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return false;

        // Deleting a grid deletes its children, shield and emitter included, in no particular order. The emitter's
        // shutdown lands here, and if the shield is already on its way out a second Del throws "Called Delete on an
        // entity already being deleted" - every round restart and every destroyed shielded ship.
        if (!TerminatingOrDeleted(component.Shield))
            Del(component.Shield);

        if (!TerminatingOrDeleted(uid))
            RemComp<ShipShieldedComponent>(uid);

        return true;
    }

    private ChainShape GenerateOvalFixture(EntityUid uid, string name, PhysicsComponent physics, MapGridComponent mapGrid, float padding = Padding)
    {
        float radius;
        float scale;
        var scaleX = true;

        var height = mapGrid.LocalAABB.Height + padding;
        var width = mapGrid.LocalAABB.Width + padding;

        if (width > height)
        {
            radius = 0.5f * height;
            scale = width / height;
        }
        else
        {
            radius = 0.5f * width;
            scale = height / width;
            scaleX = false;
        }

        var chain = new ChainShape();

        chain.CreateLoop(Vector2.Zero, radius);

        for (int i = 0; i < chain.Vertices.Length; i++)
        {
            if (scaleX)
            {
                chain.Vertices[i].X *= scale;
            }
            else
            {
                chain.Vertices[i].Y *= scale;
            }
        }

        // One chain fixture, and it must never be able to form a contact.
        //
        // A ChainShape gets one broadphase proxy per segment but all of them share a single Fixture object, and
        // the engine's new-pair check (SharedBroadphaseSystem.FindPairs) dedupes per Fixture *before* the batch
        // is applied. Two segments overlapping the same other fixture in one tick therefore queue the same pair
        // twice, and the second AddPair throws "An item with the same key has already been added. Key: Fixture"
        // after it has already put the contact in the active list. From the next tick on DestroyContact throws
        // "The LinkedList node does not belong to current LinkedList" and CollideContacts aborts every tick:
        // physics stops, the client stops responding and Windows kills it as hung. This bubble is on every
        // client (AddGlobalOverride) and never sleeps, so it used to take everyone down with it.
        //
        // PhasePrevention is a query-only layer - nothing carries it in a collision mask - so this fixture is
        // reachable by IntersectRay and by the client overlays that read its shape, and by nothing else. It is
        // hard because IntersectRay skips soft fixtures. Deflection contacts live on "internalShield", which is
        // a PolygonShape and therefore has exactly one proxy. Do not give this one a colliding layer.
        _fixtureSystem.TryCreateFixture(uid, chain, name,
            hard: true,
            collisionLayer: (int) CollisionGroup.PhasePrevention,
            body: physics);

        return chain;
    }

    [ByRefEvent]
    public record struct ShieldDeflectedEvent(EntityUid Deflected)
    {

    }
}
