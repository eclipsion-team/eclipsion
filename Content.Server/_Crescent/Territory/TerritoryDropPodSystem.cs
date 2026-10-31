using System.Numerics;
using Content.Server._Crescent.NpcSquad;
using Content.Shared._Crescent.Territory;
using Content.Shared.Camera;
using Robust.Server.Audio;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryDropPodSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly NpcSquadSystem _squad = default!;
    [Dependency] private readonly SharedCameraRecoilSystem _recoil = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private static readonly EntProtoId Pod = "TerritoryDropPod";

    private const float ShakeRange = 8f;

    private readonly List<EntityUid> _landing = new();

    public EntityUid Launch(EntityCoordinates target, EntProtoId soldier, string faction, string regionId, bool escort,
        EntityUid? recruiter = null, float delay = 0f)
    {
        var uid = Spawn(Pod, target);
        var pod = Comp<TerritoryDropPodComponent>(uid);
        pod.LandAt = _timing.CurTime + TimeSpan.FromSeconds(delay + pod.FallTime);
        Dirty(uid, pod);

        var payload = EnsureComp<TerritoryDropPodPayloadComponent>(uid);
        payload.Soldier = soldier;
        payload.Faction = faction;
        payload.RegionId = regionId;
        payload.Escort = escort;
        payload.Recruiter = recruiter;
        return uid;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<TerritoryDropPodComponent>();
        while (query.MoveNext(out var uid, out var pod))
        {
            if (!pod.Landed && now >= pod.LandAt)
                _landing.Add(uid);
        }

        foreach (var uid in _landing)
        {
            Land(uid);
        }

        _landing.Clear();
    }

    private void Land(EntityUid uid)
    {
        var pod = Comp<TerritoryDropPodComponent>(uid);
        pod.Landed = true;
        Dirty(uid, pod);
        EnsureComp<TimedDespawnComponent>(uid).Lifetime = pod.Linger;

        var coords = Transform(uid).Coordinates;
        _audio.PlayPvs(pod.LandSound, uid);
        Spawn("EffectSparks", coords);
        Shake(uid);

        if (!TryComp<TerritoryDropPodPayloadComponent>(uid, out var payload))
            return;

        var soldier = Spawn(payload.Soldier, coords);
        var marker = EnsureComp<TerritoryGarrisonSoldierComponent>(soldier);
        marker.Faction = payload.Faction;
        marker.RegionId = payload.RegionId;
        marker.Escort = payload.Escort;

        if (!payload.Escort)
        {
            // holds its post instead of following somebody off on a raid
            RemComp<NpcSquadRecruitableComponent>(soldier);
        }
        else if (payload.Recruiter is { } recruiter && !TerminatingOrDeleted(recruiter))
        {
            _squad.TryRecruit(recruiter, soldier);
        }

        RemComp<TerritoryDropPodPayloadComponent>(uid);
    }

    private void Shake(EntityUid pod)
    {
        var epicenter = _transform.GetMapCoordinates(pod);
        var players = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (players.MoveNext(out var player, out _, out var xform))
        {
            if (xform.MapID != epicenter.MapId)
                continue;

            var delta = epicenter.Position - _transform.GetWorldPosition(xform);
            var distance = delta.Length();
            if (distance >= ShakeRange)
                continue;

            if (distance < 0.01f)
                delta = new Vector2(0.01f, 0f);

            _recoil.KickCamera(player, -delta.Normalized() * 3f * (1f - distance / ShakeRange));
        }
    }
}
