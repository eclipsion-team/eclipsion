using Content.Shared.Construction.Components;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Pulling.Events;
using Content.Shared.Popups;
using Robust.Shared.Network;

namespace Content.Shared._Crescent.Territory;

/// <summary>
/// Asks how much damage brings territory works down. Only the server knows its destruction thresholds.
/// </summary>
[ByRefEvent]
public record struct PersistentPoiIntegrityEvent(FixedPoint2? DestroyedAt);

public sealed class PersistentPoiProtectionSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiStructureComponent, UnanchorAttemptEvent>(OnStructureUnanchor);
        SubscribeLocalEvent<PersistentPoiStructureComponent, BeingPulledAttemptEvent>(OnStructurePullAttempt);
        SubscribeLocalEvent<PersistentPoiStructureComponent, ExaminedEvent>(OnStructureExamined);
        SubscribeLocalEvent<PersistentPoiFurnishingComponent, ExaminedEvent>(OnFurnishingExamined);
    }

    private void OnStructureUnanchor(Entity<PersistentPoiStructureComponent> ent, ref UnanchorAttemptEvent args)
    {
        args.Cancel();

        if (_net.IsServer)
            _popup.PopupEntity(Loc.GetString("persistent-poi-structure-unanchor-denied"), ent, args.User, PopupType.SmallCaution);
    }

    // anchored works are static anyway; this also holds one that somehow came loose where it stands
    private void OnStructurePullAttempt(Entity<PersistentPoiStructureComponent> ent, ref BeingPulledAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnStructureExamined(Entity<PersistentPoiStructureComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("persistent-poi-structure-examine"));
        PushIntegrity(ent, args);
    }

    private void OnFurnishingExamined(Entity<PersistentPoiFurnishingComponent> ent, ref ExaminedEvent args)
    {
        PushIntegrity(ent, args);
    }

    private void PushIntegrity(EntityUid uid, ExaminedEvent args)
    {
        if (!TryComp<DamageableComponent>(uid, out var damageable))
            return;

        var ev = new PersistentPoiIntegrityEvent();
        RaiseLocalEvent(uid, ref ev);
        if (ev.DestroyedAt is not { } max || max <= FixedPoint2.Zero || max == FixedPoint2.MaxValue)
            return;

        var left = FixedPoint2.Max(FixedPoint2.Zero, max - damageable.TotalDamage);
        var fraction = left.Float() / max.Float();
        var colour = fraction > 0.66f ? "green" : fraction > 0.33f ? "yellow" : "red";
        args.PushMarkup(Loc.GetString("persistent-poi-structure-integrity",
            ("colour", colour),
            ("left", left.Int()),
            ("max", max.Int())));
    }
}
