using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

public sealed class NpcAimSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcAimComponent, DamageChangedEvent>(OnDamageChanged);
    }

    private void OnDamageChanged(Entity<NpcAimComponent> ent, ref DamageChangedEvent args)
    {
        if (args.DamageIncreased && args.Origin is { } origin && origin != ent.Owner)
            ent.Comp.LastHurt = _timing.CurTime;
    }

    /// <summary>
    /// Call on every LOS check. Returns the shoot delay to use when the target just came into view, else null.
    /// </summary>
    public float? UpdateSight(Entity<NpcAimComponent> ent, EntityUid target, bool inSight, bool wasInSight)
    {
        var comp = ent.Comp;
        var now = _timing.CurTime;

        if (!inSight)
            return null;

        var reacquired = comp.Target == target && now - comp.LastSeen < comp.ReacquireTime;
        comp.LastSeen = now;

        if (wasInSight && comp.Target == target)
            return null;

        if (!reacquired)
            comp.AimSince = now;

        comp.Target = target;
        return reacquired ? comp.ReacquireReaction : _random.NextFloat(comp.ReactionMin, comp.ReactionMax);
    }

    public Vector2 GetShotSpot(Entity<NpcAimComponent> ent, Vector2 shooterPos, Vector2 shooterVel, Vector2 targetPos,
        Vector2 targetVel, float shootSpeed)
    {
        var comp = ent.Comp;
        var now = _timing.CurTime;
        var toTarget = targetPos - shooterPos;
        var distance = toTarget.Length();

        if (distance < 0.01f)
            return targetPos;

        var spot = targetPos + targetVel * distance / shootSpeed * _random.NextFloat(comp.LeadMin, comp.LeadMax);

        var spread = comp.BaseSpread;

        var settled = (float) ((now - comp.AimSince).TotalSeconds / comp.SettleTime.TotalSeconds);
        if (settled < 1f)
            spread += comp.SettleSpread * (1f - MathF.Max(settled, 0f));

        spread += comp.MovingSpread * MathF.Min(1f, shooterVel.Length() / comp.FullSpeed);

        var dir = toTarget / distance;
        var lateral = MathF.Abs(targetVel.X * dir.Y - targetVel.Y * dir.X);
        spread += comp.TargetMovingSpread * MathF.Min(1f, lateral / comp.FullSpeed);

        if (comp.LastHurt is { } hurt && now < hurt + comp.HurtTime)
            spread += comp.HurtSpread;

        var error = MathHelper.DegreesToRadians((_random.NextFloat() - _random.NextFloat()) * spread);
        var aim = spot - shooterPos;
        return shooterPos + new Angle(error).RotateVec(aim);
    }

    public bool CanFire(Entity<NpcAimComponent> ent)
    {
        return _timing.CurTime >= ent.Comp.BurstPauseUntil;
    }

    public void OnShot(Entity<NpcAimComponent> ent, GunComponent gun)
    {
        var comp = ent.Comp;

        if (gun.FireRateModified < comp.BurstFireRate)
            return;

        if (comp.BurstLeft <= 0)
            comp.BurstLeft = _random.Next(comp.BurstMin, comp.BurstMax + 1);

        if (--comp.BurstLeft > 0)
            return;

        comp.BurstPauseUntil = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat(comp.BurstPauseMin, comp.BurstPauseMax));
    }
}
