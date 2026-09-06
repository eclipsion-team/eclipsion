using Content.Client.Gameplay;
using Content.Shared.Audio.Jukebox;
using Content.Shared.CCVar;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.Client.Audio;

/// <summary>
/// Smoothly lowers ("ducks") the game's own ambient/combat music while the local
/// player is standing near a playing jukebox or boombox, so the boombox track can
/// be heard clearly. The closer you get, the more the game music is ducked, and it
/// eases back up smoothly once you walk away or the boombox stops.
/// </summary>
public sealed partial class ContentAudioSystem
{
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    // How fast (dB per second) the duck eases in and out. Lower = smoother/slower.
    private const float DuckChangeSpeed = 9f;

    // How much (in dB) to lower the game music by when standing on top of a boombox.
    // Negative; 0 means the player turned ducking off.
    private float _maxDuckDb;

    // Current applied duck offset in dB (<= 0). Eased toward the target each frame.
    private float _currentDuckDb;

    // Whether we touched the music volume last frame, so we know to restore it once.
    private bool _wasDucking;

    private void InitializeDucking()
    {
        Subs.CVar(_configManager, CCVars.BoomboxMusicDuck, OnDuckCVarChanged, true);
    }

    private void OnDuckCVarChanged(float db)
    {
        // Stored as a positive number in the CVar because "duck by 14 dB" reads better than -14.
        _maxDuckDb = -MathF.Abs(db);
    }

    private void UpdateDucking(float frameTime)
    {
        var target = GetDuckTarget();

        _currentDuckDb = MoveToward(_currentDuckDb, target, DuckChangeSpeed * frameTime);

        // Not ducking and nothing to restore: leave the music volume alone entirely
        // so the normal ambient/volume logic keeps ownership of it.
        if (MathF.Abs(_currentDuckDb) < 0.01f)
        {
            if (!_wasDucking)
                return;

            _currentDuckDb = 0f;
            ApplyDuck();
            _wasDucking = false;
            return;
        }

        _wasDucking = true;
        ApplyDuck();
    }

    /// <summary>
    /// Returns the strongest (most negative) duck offset warranted by any playing
    /// boombox near the local player, or 0 if none are in range.
    /// </summary>
    private float GetDuckTarget()
    {
        if (_maxDuckDb == 0f)
            return 0f;

        if (_state.CurrentState is not GameplayState)
            return 0f;

        if (_player.LocalEntity is not { } player || !Exists(player))
            return 0f;

        var playerCoords = _xform.GetMapCoordinates(player);
        var best = 0f;

        var jukeQuery = AllEntityQuery<JukeboxComponent>();
        while (jukeQuery.MoveNext(out var uid, out var juke))
        {
            best = MathF.Min(best, DuckFor(uid, juke.AudioStream, playerCoords));
        }

        return best;
    }

    /// <summary>
    /// Computes the duck offset (<= 0) contributed by a single source, scaled by how
    /// close the player is. Returns 0 if the source isn't playing or is out of range.
    /// </summary>
    private float DuckFor(EntityUid source, EntityUid? audioStream, MapCoordinates playerCoords)
    {
        // Deliberately not SharedAudioSystem.IsPlaying: that also requires the stream's networked
        // AudioState to be Playing, and a stream that is audible right now is reason enough to duck.
        if (audioStream is not { } stream ||
            !TryComp(stream, out AudioComponent? audio) ||
            !audio.Playing ||
            float.IsNegativeInfinity(audio.Params.Volume))
            return 0f;

        var coords = _xform.GetMapCoordinates(source);
        if (coords.MapId == MapId.Nullspace || coords.MapId != playerCoords.MapId)
            return 0f;

        // Duck over exactly the radius the stream is audible at, not a fixed distance. A fixed one
        // outruns a quiet source, so a boombox you can barely hear would still kill your own music.
        var range = audio.Params.MaxDistance;
        if (range <= 0f)
            return 0f;

        var dist = (coords.Position - playerCoords.Position).Length();
        if (dist >= range)
            return 0f;

        // 1 right on top of the boombox, 0 at the edge of the range.
        var factor = 1f - dist / range;
        return _maxDuckDb * factor;
    }

    /// <summary>
    /// Applies the current duck offset on top of the ambient music's intended volume.
    /// </summary>
    private void ApplyDuck()
    {
        if (_ambientMusicStream is not { } stream)
            return;

        // The track is on its way out and will be stopped by the fade; leave it alone.
        if (_fadingOut.ContainsKey(stream))
            return;

        if (!TryComp(stream, out AudioComponent? comp))
            return;

        var target = GetDuckedVolume(_ambientMusicVolume);

        // Mid fade-in (10s for ambient tracks) we don't set the volume directly, we retarget
        // the fade instead. Otherwise the duck would be ignored for the whole fade and then
        // slam the volume down by the full duck in a single frame the moment the fade finished.
        if (_fadingIn.TryGetValue(stream, out var fade))
        {
            _fadingIn[stream] = (fade.VolumeChange, target);
            return;
        }

        // Compare against the volume we last asked for, not AudioComponent.Volume: that one reads
        // straight back off the audio source, where it comes back as -infinity while the stream is
        // muted and, without EFX, has occlusion folded into it. Either would make this bail out and
        // never duck at all.
        if (comp.Params.Volume == target)
            return;

        _audio.SetVolume(stream, target, comp);
    }

    /// <summary>
    /// Applies the current duck to an intended music volume, keeping the result above
    /// <see cref="MinVolume"/>. Going under that floor breaks <see cref="FadeOut"/>: it would
    /// compute a negative per-tick change, so the stream fades *up* instead of down, never
    /// reaches MinVolume, is never stopped, and keeps playing over the next track.
    /// </summary>
    private float GetDuckedVolume(float baseVolume)
    {
        var floor = MathF.Min(baseVolume, MinVolume + 0.5f);
        return MathF.Max(baseVolume + _currentDuckDb, floor);
    }

    private static float MoveToward(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta)
            return target;

        return current + MathF.Sign(target - current) * maxDelta;
    }
}
