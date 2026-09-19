using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Audio.Jukebox;
using Content.Shared.Power;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using JukeboxComponent = Content.Shared.Audio.Jukebox.JukeboxComponent;
using Content.Server.Instruments; // Ratgore change

namespace Content.Server.Audio.Jukebox;


public sealed class JukeboxSystem : SharedJukeboxSystem
{
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly AppearanceSystem _appearanceSystem = default!;

    // Reused each tick to defer queue advancement until after the entity query
    // enumerator has finished iterating (see Update).
    private readonly List<EntityUid> _toAdvance = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<JukeboxComponent, JukeboxSelectedMessage>(OnJukeboxSelected);
        SubscribeLocalEvent<JukeboxComponent, JukeboxPlayingMessage>(OnJukeboxPlay);
        SubscribeLocalEvent<JukeboxComponent, JukeboxPauseMessage>(OnJukeboxPause);
        SubscribeLocalEvent<JukeboxComponent, JukeboxStopMessage>(OnJukeboxStop);
        SubscribeLocalEvent<JukeboxComponent, JukeboxSetTimeMessage>(OnJukeboxSetTime);
        SubscribeLocalEvent<JukeboxComponent, JukeboxQueueAddMessage>(OnJukeboxQueueAdd);
        SubscribeLocalEvent<JukeboxComponent, JukeboxQueueRemoveMessage>(OnJukeboxQueueRemove);
        SubscribeLocalEvent<JukeboxComponent, JukeboxQueueClearMessage>(OnJukeboxQueueClear);
        SubscribeLocalEvent<JukeboxComponent, JukeboxQueueNextMessage>(OnJukeboxQueueNext);
        SubscribeLocalEvent<JukeboxComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<JukeboxComponent, ComponentShutdown>(OnComponentShutdown);

        SubscribeLocalEvent<JukeboxComponent, PowerChangedEvent>(OnPowerChanged);
    }

    /// <summary>
    /// Params every jukebox track is started with. The volume here is only the base level; each
    /// client scales it further with their own boombox volume slider once the stream arrives.
    /// Rolloff is zeroed because the client's jukebox system plays music as a flat stream and
    /// applies the distance falloff itself; OpenAL's positional processing garbles long tracks.
    /// </summary>
    private static AudioParams GetAudioParams(JukeboxComponent component)
    {
        return AudioParams.Default
            .WithMaxDistance(component.Range)
            .WithVolume(component.Volume)
            .WithRolloffFactor(0f);
    }

    /// <summary>
    /// Starts a jukebox track and applies jukebox-specific spatial audio behavior.
    /// Long-form music is especially unpleasant through OpenAL's aggressive low-pass occlusion,
    /// so prototypes with occlusion disabled retain distance attenuation without that filter.
    /// </summary>
    private EntityUid? PlayTrack(EntityUid uid, JukeboxComponent component, JukeboxPrototype track)
    {
        var stream = Audio.PlayPvs(track.Path, uid, GetAudioParams(component));
        if (stream == null)
            return null;

        if (!component.Occlusion)
        {
            stream.Value.Component.Flags |= AudioFlags.NoOcclusion;
            Dirty(stream.Value.Entity, stream.Value.Component);
        }

        return stream.Value.Entity;
    }

    private void OnComponentInit(EntityUid uid, JukeboxComponent component, ComponentInit args)
    {
        if (HasComp<ApcPowerReceiverComponent>(uid))
        {
            TryUpdateVisualState(uid, component);
        }
    }

    private void OnJukeboxPlay(EntityUid uid, JukeboxComponent component, ref JukeboxPlayingMessage args)
    {
        if (Exists(component.AudioStream))
        {
            Audio.SetState(component.AudioStream, AudioState.Playing);
        }
        else
        {
            component.AudioStream = Audio.Stop(component.AudioStream);

            if (string.IsNullOrEmpty(component.SelectedSongId) ||
                !_protoManager.TryIndex(component.SelectedSongId, out var jukeboxProto))
            {
                return;
            }

            component.AudioStream = PlayTrack(uid, component, jukeboxProto);
            Dirty(uid, component);
        }

        // Ratgore start
        if (!HasComp<ActiveInstrumentComponent>(uid))
        {
            AddComp<ActiveInstrumentComponent>(uid);
        }
        // Ratgore end
    }

    private void OnJukeboxPause(Entity<JukeboxComponent> ent, ref JukeboxPauseMessage args)
    {
        Audio.SetState(ent.Comp.AudioStream, AudioState.Paused);
    }

    private void OnJukeboxSetTime(EntityUid uid, JukeboxComponent component, JukeboxSetTimeMessage args)
    {
        if (TryComp(args.Actor, out ActorComponent? actorComp))
        {
            var offset = actorComp.PlayerSession.Channel.Ping * 1.5f / 1000f;
            Audio.SetPlaybackPosition(component.AudioStream, args.SongTime + offset);
        }
    }

    private void OnJukeboxQueueAdd(EntityUid uid, JukeboxComponent component, JukeboxQueueAddMessage args)
    {
        if (string.IsNullOrEmpty(args.SongId) || !_protoManager.HasIndex(args.SongId))
            return;

        component.Queue.Add(args.SongId);
        // Adding to the queue implies the user wants it to keep playing, so make
        // sure it advances once the current song ends.
        component.QueueActive = true;

        // Nothing is playing right now, so kick off the queue immediately.
        if (!Exists(component.AudioStream))
            AdvanceQueue(uid, component);
        else
            Dirty(uid, component);
    }

    private void OnJukeboxQueueRemove(EntityUid uid, JukeboxComponent component, JukeboxQueueRemoveMessage args)
    {
        if (args.Index < 0 || args.Index >= component.Queue.Count)
            return;

        component.Queue.RemoveAt(args.Index);
        Dirty(uid, component);
    }

    private void OnJukeboxQueueClear(EntityUid uid, JukeboxComponent component, JukeboxQueueClearMessage args)
    {
        component.Queue.Clear();
        Dirty(uid, component);
    }

    private void OnJukeboxQueueNext(EntityUid uid, JukeboxComponent component, JukeboxQueueNextMessage args)
    {
        // Nothing queued to skip to, so leave the current song playing.
        if (component.Queue.Count == 0)
            return;

        component.QueueActive = true;
        AdvanceQueue(uid, component);
    }

    /// <summary>
    /// Pops the next song off the queue and starts playing it. Deactivates the
    /// queue if there is nothing left to play.
    /// </summary>
    private void AdvanceQueue(EntityUid uid, JukeboxComponent component)
    {
        if (component.Queue.Count == 0)
        {
            component.QueueActive = false;
            Dirty(uid, component);
            return;
        }

        var next = component.Queue[0];
        component.Queue.RemoveAt(0);
        component.SelectedSongId = next;
        component.QueueActive = true;
        PlaySelected(uid, component);
    }

    /// <summary>
    /// Plays <see cref="JukeboxComponent.SelectedSongId"/> from the start.
    /// </summary>
    private void PlaySelected(EntityUid uid, JukeboxComponent component)
    {
        component.AudioStream = Audio.Stop(component.AudioStream);

        if (string.IsNullOrEmpty(component.SelectedSongId) ||
            !_protoManager.TryIndex(component.SelectedSongId, out var jukeboxProto))
        {
            Dirty(uid, component);
            return;
        }

        component.AudioStream = PlayTrack(uid, component, jukeboxProto);

        // If the track failed to start there is no stream, which would make the queue
        // try to advance again every tick. Stop the queue instead of spinning.
        if (!Exists(component.AudioStream))
            component.QueueActive = false;

        // Ratgore start
        if (!HasComp<ActiveInstrumentComponent>(uid))
        {
            AddComp<ActiveInstrumentComponent>(uid);
        }
        // Ratgore end

        Dirty(uid, component);
    }

    private void OnPowerChanged(Entity<JukeboxComponent> entity, ref PowerChangedEvent args)
    {
        TryUpdateVisualState(entity);

        if (!this.IsPowered(entity.Owner, EntityManager))
        {
            Stop(entity);
        }
    }

    private void OnJukeboxStop(Entity<JukeboxComponent> entity, ref JukeboxStopMessage args)
    {
        Stop(entity);
    }

    private void Stop(Entity<JukeboxComponent> entity)
    {
        Audio.SetState(entity.Comp.AudioStream, AudioState.Stopped);
        entity.Comp.QueueActive = false;
        Dirty(entity);

        RemComp<ActiveInstrumentComponent>(entity); // Ratgore change: the neural net is the one saying clever things
    }

    private void OnJukeboxSelected(EntityUid uid, JukeboxComponent component, JukeboxSelectedMessage args)
    {
        if (!Audio.IsPlaying(component.AudioStream))
        {
            component.SelectedSongId = args.SongId;
            DirectSetVisualState(uid, JukeboxVisualState.Select);
            component.Selecting = true;
            component.AudioStream = Audio.Stop(component.AudioStream);
        }

        Dirty(uid, component);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<JukeboxComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Selecting)
            {
                comp.SelectAccumulator += frameTime;
                if (comp.SelectAccumulator >= 0.5f)
                {
                    comp.SelectAccumulator = 0f;
                    comp.Selecting = false;

                    TryUpdateVisualState(uid, comp);
                }
            }

            // The stream entity is removed once a track finishes playing. A paused
            // track still exists, so this only fires on a natural end (or stop, but
            // stopping clears QueueActive so we won't restart).
            //
            // We only collect here and advance below: AdvanceQueue spawns a new audio
            // entity, and spawning/deleting entities while this EntityQueryEnumerator is
            // still iterating can corrupt the enumeration (this was freezing the jukebox
            // on the song change).
            if (comp.QueueActive && !Exists(comp.AudioStream))
            {
                _toAdvance.Add(uid);
            }
        }

        foreach (var uid in _toAdvance)
        {
            if (TryComp<JukeboxComponent>(uid, out var comp))
                AdvanceQueue(uid, comp);
        }

        _toAdvance.Clear();
    }

    private void OnComponentShutdown(EntityUid uid, JukeboxComponent component, ComponentShutdown args)
    {
        component.AudioStream = Audio.Stop(component.AudioStream);
    }

    private void DirectSetVisualState(EntityUid uid, JukeboxVisualState state)
    {
        _appearanceSystem.SetData(uid, JukeboxVisuals.VisualState, state);
    }

    private void TryUpdateVisualState(EntityUid uid, JukeboxComponent? jukeboxComponent = null)
    {
        if (!Resolve(uid, ref jukeboxComponent))
            return;

        var finalState = JukeboxVisualState.On;

        if (!this.IsPowered(uid, EntityManager))
        {
            finalState = JukeboxVisualState.Off;
        }

        _appearanceSystem.SetData(uid, JukeboxVisuals.VisualState, finalState);
    }
}
