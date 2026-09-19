using System.Numerics;
using Content.Server.Abilities.Psionics;
using Content.Shared._Crescent.ShieldBelt;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Clothing;
using Content.Shared.Crescent.Psionics;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.StatusEffect;
using Content.Shared.Throwing;
using Content.Shared.Voidborn;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Content.Server._Crescent.Psionics;

/// <summary>
/// Runs every null field in the round. A field sits around each living <see cref="PsionicNullifierComponent"/> - the
/// χ Waveform Misalignment trait, or someone wearing a nullifier helmet - and on a short cadence:
/// <list type="bullet">
/// <item>marks every psion inside with <see cref="PsionicallyNullifiedComponent"/>, which refuses all casting, and
/// tears down whatever they had running (concealment, shadow form, energy shield);</item>
/// <item>snaps mind swaps and telegnostic projections back and sends familiars home;</item>
/// <item>unravels psionic projectiles and drops telekinetically hurled objects;</item>
/// <item>shatters aegis domes and collapses stasis fields that reach into it.</item>
/// </list>
/// Area powers that would otherwise still reach the nullifier from outside check <see cref="IsInsideNullField"/> or
/// the component directly.
/// </summary>
public sealed class PsionicNullifierSystem : EntitySystem
{
    /// <summary>
    /// Seconds between scans. A psionic fireball travels 14m/s, so it covers 1.4m of a 5m field per scan and is
    /// always caught well before it reaches the middle.
    /// </summary>
    private const float ScanInterval = 0.1f;

    /// <summary>
    /// Projectiles are non-hard fixtures, and a lookup without <see cref="LookupFlags.Sensors"/> silently skips them.
    /// </summary>
    private const LookupFlags ManifestationFlags = LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Sensors;

    /// <summary>Chems and similar effects hang insulation off this rather than off an item.</summary>
    private const string InsulatedStatusEffect = "PsionicallyInsulated";

    [Dependency] private readonly AegisDomeSystem _aegis = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MindSwapPowerSystem _mindSwap = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PsionicFamiliarSystem _familiars = default!;
    [Dependency] private readonly PsionicNullifiedSystem _nullifiedSystem = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly ThrownItemSystem _thrownItem = default!;

    // Scratch collections, rebuilt from scratch every scan. Nothing in them outlives the scan that filled them,
    // so a round restart leaves nothing stale behind.
    private readonly List<(MapCoordinates Origin, float Range)> _fields = new();
    private readonly HashSet<EntityUid> _nullified = new();
    private readonly HashSet<Entity<PsionicComponent>> _psions = new();
    private readonly HashSet<Entity<PsionicFamiliarComponent>> _familiarsFound = new();
    private readonly HashSet<Entity<MindSwappedComponent>> _swapped = new();
    private readonly HashSet<Entity<PsionicManifestationComponent>> _manifestations = new();
    private readonly List<EntityUid> _scratch = new();

    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PsionicNullifierClothingComponent, ClothingGotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<PsionicNullifierClothingComponent, ClothingGotUnequippedEvent>(OnUnequipped);
        SubscribeLocalEvent<PsionicNullifierClothingComponent, ComponentShutdown>(OnClothingShutdown);
    }

    /// <summary>
    /// Whether a point lies inside any null field as of the last scan.
    /// </summary>
    public bool IsInsideNullField(MapCoordinates coordinates)
    {
        foreach (var (origin, range) in _fields)
        {
            if (origin.MapId == coordinates.MapId
                && Vector2.DistanceSquared(origin.Position, coordinates.Position) <= range * range)
                return true;
        }

        return false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < ScanInterval)
            return;

        _accumulator = 0f;

        CollectFields();
        _nullified.Clear();

        foreach (var (origin, range) in _fields)
            ScanField(origin, range);

        if (_fields.Count > 0)
            CollapseConstructs();

        foreach (var psion in _nullified)
            Suppress(psion);

        ReleaseOutsiders();
        ForgetLandedObjects();
    }

    private void CollectFields()
    {
        _fields.Clear();

        var query = EntityQueryEnumerator<PsionicNullifierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var nullifier, out var xform))
        {
            // The field is the brain being out of phase. A dead brain is not out of phase with anything.
            if (_mobState.IsDead(uid) || xform.MapID == MapId.Nullspace)
                continue;

            _fields.Add((_transform.GetMapCoordinates(uid, xform), nullifier.Range));
        }
    }

    private void ScanField(MapCoordinates origin, float range)
    {
        _psions.Clear();
        _lookup.GetEntitiesInRange(origin, range, _psions);
        foreach (var psion in _psions)
            _nullified.Add(psion.Owner);

        _familiarsFound.Clear();
        _lookup.GetEntitiesInRange(origin, range, _familiarsFound);
        foreach (var familiar in _familiarsFound)
        {
            if (!TerminatingOrDeleted(familiar))
                _familiars.DespawnFamiliar(familiar, familiar.Comp);
        }

        // Covers telegnosis too: the projection is one half of a mind swap. Ending either half puts both minds
        // back, and the other half then no longer has anything to snap back from. Swap is called directly rather
        // than raising a dispel, which would also burn anything dispellable to ash on every scan.
        _swapped.Clear();
        _lookup.GetEntitiesInRange(origin, range, _swapped);
        foreach (var swapped in _swapped)
        {
            if (!TerminatingOrDeleted(swapped))
                _mindSwap.Swap(swapped, swapped.Comp.OriginalEntity, true);
        }

        _manifestations.Clear();
        _lookup.GetEntitiesInRange(origin, range, _manifestations, ManifestationFlags);
        foreach (var manifestation in _manifestations)
            Unravel(manifestation);
    }

    private void Unravel(Entity<PsionicManifestationComponent> manifestation)
    {
        if (TerminatingOrDeleted(manifestation))
            return;

        if (manifestation.Comp.DeleteOnNullify)
        {
            _popup.PopupCoordinates(
                Loc.GetString("psionic-nullified-manifestation-fizzle", ("entity", manifestation.Owner)),
                Transform(manifestation).Coordinates,
                Filter.Pvs(manifestation),
                true,
                PopupType.SmallCaution);
            QueueDel(manifestation);
            return;
        }

        // A real object that was only being pushed. The push goes away and it drops where it is.
        _physics.SetLinearVelocity(manifestation, Vector2.Zero);
        _physics.SetAngularVelocity(manifestation, 0f);

        if (TryComp<ThrownItemComponent>(manifestation, out var thrown))
            _thrownItem.StopThrow(manifestation, thrown);

        RemCompDeferred<PsionicManifestationComponent>(manifestation);
    }

    /// <summary>
    /// Domes and stasis fields are placed rather than carried, so they are caught by overlap: any part of one
    /// reaching into a null field brings the whole thing down.
    /// </summary>
    private void CollapseConstructs()
    {
        var domes = EntityQueryEnumerator<AegisDomeComponent, TransformComponent>();
        while (domes.MoveNext(out var uid, out var dome, out var xform))
        {
            if (!TerminatingOrDeleted(uid) && Overlaps(_transform.GetMapCoordinates(uid, xform), dome.Radius))
                _aegis.Shatter((uid, dome));
        }

        var stasis = EntityQueryEnumerator<RecurrenceFieldComponent, TransformComponent>();
        while (stasis.MoveNext(out var uid, out var field, out var xform))
        {
            // Shutdown hands every captured thing its speed back.
            if (!TerminatingOrDeleted(uid) && Overlaps(_transform.GetMapCoordinates(uid, xform), field.Radius))
                QueueDel(uid);
        }
    }

    private bool Overlaps(MapCoordinates position, float radius)
    {
        foreach (var (origin, range) in _fields)
        {
            var reach = range + radius;
            if (origin.MapId == position.MapId
                && Vector2.DistanceSquared(origin.Position, position.Position) < reach * reach)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Repeated every scan rather than once on entry, so nothing cast in the instant before the marker landed
    /// survives inside the field.
    /// </summary>
    private void Suppress(EntityUid psion)
    {
        if (!HasComp<PsionicallyNullifiedComponent>(psion))
        {
            AddComp<PsionicallyNullifiedComponent>(psion);
            _popup.PopupEntity(Loc.GetString("psionic-nullified-start"), psion, psion, PopupType.LargeCaution);
        }

        RemComp<PsionicInvisibilityUsedComponent>(psion);
        RemComp<EtherealComponent>(psion);
        RemComp<PsionicEnergyShieldComponent>(psion);
    }

    private void ReleaseOutsiders()
    {
        _scratch.Clear();

        var query = EntityQueryEnumerator<PsionicallyNullifiedComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (!_nullified.Contains(uid))
                _scratch.Add(uid);
        }

        foreach (var uid in _scratch)
        {
            RemComp<PsionicallyNullifiedComponent>(uid);

            if (!TerminatingOrDeleted(uid))
                _popup.PopupEntity(Loc.GetString("psionic-nullified-end"), uid, uid, PopupType.Medium);
        }
    }

    /// <summary>
    /// A telekinetic push ends when the object lands. Left marked, a later throw by hand would be stopped too.
    /// </summary>
    private void ForgetLandedObjects()
    {
        _scratch.Clear();

        var query = EntityQueryEnumerator<PsionicManifestationComponent>();
        while (query.MoveNext(out var uid, out var manifestation))
        {
            if (!manifestation.DeleteOnNullify && !HasComp<ThrownItemComponent>(uid))
                _scratch.Add(uid);
        }

        foreach (var uid in _scratch)
            RemComp<PsionicManifestationComponent>(uid);
    }

    private void OnEquipped(Entity<PsionicNullifierClothingComponent> ent, ref ClothingGotEquippedEvent args)
    {
        Release(ent);
        ent.Comp.Wearer = args.Wearer;

        // Someone already misaligned keeps their own field, and gets nothing to lose when this comes off.
        if (!HasComp<PsionicNullifierComponent>(args.Wearer))
        {
            var nullifier = AddComp<PsionicNullifierComponent>(args.Wearer);
            nullifier.Range = ent.Comp.Range;
            nullifier.FromClothing = true;
            nullifier.Source = ent.Owner;
            ent.Comp.GrantedNullifier = true;
        }

        if (!HasComp<PsionicInsulationComponent>(args.Wearer))
        {
            AddComp<PsionicInsulationComponent>(args.Wearer);
            ent.Comp.GrantedInsulation = true;
        }
    }

    private void OnUnequipped(Entity<PsionicNullifierClothingComponent> ent, ref ClothingGotUnequippedEvent args)
    {
        Release(ent);
    }

    private void OnClothingShutdown(Entity<PsionicNullifierClothingComponent> ent, ref ComponentShutdown args)
    {
        Release(ent);
    }

    private void Release(Entity<PsionicNullifierClothingComponent> ent)
    {
        if (ent.Comp.Wearer is not { } wearer)
            return;

        var grantedNullifier = ent.Comp.GrantedNullifier;
        var grantedInsulation = ent.Comp.GrantedInsulation;
        ent.Comp.Wearer = null;
        ent.Comp.GrantedNullifier = false;
        ent.Comp.GrantedInsulation = false;

        if (TerminatingOrDeleted(wearer))
            return;

        if (grantedNullifier
            && TryComp<PsionicNullifierComponent>(wearer, out var nullifier)
            && nullifier.FromClothing
            && nullifier.Source == ent.Owner)
        {
            RemComp(wearer, nullifier);
        }

        // Our field is gone by now, so this only answers yes if the wearer carries a field of their own.
        if (grantedInsulation && !_nullifiedSystem.ClaimsInsulation(wearer) && !HasOtherInsulationSource(wearer))
            RemComp<PsionicInsulationComponent>(wearer);
    }

    private bool HasOtherInsulationSource(EntityUid wearer)
    {
        if (_statusEffects.HasStatusEffect(wearer, InsulatedStatusEffect))
            return true;

        var slots = _inventory.GetSlotEnumerator(wearer);
        while (slots.NextItem(out var item))
        {
            if (TryComp<TinfoilHatComponent>(item, out var tinfoil) && tinfoil.IsActive)
                return true;

            if (TryComp<ShieldBeltComponent>(item, out var belt) && belt.Insulating)
                return true;
        }

        return false;
    }
}
