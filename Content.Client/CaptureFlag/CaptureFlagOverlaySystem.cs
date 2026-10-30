using System.Numerics;
using Content.Shared._Crescent.Territory;
using Content.Shared.CaptureFlag;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Maths;

namespace Content.Client.CaptureFlag;

public sealed class CaptureFlagOverlaySystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    private CaptureFlagOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new CaptureFlagOverlay();
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
    }
}

public sealed class CaptureFlagOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    private readonly TransformSystem _xform;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public CaptureFlagOverlay()
    {
        IoCManager.InjectDependencies(this);
        _xform = _entMan.System<TransformSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var query = _entMan.AllEntityQueryEnumerator<CaptureFlagComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var flag, out var xform))
        {
            if (xform.MapID != args.MapId)
                continue;

            if (_entMan.HasComponent<PersistentCaptureRegionComponent>(uid))
                continue;

            var worldPos = _xform.GetWorldPosition(uid);

            var color = flag.Stage switch
            {
                CaptureFlagStage.Contested => Color.OrangeRed,
                CaptureFlagStage.Neutralizing => Color.Goldenrod,
                CaptureFlagStage.Capturing => Color.CornflowerBlue,
                _ => flag.OwnerTeam == null ? Color.Gray : Color.LimeGreen
            };

            args.WorldHandle.DrawCircle(worldPos, flag.Radius, color.WithAlpha(0.35f));
            args.WorldHandle.DrawCircle(worldPos, flag.Radius, color.WithAlpha(0.9f), filled: false);

            args.WorldHandle.DrawCircle(worldPos, 0.08f, color.WithAlpha(0.9f));
            var stageTime = flag.Stage == CaptureFlagStage.Neutralizing ? flag.NeutralizeTime :
                flag.Stage == CaptureFlagStage.Capturing ? flag.CaptureTime : 0f;

            if (stageTime > 0f && flag.ProgressSeconds > 0f)
            {
                var frac = Math.Clamp(flag.ProgressSeconds / stageTime, 0f, 1f);
                var angle = new Angle((MathF.PI * 2f) * frac);
                var end = worldPos + angle.ToVec() * flag.Radius;
                args.WorldHandle.DrawLine(worldPos, end, color.WithAlpha(0.9f));
            }
        }
    }
}

