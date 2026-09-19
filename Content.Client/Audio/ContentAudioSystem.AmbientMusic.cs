using System.ComponentModel.DataAnnotations;
using System.Linq;
using Content.Client.Gameplay;
using Content.Shared._Crescent.SpaceBiomes;
using Content.Shared.Audio;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Random;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.State;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Timer = Robust.Shared.Timing.Timer;
using Robust.Shared.Utility;
using Content.Client.CombatMode;
using Content.Shared.CombatMode;
using System.IO;
using Robust.Shared.Toolshed.Commands.Values;
using Content.Shared.Preferences;
using Content.Client.Lobby;
using System.Diagnostics;
using System.Threading;
using Robust.Shared.Timing;
using Content.Shared._Crescent.HullrotFaction;

namespace Content.Client.Audio;

/// <summary>
/// This handles playing ambient music over time, and combat music per faction.
/// </summary>
public sealed partial class ContentAudioSystem
{
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IStateManager _state = default!;
    [Dependency] private readonly RulesSystem _rules = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly CombatModeSystem _combatModeSystem = default!; //CLIENT ONE. WHY ARE THERE 3???
    [Dependency] private readonly IPrototypeManager _protMan = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IClientPreferencesManager _prefsManager = default!;

    //options menu ---
    private static float _volumeSliderAmbient;
    private static float _volumeSliderCombat;
    private static bool _combatMusicToggle;
    // Faction-flavoured music: the per-faction combat tracks and station/ship themes. Off falls back
    // to the generic combat tracks and the biome's music.
    private bool _factionMusicToggle = true;
    //options menu ---

    // This stores the music stream. It's used to start/stop the music on the fly.
    private EntityUid? _ambientMusicStream;

    // Volume the current track is meant to play at (prototype volume plus the matching slider),
    // before boombox ducking. Tracked here rather than recomputed from _musicProto, which can
    // already describe the *next* track while the current one is still fading out.
    private float _ambientMusicVolume;

    // The prototype volume of the track that is actually playing, without any slider applied. Kept
    // so moving a slider can re-derive _ambientMusicVolume for *this* stream; reading _musicProto
    // there would price the current track using the next one's volume.
    private float _ambientMusicBaseVolume;

    // This stores the ambient music prototype to be played next.
    private AmbientMusicPrototype? _musicProto;

    // Need to keep track of the last biome we were in to re-play its music when we're out of combat mode
    private SpaceBiomePrototype? _lastBiome;

    // Time to wait in between replaying ambient music tracks. Should be at least 1-2 seconds to prevent possible overlapping.
    private TimeSpan _timeUntilNextAmbientTrack = TimeSpan.FromSeconds(10);

    // List of available ambient music tracks to sift through.
    private List<AmbientMusicPrototype>? _musicTracks;

    // Shuffle bags: keyed by SoundCollectionPrototype ID.
    // Each bag holds tracks not yet played; when empty it refills and reshuffles.
    // Shared across ambient and combat so each collection is independently no-repeat.
    private readonly Dictionary<string, List<string>> _musicBags = new();

    // Time in seconds for ambient music tracks to fade in. Set to 0 to play immediately.
    private float _ambientMusicFadeInTime = 10f;

    // Time in seconds for combat music tracks to fade in. Set to 0 to play immediately.
    private float _combatMusicFadeInTime = 2f;

    // Time that combat mode needs to be on to start playing music. Set to 0 to play immediately.
    private TimeSpan _combatStartUpTime = TimeSpan.FromSeconds(3.0);

    // Time that combat mode needs to be off to stop combat mode. Set to 0 to turn off as soon as combat mode is off.
    private TimeSpan _combatWindDownTime = TimeSpan.FromSeconds(30.0);

    // Combat mode state before checking to switch combat music off/on.
    // 1. We toggle combat mode. We fire SwitchCombatMusic in (timer) seconds.
    // 2. We save the state from step 1 in _lastCombatState
    // 3. When SwitchCombatMusic fires, we check if the current combat state is different than _lastCombatState. If it is, then we change music. If not, we keep it.
    bool _lastCombatState = false;

    // This is for checking if we play station or biome music after combat mode turns off.
    // There's probably a better way to do this but nobody will care until this code gets refactored again
    // If so, contact .2 from Hullrot. .2 | 2025
    bool _validStationMusic = false;

    // This stores the last station music we were in, so that we can play it when combat mode turns off.
    private string _lastStationMusic = "";
    // really stupid - i need this to check if the volume changes when you change the options menu options.
    private bool _isCombatMusicPlaying = false;


    private CancellationTokenSource _combatMusicCancelToken = new CancellationTokenSource();
    private CancellationTokenSource _ambientMusicCancelToken = new CancellationTokenSource();

    //used for logging, don't touch this
    private ISawmill _sawmill = default!;

    private void InitializeAmbientMusic()
    {
        SubscribeNetworkEvent<SpaceBiomeSwapMessage>(OnBiomeChange);
        SubscribeNetworkEvent<NewVesselEnteredMessage>(OnVesselChange);
        SubscribeLocalEvent<ToggleCombatActionEvent>(OnCombatModeToggle);

        Subs.CVar(_configManager, CCVars.AmbientMusicVolume, AmbienceCVarChanged, true);
        Subs.CVar(_configManager, CCVars.CombatMusicVolume, CombatCVarChanged, true);
        Subs.CVar(_configManager, CCVars.CombatMusicEnabled, CombatToggleChanged, true);
        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("audio.ambience");
        Subs.CVar(_configManager, CCVars.FactionMusicEnabled, FactionMusicToggleChanged, true);

        // Setup tracks to pull from. Runs once.
        _musicTracks = GetTracks();


        //no longer needed because we track my the current audio track's time
        //Timer.Spawn(_timeUntilNextAmbientTrack, () => ReplayAmbientMusic());

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnProtoReload);
        _state.OnStateChanged += OnStateChange;
        // On round end summary OR lobby cut audio.
        SubscribeNetworkEvent<RoundEndMessageEvent>(OnRoundEndMessage);
    }

    /// <summary>
    /// This function runs on a looping timer. The timer fires immediately after any AMBIENT (not combat) track is played, to play the next track.
    /// </summary>
    private void ReplayAmbientMusic()
    {
        // A replay timer can outlive its track while the client leaves gameplay. Never let a stale
        // callback start ambient music over the lobby or round-end screen.
        if (_state.CurrentState is not GameplayState)
            return;

        if (_musicProto == null) //if we don't find any, we play the default track.
        {
            _musicProto = _protMan.Index<AmbientMusicPrototype>("default");
            _lastBiome = _protMan.Index<SpaceBiomePrototype>("default");
        }

        SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID);

        string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

        PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _ambientMusicFadeInTime, false);

        ScheduleAmbientReplay(path);

    }

    private void OnBiomeChange(SpaceBiomeSwapMessage ev)
    {

        SpaceBiomePrototype biome = _protMan.Index<SpaceBiomePrototype>(ev.Biome); //get the biome prototype
        _lastBiome = biome; //save biome in case we are in combat mode

        if (_combatModeSystem.IsInCombatMode()) //we don't want to change music if we are in combat mode right now
            return;

        // if we're on the countsman or something moving, we don't want to switch the music
        if (UseStationMusic)
            return;

        FadeOut(_ambientMusicStream);

        if (_musicTracks == null)
            return;

        _musicProto = null;

        foreach (var ambient in _musicTracks)
        {
            if (biome.ID == ambient.ID) //if we find the biome that's matching the ambient's ID, we play that track!
            {
                //_sawmill.Debug($"found biome match: {biome.ID} == {ambient.ID}");
                _musicProto = ambient;
                break;
            }
        }

        if (_musicProto == null) //if we don't find any, we play the default track.
        {
            _musicProto = _protMan.Index<AmbientMusicPrototype>("default");
            _lastBiome = _protMan.Index<SpaceBiomePrototype>("default");
        }

        SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID);

        string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

        PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _ambientMusicFadeInTime, false);

        ScheduleAmbientReplay(path);
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="ev"></param>
    private void OnVesselChange(NewVesselEnteredMessage ev)
    {
        _sawmill.Debug($"went to ship {ev.Name}"); //SHOULD BE ONE WORD. Jackal, Countsman, PortBalreska...

        if (ev.AmbientMusicPrototype == "")
        {
            _validStationMusic = false;
            _sawmill.Debug("NO MUSIC FOUND FOR SHIP");
            return;
        }
        else
        {
            _validStationMusic = true;
            _lastStationMusic = ev.AmbientMusicPrototype;
            _sawmill.Debug("MUSIC FOUND FOR SHIP! " + ev.AmbientMusicPrototype);
        }

        // Still remembered above, so turning faction music back on can pick the station theme up.
        if (!_factionMusicToggle)
            return;

        if (_combatModeSystem.IsInCombatMode()) //we don't want to change music if we are in combat mode right now
            return;

        FadeOut(_ambientMusicStream);

        if (_musicTracks == null)
            return;

        _musicProto = null;

        foreach (var ambient in _musicTracks)
        {
            if (ev.AmbientMusicPrototype == ambient.ID) //if we find the biome that's matching the ambient's ID, we play that track!
            {
                //_sawmill.Debug($"found biome match: {biome.ID} == {ambient.ID}");
                _musicProto = ambient;
                break;
            }
        }

        if (_musicProto == null) //if we don't find any, we play the default track.
        {
            _musicProto = _protMan.Index<AmbientMusicPrototype>("default");
            //_lastBiome = _proto.Index<SpaceBiomePrototype>("default");
        }

        SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID);

        string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

        PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _ambientMusicFadeInTime, false);

        ScheduleAmbientReplay(path);
    }


    private void OnCombatModeToggle(ToggleCombatActionEvent ev)
    {
        if (_combatMusicToggle == false)
            return;
        if (!_timing.IsFirstTimePredicted == true) //needed, because combat mode is predicted, and triggers 7 times otherwise.
            return;

        // Without this, there's inconsistent behaviour to when combat mode toggles on/off.
        ResetCombatMusicToken();

        bool currentCombatState = _combatModeSystem.IsInCombatMode();

        _sawmill.Debug("ToggleCombatActionEvent performer: " + ev.Performer);
        string faction = "";
        if (!TryComp<HullrotFactionComponent>(ev.Performer, out HullrotFactionComponent? factionComp))
            _sawmill.Debug("NO HULLROT FACTION COMPONENT FOUND! YOU NEED TO ADD A FACTION COMPONENT TO THIS ROLE!");
        else
            faction = factionComp.Faction;
        if (currentCombatState)
            Timer.Spawn(_combatStartUpTime, () => SwitchCombatMusic(faction), _combatMusicCancelToken.Token);
        else
            Timer.Spawn(_combatWindDownTime, () => SwitchCombatMusic(faction), _combatMusicCancelToken.Token);

    }
    private void SwitchCombatMusic(string factionComponentString)
    {

        ResetAmbientReplayToken();

        if (_state.CurrentState is not GameplayState)
            return;


        bool currentCombatState = _combatModeSystem.IsInCombatMode();

        if (_lastCombatState == currentCombatState)
            return;

        _lastCombatState = currentCombatState;

        FadeOut(_ambientMusicStream);

        if (currentCombatState) //true = we toggled combat ON.
        {
            string combatFactionSuffix = ""; //this is added to "combatmode" to create "combatmodeNCWL", "combatmodeDSM", etc, to fetch combat tracks.

            // .2 | 2025 - this is an old variant that looked at your character's preferences prototype.
            // baldy fileld in hullrotfactioncomponent for every job so now we can #deprecateThisGarbage
            // if (_prefsManager.Preferences != null) //this literally cannot be null unless you're in lobby or something
            // {
            //     var profile = (HumanoidCharacterProfile) _prefsManager.Preferences.SelectedCharacter;

            //     combatFactionSuffix = profile.Faction; //becomes NCWL, DSM, etc.

            //     //_sawmill.Debug($"FACTION: {faction}");
            // }

            combatFactionSuffix = factionComponentString;

            //if we find a ambient music prototype for our faction, then pick that one!
            if (_factionMusicToggle && _protMan.TryIndex<AmbientMusicPrototype>("combatmode" + combatFactionSuffix, out var factionCombatMusicPrototype))
            {
                _musicProto = factionCombatMusicPrototype;
                SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID);

                string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

                PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _combatMusicFadeInTime, true);
            }
            else //if the faction combat music prototype does not exist, instead fall back to the default.
            {
                _musicProto = _protMan.Index<AmbientMusicPrototype>("combatmodedefault");
                SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID);

                string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

                PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _combatMusicFadeInTime, true);
            }
        }
        else                    //false = we toggled combat OFF
        {
            if (_lastBiome == null) //this should never happen still
            {
                if (_player.LocalSession != null) //THIS LITERALLY CANNOT BE NULL!! BUT IT COMPLAINS IF I DONT PUT THIS HERE!!!
                {
                    _entMan.TryGetComponent<SpaceBiomeTrackerComponent>(_player.LocalSession.AttachedEntity, out var comp);
                    if (comp != null)
                    {
                        if (comp.Biome != null)
                            _lastBiome = _protMan.Index<SpaceBiomePrototype>(comp.Biome);
                    }
                }
            }

            if (_lastBiome == null)
                return;


            // when combat mode turns off, do we have valid station music to play? if yes, play it. if not, play the biome's music.
            if (UseStationMusic)
            {
                _musicProto = _protMan.Index<AmbientMusicPrototype>(_lastStationMusic);
            }
            else
            {
                _musicProto = _protMan.Index<AmbientMusicPrototype>(_lastBiome.ID); //THIS CAN FUCK UP! BECAUSE THE ID MIGHT NOT HAVE MUSIC AND BE A FALLBACK!
            }
            SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID); //THIS IS WHAT ERRORS!

            string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

            PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _ambientMusicFadeInTime, false);

            ScheduleAmbientReplay(path);

        }
    }

    /// <summary>
    /// This is a helper function that actually plays the music tracks.
    /// </summary>
    /// <param name="path"> Path to music to play.</param>
    /// <param name="volume"> Volume modifier (put 0 to keep original volume).</param>
    /// <param name="fadein"> Seconds for the music to fade in. Put 0 for no fadein. </param>
    private void PlayMusicTrack(string path, float volume, float fadein, bool combatMode)
    {
        _isCombatMusicPlaying = combatMode;
        _sawmill.Debug($"NOW PLAYING: {path}" + " | COMBAT MODE: " + _isCombatMusicPlaying);

        _ambientMusicBaseVolume = volume;

        if (combatMode)
            volume += _volumeSliderCombat;
        else
            volume += _volumeSliderAmbient;

        _ambientMusicVolume = volume;

        // Start already ducked if a boombox is playing next to us, otherwise the track would open
        // at full volume and only get pulled down once the ducking update caught up.
        var strim = _audio.PlayGlobal(
            path,
            Filter.Local(),
            false,
            AudioParams.Default.WithVolume(GetDuckedVolume(volume)))!;

        _ambientMusicStream = strim.Value.Entity; //this plays it immediately, but fadein function later makes it actually fade in.

        if (fadein != 0)
            FadeIn(_ambientMusicStream, strim.Value.Component, fadein);
    }

    /// <summary>
    /// Picks the next unplayed track from a sound collection.
    /// Once all tracks have been played once the bag refills and reshuffles,
    /// guaranteeing no track repeats until the whole collection has been heard.
    /// </summary>
    private string PickNext(SoundCollectionPrototype soundcol)
    {
        if (!_musicBags.TryGetValue(soundcol.ID, out var bag) || bag.Count == 0)
        {
            bag = soundcol.PickFiles.Select(f => f.ToString()).ToList();
            _random.Shuffle(bag);
            _musicBags[soundcol.ID] = bag;
        }
        var track = bag[^1];
        bag.RemoveAt(bag.Count - 1);
        return track;
    }

    private List<AmbientMusicPrototype> GetTracks()
    {
        List<AmbientMusicPrototype> musictracks = new List<AmbientMusicPrototype>();

        bool fallback = true;
        foreach (var ambience in _protMan.EnumeratePrototypes<AmbientMusicPrototype>())
        {
            _sawmill.Debug($"logged ambient sound {ambience.ID}");
            musictracks.Add(ambience);
            fallback = false;
        }

        if (fallback) //if we somehow FOUND NO MUSIC TRACKS
        {
            _sawmill.Debug($"NO MUSIC FOUND, SOMETHING IS WRONG!");
        }

        return musictracks;
    }
    private void AmbienceCVarChanged(float obj)
    {
        _volumeSliderAmbient = SharedAudioSystem.GainToVolume(obj);

        //this changes the music volume live, while the music is playing. otherwise, the line above that changes the slider is the one that matters.

        // GetDuckedVolume keeps any active boombox duck applied, otherwise moving the slider
        // would jump the music back to full volume until the ducking update ran again.
        if (_ambientMusicStream == null || _isCombatMusicPlaying)
            return;

        _ambientMusicVolume = _ambientMusicBaseVolume + _volumeSliderAmbient;
        _audio.SetVolume(_ambientMusicStream, GetDuckedVolume(_ambientMusicVolume));
    }

    private void CombatCVarChanged(float obj)
    {
        _volumeSliderCombat = SharedAudioSystem.GainToVolume(obj);

        //this changes the music volume live, while the music is playing. otherwise, the line above that changes the slider is the one that matters.

        if (_ambientMusicStream == null || !_isCombatMusicPlaying)
            return;

        _ambientMusicVolume = _ambientMusicBaseVolume + _volumeSliderCombat;
        _audio.SetVolume(_ambientMusicStream, GetDuckedVolume(_ambientMusicVolume));
    }

    private void CombatToggleChanged(bool obj)
    {
        _combatMusicToggle = obj;

        if (_combatMusicToggle)
            return;

        // if we are turning combat music OFF, then do all this bullshit to turn music off and get ambient music back on
        ResetCombatMusicToken();

        ResetAmbientReplayToken();

        if (_state.CurrentState is not GameplayState)
            return;


        bool currentCombatState = _combatModeSystem.IsInCombatMode();

        if (_lastCombatState == currentCombatState)
            return;

        _lastCombatState = currentCombatState;

        FadeOut(_ambientMusicStream);

        if (_lastBiome == null) //this should never happen still
        {
            if (_player.LocalSession != null) //THIS LITERALLY CANNOT BE NULL!! BUT IT COMPLAINS IF I DONT PUT THIS HERE!!!
            {
                _entMan.TryGetComponent<SpaceBiomeTrackerComponent>(_player.LocalSession.AttachedEntity, out var comp);
                if (comp != null)
                {
                    if (comp.Biome != null)
                        _lastBiome = _protMan.Index<SpaceBiomePrototype>(comp.Biome);
                }
            }
        }

        if (_lastBiome == null)
            return;


        // when combat mode turns off, do we have valid station music to play? if yes, play it. if not, play the biome's music.
        if (_validStationMusic == true)
        {
            _musicProto = _protMan.Index<AmbientMusicPrototype>(_lastStationMusic);
        }
        else
        {
            _musicProto = _protMan.Index<AmbientMusicPrototype>(_lastBiome.ID); //THIS CAN FUCK UP! BECAUSE THE ID MIGHT NOT HAVE MUSIC AND BE A FALLBACK!
        }
        SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID); //THIS IS WHAT ERRORS!

        string path = PickNext(soundcol); // THIS WILL PICK A RANDOM SOUND. WE MAY WANT TO SPECIFY ONE INSTEAD!!

        PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _ambientMusicFadeInTime, false);

        ScheduleAmbientReplay(path);

    }

    private bool UseStationMusic => _validStationMusic && _factionMusicToggle;

    private void FactionMusicToggleChanged(bool obj)
    {
        _factionMusicToggle = obj;

        if (_state.CurrentState is not GameplayState)
            return;

        // Combat tracks pick the setting up with the next track. Only a station theme needs swapping
        // right away, otherwise the replay timer would keep looping it.
        if (_isCombatMusicPlaying || !_validStationMusic)
            return;

        FadeOut(_ambientMusicStream);

        if (!UseStationMusic || !_protMan.TryIndex<AmbientMusicPrototype>(_lastStationMusic, out _musicProto))
        {
            if (_lastBiome == null || !_protMan.TryIndex<AmbientMusicPrototype>(_lastBiome.ID, out _musicProto))
                _musicProto = _protMan.Index<AmbientMusicPrototype>("default");
        }

        SoundCollectionPrototype soundcol = _protMan.Index<SoundCollectionPrototype>(_musicProto.ID);

        string path = PickNext(soundcol);

        PlayMusicTrack(path, _musicProto.Sound.Params.Volume, _ambientMusicFadeInTime, false);

        ScheduleAmbientReplay(path);
    }

    private void ShutdownAmbientMusic()
    {
        _state.OnStateChanged -= OnStateChange;
        _ambientMusicCancelToken.Cancel();
        _combatMusicCancelToken.Cancel();
        _ambientMusicStream = _audio.Stop(_ambientMusicStream);
    }

    private void ScheduleAmbientReplay(string path)
    {
        ResetAmbientReplayToken();
        Timer.Spawn(_audio.GetAudioLength(path) + _timeUntilNextAmbientTrack,
            ReplayAmbientMusic,
            _ambientMusicCancelToken.Token);
    }

    private void ResetAmbientReplayToken()
    {
        _ambientMusicCancelToken.Cancel();
        _ambientMusicCancelToken = new CancellationTokenSource();
    }

    private void ResetCombatMusicToken()
    {
        _combatMusicCancelToken.Cancel();
        _combatMusicCancelToken = new CancellationTokenSource();
    }

    private void OnProtoReload(PrototypesReloadedEventArgs obj)
    {
        if (obj.WasModified<AmbientMusicPrototype>())
        {
            _musicTracks = GetTracks();
            _musicBags.Clear(); // rebuild bags on next pick
        }
    }
    ///<summary>
    /// This function handles the change from lobby to gameplay, disabling music when you're not in gameplay state.
    ///</summary>
    private void OnStateChange(StateChangedEventArgs obj)
    {
        if (obj.NewState is not GameplayState)
            DisableAmbientMusic();
    }

    private void OnRoundEndMessage(RoundEndMessageEvent ev)
    {
        ResetAmbientReplayToken();
        ResetCombatMusicToken();

        if (_ambientMusicStream == null)
        {
            _sawmill.Debug("AMBIENT MUSIC STREAM WAS NULL? FROM OnRoundEndMessage()");
            return;
        }
        // If scoreboard shows then just stop the music
        _ambientMusicStream = _audio.Stop(_ambientMusicStream);
    }
    public void DisableAmbientMusic()
    {
        ResetAmbientReplayToken();
        ResetCombatMusicToken();

        if (_ambientMusicStream == null)
        {
            _sawmill.Debug("AMBIENT MUSIC STREAM WAS NULL? FROM DisableAmbientMusic()");
            return;
        }
        FadeOut(_ambientMusicStream);
        _ambientMusicStream = null;
    }
}
