using Content.Client.Gameplay;
using Content.Shared.Audio;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using AudioComponent = Robust.Shared.Audio.Components.AudioComponent;
using EngineAudioSystem = Robust.Client.Audio.AudioSystem;

namespace Content.Client.Audio;

public sealed partial class ContentAudioSystem : SharedContentAudioSystem
{
    // Need how much volume to change per tick and just remove it when it drops below "0"
    private readonly Dictionary<EntityUid, float> _fadingOut = new();

    // Need volume change per tick + target volume.
    private readonly Dictionary<EntityUid, (float VolumeChange, float TargetVolume)> _fadingIn = new();

    private readonly List<EntityUid> _fadeToRemove = new();

    private const float MinVolume = -32f;
    private const float DefaultDuration = 2f;

    /*
     * Gain multipliers for specific audio sliders.
     * The float value will get multiplied by this when setting
     * i.e. a gain of 0.5f x 3 will equal 1.5f which is supported in OpenAL.
     */

    public const float MasterVolumeMultiplier = 3f;
    public const float MidiVolumeMultiplier = 0.25f;
    public const float AmbienceMultiplier = 3f;
    public const float CombatMusicMultiplier = 3f;
    public const float AmbientMusicMultiplier = 3f;
    public const float LobbyMultiplier = 3f;
    public const float InterfaceMultiplier = 2f;
    public const float AnnouncerMultiplier = 3f;
    public const float CommunicationsMultiplier = 3f;
    // Multiplier for boombox / jukebox individual volume cvar.
    public const float BoomboxMultiplier = 3f;

    // Duck amount, in dB, that a fully cranked boombox ducking slider corresponds to.
    public const float BoomboxDuckMaxDb = 20f;

    public override void Initialize()
    {
        base.Initialize();

        UpdatesOutsidePrediction = true;
        // Run lifecycle cleanup after the engine has processed audio for this frame. This also
        // catches networked streams that arrive after the initial transition to the lobby.
        UpdatesAfter.Add(typeof(EngineAudioSystem));
        InitializeAmbientMusic();
        InitializeLobbyMusic();
        InitializeDucking();
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnLocalPlayerDetached);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent ev)
    {
        _fadingOut.Clear();
        _suppressedNetworkedLoops.Clear();
        _worldAudioSuppressed = false;

        // Preserve lobby/restart music but really stop every in-round source. Changing gain here
        // only muted the current source and allowed a later component state to start it again.
        var lobbyMusic = _lobbySoundtrackInfo?.MusicStreamEntityUid;
        var restartAudio = _lobbyRoundRestartAudioStream;
        StopAudioSources(lobbyMusic, restartAudio);
        PlayRestartSound(ev);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ShutdownAmbientMusic();
        ShutdownLobbyMusic();
        _suppressedNetworkedLoops.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        //UpdateAmbientMusic();
        UpdateLobbyMusic();
        UpdateFades(frameTime);
        UpdateDucking(frameTime);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        UpdateWorldAudioLifecycle();
    }

    #region World audio lifecycle

    /// <summary>
    /// Server-owned loops that were present when the local player detached. They are restarted
    /// once a new player entity is attached, since the server will not necessarily resend their
    /// unchanged Playing state after a respawn.
    /// </summary>
    private readonly HashSet<EntityUid> _suppressedNetworkedLoops = new();

    private bool _worldAudioSuppressed;

    private void OnLocalPlayerDetached(LocalPlayerDetachedEvent args)
    {
        SuppressWorldAudio();
    }

    private void UpdateWorldAudioLifecycle()
    {
        // A positional listener is only meaningful during gameplay with an attached player entity.
        // Lobby audio is client-owned and explicitly exempted below.
        if (_state.CurrentState is not GameplayStateBase || _player.LocalEntity == null)
        {
            SuppressWorldAudio();
            return;
        }

        if (!_worldAudioSuppressed)
            return;

        _worldAudioSuppressed = false;

        foreach (var uid in _suppressedNetworkedLoops)
        {
            if (!TryComp(uid, out AudioComponent? component) ||
                component.State != Robust.Shared.Audio.Components.AudioState.Playing ||
                !component.Params.Loop)
            {
                continue;
            }

            // ProcessStream will restart the source on the next audio frame and update its position
            // and gain for the newly attached player before it becomes audible.
            component.Started = false;
        }

        _suppressedNetworkedLoops.Clear();
    }

    private void SuppressWorldAudio(bool stopClientAudio = false)
    {
        _worldAudioSuppressed = true;

        var lobbyMusic = _lobbySoundtrackInfo?.MusicStreamEntityUid;
        var restartAudio = _lobbyRoundRestartAudioStream;
        var query = AllEntityQuery<AudioComponent>();

        while (query.MoveNext(out var uid, out var component))
        {
            if (uid == lobbyMusic || uid == restartAudio)
                continue;

            var clientSide = IsClientSide(uid);

            // Client-owned ambience and effects are recreated by their owning systems. Networked
            // loops are persistent server state, so remember those for the next attached player.
            if (!clientSide && component.Params.Loop)
                _suppressedNetworkedLoops.Add(uid);

            // Keep suppressing server/PVS audio for the whole lobby: component startup and state
            // deltas can legitimately arrive after the transition. Client-owned gameplay audio is
            // stopped by the explicit lobby transition, while fresh lobby UI sounds remain available.
            if (!clientSide || stopClientAudio)
            {
                // A just-created source may not be playing yet. Marking it started prevents the
                // engine's next ProcessStream pass from undoing this lifecycle cleanup.
                component.Started = true;

                if (component.Playing)
                    component.StopPlaying();
            }
        }
    }

    /// <summary>
    /// Immediately stops client audio sources without rewriting their AudioComponent State or Params.
    /// </summary>
    private void StopAudioSources(EntityUid? preservedStream = null, EntityUid? secondPreservedStream = null)
    {
        var query = AllEntityQuery<AudioComponent>();

        while (query.MoveNext(out var uid, out var component))
        {
            if (uid == preservedStream || uid == secondPreservedStream)
                continue;

            // Prevent the engine's next ProcessStream pass from auto-starting a source that had
            // not reached its first audio frame yet.
            component.Started = true;
            component.StopPlaying();
        }
    }

    #endregion

    #region Fades

    /// <summary>
    /// Gets the volume a fade should start from. AudioComponent.Volume is a straight passthrough for the
    /// source's current gain, so it reads back as -infinity whenever the stream happens to be silent: either
    /// a volume slider sits at 0, or the engine muted the stream for being on another map. Dividing that by
    /// the duration gives an infinite per-tick change, and the first frame the volume is finite again the
    /// fade lands on +infinity, which sticks in the component's AudioParams and makes OpenAL reject every
    /// gain set from then on. So fall back to the intended volume, which stays finite in those cases.
    /// </summary>
    private static bool TryGetFadeVolume(AudioComponent component, out float volume)
    {
        volume = component.Volume;

        if (!float.IsFinite(volume))
            volume = component.Params.Volume;

        return float.IsFinite(volume);
    }

    public void FadeOut(EntityUid? stream, AudioComponent? component = null, float duration = DefaultDuration)
    {
        if (stream == null || duration <= 0f || !Resolve(stream.Value, ref component, false))
            return;

        // Just in case
        // TODO: Maybe handle the removals by making it seamless?
        _fadingIn.Remove(stream.Value);

        if (!TryGetFadeVolume(component, out var curVolume) || curVolume <= MinVolume)
        {
            // Nothing audible left to fade, so go straight to what the fade would have ended on.
            _audio.Stop(stream);
            _fadingOut.Remove(stream.Value);
            return;
        }

        _fadingOut[stream.Value] = (curVolume - MinVolume) / duration;
    }

    public void FadeIn(EntityUid? stream, AudioComponent? component = null, float duration = DefaultDuration)
    {
        if (stream == null || duration <= 0f || !Resolve(stream.Value, ref component, false))
            return;

        if (!TryGetFadeVolume(component, out var curVolume) || curVolume < MinVolume)
            return;

        _fadingOut.Remove(stream.Value);
        var change = (MinVolume - curVolume) / duration;
        _fadingIn[stream.Value] = (change, curVolume);
        component.Volume = MinVolume;
    }

    private void UpdateFades(float frameTime)
    {
        _fadeToRemove.Clear();

        foreach (var (stream, change) in _fadingOut)
        {
            if (!TryComp(stream, out AudioComponent? component))
            {
                _fadeToRemove.Add(stream);
                continue;
            }

            var volume = component.Volume - change * frameTime;

            // The stream was silenced from under us mid-fade (map change, slider at 0), so the step above
            // is infinite. MathF.Max won't clamp +infinity, and letting it through would burn a permanent
            // infinite volume into the params. It's inaudible either way, so just end the fade here.
            if (!float.IsFinite(volume))
                volume = MinVolume;

            volume = MathF.Max(MinVolume, volume);
            _audio.SetVolume(stream, volume, component);

            if (volume.Equals(MinVolume))
            {
                _audio.Stop(stream);
                _fadeToRemove.Add(stream);
            }
        }

        foreach (var stream in _fadeToRemove)
        {
            _fadingOut.Remove(stream);
        }

        _fadeToRemove.Clear();

        foreach (var (stream, (change, target)) in _fadingIn)
        {
            // Cancelled elsewhere
            if (!TryComp(stream, out AudioComponent? component))
            {
                _fadeToRemove.Add(stream);
                continue;
            }

            var volume = component.Volume - change * frameTime;

            // Same as above: silenced mid-fade. Restart the ramp from the floor rather than letting an
            // infinite value through, so the fade picks back up once the stream is audible again.
            if (!float.IsFinite(volume))
                volume = MinVolume;

            volume = MathF.Min(target, volume);
            _audio.SetVolume(stream, volume, component);

            if (volume.Equals(target))
            {
                _fadeToRemove.Add(stream);
            }
        }

        foreach (var stream in _fadeToRemove)
        {
            _fadingIn.Remove(stream);
        }
    }

    #endregion
}

/// <summary>
/// Raised whenever ambient music tries to play.
/// </summary>
[ByRefEvent]
public record struct PlayAmbientMusicEvent(bool Cancelled = false);
