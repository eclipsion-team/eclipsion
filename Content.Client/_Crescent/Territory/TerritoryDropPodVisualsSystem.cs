using System.Numerics;
using Content.Shared._Crescent.Territory;
using Robust.Client.GameObjects;
using Robust.Shared.Timing;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._Crescent.Territory;

/// <summary>
/// Draws a drop pod coming down onto its marker. Worked out from the landing time each frame, so a pod that
/// comes into view halfway down is drawn halfway down.
/// </summary>
public sealed class TerritoryDropPodVisualsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string PodLayer = "pod";
    private const string TrailLayer = "trail";
    private const string MarkerLayer = "marker";
    private const string LandedLayer = "landed";

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<TerritoryDropPodComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var pod, out var sprite))
        {
            Entity<SpriteComponent?> ent = (uid, sprite);

            if (pod.Landed)
            {
                _sprite.LayerSetVisible(ent, PodLayer, false);
                _sprite.LayerSetVisible(ent, TrailLayer, false);
                _sprite.LayerSetVisible(ent, MarkerLayer, false);
                _sprite.LayerSetVisible(ent, LandedLayer, true);
                _sprite.SetDrawDepth(ent, (int) DrawDepth.Objects);
                continue;
            }

            var left = (float) (pod.LandAt - now).TotalSeconds;
            var falling = left <= pod.FallTime;
            _sprite.LayerSetVisible(ent, PodLayer, falling);
            _sprite.LayerSetVisible(ent, TrailLayer, falling);
            _sprite.LayerSetVisible(ent, LandedLayer, false);

            // slow from up high, fast at the end
            var t = Math.Clamp(left / MathF.Max(pod.FallTime, 0.01f), 0f, 1f);
            var height = pod.Height * t * t;
            _sprite.LayerSetOffset(ent, PodLayer, new Vector2(0f, height));
            _sprite.LayerSetOffset(ent, TrailLayer, new Vector2(0f, height + 1f));

            var pulse = 0.55f + 0.45f * MathF.Abs(MathF.Sin((float) now.TotalSeconds * 6f));
            _sprite.LayerSetScale(ent, MarkerLayer, new Vector2(1.4f - 0.6f * t));
            _sprite.LayerSetColor(ent, MarkerLayer, Color.White.WithAlpha(pulse));
        }
    }
}
