using System.Numerics;
using Content.Shared.Audio.Jukebox;
using Content.Shared.CCVar;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Client.Audio.Jukebox;


public sealed class JukeboxSystem : SharedJukeboxSystem
{
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly AnimationPlayerSystem _animationPlayer = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    /// <summary>
    /// The listener's boombox volume slider, converted from gain to dB so it can be added on top
    /// of the jukebox's own base volume.
    /// </summary>
    private float _volumeSlider;

    public override void Initialize()
    {
        base.Initialize();
        // Runs after the engine has positioned each stream, so the overrides below win.
        UpdatesAfter.Add(typeof(Robust.Client.Audio.AudioSystem));
        SubscribeLocalEvent<JukeboxComponent, AppearanceChangeEvent>(OnAppearanceChange);
        SubscribeLocalEvent<JukeboxComponent, AnimationCompletedEvent>(OnAnimationCompleted);
        SubscribeLocalEvent<JukeboxComponent, AfterAutoHandleStateEvent>(OnJukeboxAfterState);

        Subs.CVar(_cfg, CCVars.BoomboxVolume, OnVolumeCVarChanged, true);

        _protoManager.PrototypesReloaded += OnProtoReload;
    }

    private void OnVolumeCVarChanged(float gain)
    {
        _volumeSlider = SharedAudioSystem.GainToVolume(gain);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _protoManager.PrototypesReloaded -= OnProtoReload;
    }

    private void OnProtoReload(PrototypesReloadedEventArgs obj)
    {
        if (!obj.WasModified<JukeboxPrototype>())
            return;

        var query = AllEntityQuery<JukeboxComponent, UserInterfaceComponent>();

        while (query.MoveNext(out var uid, out _, out var ui))
        {
            if (!_uiSystem.TryGetOpenUi<JukeboxBoundUserInterface>((uid, ui), JukeboxUiKey.Key, out var bui))
                continue;

            bui.PopulateMusic();
        }
    }

    private void OnJukeboxAfterState(Entity<JukeboxComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!_uiSystem.TryGetOpenUi<JukeboxBoundUserInterface>(ent.Owner, JukeboxUiKey.Key, out var bui))
            return;

        bui.Reload();
    }

    /// <summary>
    /// Plays every jukebox stream as flat music instead of a positional sound. OpenAL treats a
    /// mono track as a point source 5 units off the listener plane (the engine's z-offset), with
    /// HRTF, panning, Doppler and its own distance model all on top; the further you walk the
    /// harder that bends a full music track, until it is plainly garbled. Pinning the source to the
    /// listener with the listener's velocity switches all of that off (the server zeroes OpenAL's
    /// rolloff), and the distance falloff is applied here as a plain volume change instead.
    ///
    /// Every jukebox is visited each frame rather than caching its stream: the stream entity can
    /// arrive a few ticks after the jukebox's state, and the server re-sends the stream's params
    /// (wiping the local volume) whenever it changes them.
    /// </summary>
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var listener = _eye.CurrentEye.Position;
        var listenerVelocity = _player.LocalEntity is { } local
            ? _physics.GetMapLinearVelocity(local)
            : Vector2.Zero;

        var query = EntityQueryEnumerator<JukeboxComponent>();
        while (query.MoveNext(out var comp))
        {
            if (comp.AudioStream is not { } stream || !TryComp(stream, out AudioComponent? audio))
                continue;

            var source = _xform.GetMapCoordinates(stream);
            if (source.MapId != listener.MapId)
                continue;

            var distance = (source.Position - listener.Position).Length();
            var falloff = SharedAudioSystem.GainToVolume(GetFalloff(distance, audio.Params));

            ApplyVolume(stream, comp.Volume + _volumeSlider + falloff, audio);
            audio.Position = listener.Position;
            audio.Velocity = listenerVelocity;
        }
    }

    /// <summary>
    /// The engine's default linear-clamped distance model, measured the way OpenAL would have
    /// measured it (including the z-offset), so a jukebox fades out exactly as loudly as before.
    /// </summary>
    private float GetFalloff(float distance, AudioParams audioParams)
    {
        if (distance >= audioParams.MaxDistance)
            return 0f;

        var reference = Audio.GetAudioDistance(audioParams.ReferenceDistance);
        var max = Audio.GetAudioDistance(audioParams.MaxDistance);

        if (max <= reference)
            return 1f;

        var clamped = Math.Clamp(Audio.GetAudioDistance(distance), reference, max);
        return 1f - (clamped - reference) / (max - reference);
    }

    private void ApplyVolume(EntityUid stream, float target, AudioComponent audio)
    {
        if (audio.Params.Volume == target)
            return;

        Audio.SetVolume(stream, target, audio);
    }

    private void OnAnimationCompleted(EntityUid uid, JukeboxComponent component, AnimationCompletedEvent args)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        if (!TryComp<AppearanceComponent>(uid, out var appearance) ||
            !_appearanceSystem.TryGetData<JukeboxVisualState>(uid, JukeboxVisuals.VisualState, out var visualState, appearance))
        {
            visualState = JukeboxVisualState.On;
        }

        UpdateAppearance(uid, visualState, component, sprite);
    }

    private void OnAppearanceChange(EntityUid uid, JukeboxComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!args.AppearanceData.TryGetValue(JukeboxVisuals.VisualState, out var visualStateObject) ||
            visualStateObject is not JukeboxVisualState visualState)
        {
            visualState = JukeboxVisualState.On;
        }

        UpdateAppearance(uid, visualState, component, args.Sprite);
    }

    private void UpdateAppearance(EntityUid uid, JukeboxVisualState visualState, JukeboxComponent component, SpriteComponent sprite)
    {
        SetLayerState(JukeboxVisualLayers.Base, component.OffState, sprite);

        switch (visualState)
        {
            case JukeboxVisualState.On:
                SetLayerState(JukeboxVisualLayers.Base, component.OnState, sprite);
                break;

            case JukeboxVisualState.Off:
                SetLayerState(JukeboxVisualLayers.Base, component.OffState, sprite);
                break;

            case JukeboxVisualState.Select:
                PlayAnimation(uid, JukeboxVisualLayers.Base, component.SelectState, 1.0f, sprite);
                break;
        }
    }

    private void PlayAnimation(EntityUid uid, JukeboxVisualLayers layer, string? state, float animationTime, SpriteComponent sprite)
    {
        if (string.IsNullOrEmpty(state))
            return;

        if (!_animationPlayer.HasRunningAnimation(uid, state))
        {
            var animation = GetAnimation(layer, state, animationTime);
            sprite.LayerSetVisible(layer, true);
            _animationPlayer.Play(uid, animation, state);
        }
    }

    private static Animation GetAnimation(JukeboxVisualLayers layer, string state, float animationTime)
    {
        return new Animation
        {
            Length = TimeSpan.FromSeconds(animationTime),
            AnimationTracks =
                {
                    new AnimationTrackSpriteFlick
                    {
                        LayerKey = layer,
                        KeyFrames =
                        {
                            new AnimationTrackSpriteFlick.KeyFrame(state, 0f)
                        }
                    }
                }
        };
    }

    private void SetLayerState(JukeboxVisualLayers layer, string? state, SpriteComponent sprite)
    {
        if (string.IsNullOrEmpty(state))
            return;

        sprite.LayerSetVisible(layer, true);
        sprite.LayerSetAutoAnimated(layer, true);
        sprite.LayerSetState(layer, state);
    }
}
