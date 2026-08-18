using System;
using System.Collections;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// Chooses and blends the music.
    ///
    /// There is exactly one rule here that everything else follows from: music never
    /// switches, it crossfades. Two decks are allocated once at boot and every change of
    /// biome, time of day, weather or game mode is a timed blend from the deck that is
    /// playing to the deck that is about to. A request for the track that is already
    /// playing is ignored, so repeated BiomeEntered events -- which the overworld may
    /// fire on every trigger re-entry -- cannot restart or stutter the music. A request
    /// that arrives while a blend is still running retargets that blend instead, whether
    /// it names a third track or the one being faded away from; either way the music
    /// carries on from the level it is at.
    ///
    /// The encounter sting is the one deliberate exception to crossfading, and it is
    /// still not a cut: it plays on its own deck while the exploration theme fades under
    /// it, and it was composed to end on the pitch the battle theme opens on so the
    /// hand-off lands on a shared note.
    ///
    /// Scripted scenes are the one stretch where the world does not get a vote: while an
    /// episode holds the pad (<see cref="SetCinematicHold"/>), ambient arrivals are
    /// remembered rather than played, and only an explicit cue can start music.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Music Director")]
    [DefaultExecutionOrder(-400)]
    public sealed class MusicDirector : MonoBehaviour
    {
        [Header("Crossfade")]
        [Tooltip("Shape of the blend, evaluated 0 to 1. Combined with the equal-power law below.")]
        [SerializeField]
        private AnimationCurve fadeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Keep perceived loudness constant through the blend. Turn off for a " +
                 "deliberate dip in the middle of the crossfade.")]
        [SerializeField] private bool equalPower = true;

        [SerializeField, Range(0.1f, 8f)] private float biomeFade = 2.0f;
        [SerializeField, Range(0.1f, 12f)] private float timeOfDayFade = 4.0f;
        [SerializeField, Range(0.1f, 4f)] private float battleFade = 0.8f;
        [SerializeField, Range(0.1f, 6f)] private float outroFade = 1.6f;

        [Header("Behaviour")]
        [Tooltip("Treat dusk as night for track selection.")]
        [SerializeField] private bool duskUsesNightTheme = true;

        [Tooltip("Level trim applied to music during heavy weather so ambience can come " +
                 "forward. Blended in over two seconds, never snapped.")]
        [SerializeField, Range(-12f, 0f)] private float heavyWeatherTrimDb = -2.5f;

        [SerializeField, Range(0f, 1f)] private float musicLevel = 1f;

        private AudioSourcePool _pool;
        private AudioSource _deckA;
        private AudioSource _deckB;
        private AudioSource _stingDeck;
        private bool _aIsActive = true;

        private string _currentTrack;
        private string _pendingTrack;
        private Coroutine _fade;

        private string _biome = AudioIds.BiomeRoute;
        private TimeOfDay _timeOfDay = TimeOfDay.Day;
        private Weather _weather = Weather.Clear;
        private GameMode _mode = GameMode.Boot;
        private BattleKind _battleKind = BattleKind.Wild;

        private AudioDirector _director;
        private float _duckGain = 1f;
        private float _weatherTrim = 1f;
        private float _weatherTrimTarget = 1f;

        /// <summary>
        /// True while a scripted episode holds the pad. See <see cref="SetCinematicHold"/>.
        /// </summary>
        private bool _cinematicHold;

        public string CurrentTrack => _currentTrack;

        /// <summary>
        /// The deck that owns the track being established, and the one being left behind.
        ///
        /// The roles are swapped when a blend *starts* rather than when it finishes, so
        /// that for the whole of a crossfade Active is the deck rising towards
        /// <see cref="_pendingTrack"/> and Idle is the deck falling away from
        /// <see cref="_currentTrack"/>. A request arriving mid-blend can then tell which
        /// deck is which, which is the whole of what it needs to retarget correctly.
        /// </summary>
        private AudioSource Active => _aIsActive ? _deckA : _deckB;
        private AudioSource Idle => _aIsActive ? _deckB : _deckA;

        // ------------------------------------------------------------------------------

        private static MusicDirector _instance;

        private void Awake()
        {
            // First one wins, and any later arrival removes itself.
            //
            // Both playable scenes are loaded together, so each brings its own GameHosts and
            // its own director. The streamer destroys the duplicate's root — but not before
            // that copy has registered itself on the hub and had its decks torn out from
            // under it, and a crossfade already in flight then writes to an AudioSource Unity
            // has destroyed. That is the NullReferenceException the web build reported on
            // every load, from a coroutine whose stack said only "Crossfade".
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;

            // Three sources allocated once. Nothing here instantiates per playback.
            var group = AudioDirector.TryResolve(out _director)
                ? _director.GroupFor(AudioBus.Music)
                : null;
            _pool = new AudioSourcePool(transform, "MusicDecks", 3, group);
            _deckA = _pool.RentReserved();
            _deckB = _pool.RentReserved();
            _stingDeck = _pool.RentReserved();

            ServiceHub.Register(this);
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;
            // Only while the hub still holds this one: a copy arriving with a streamed band
            // and standing down must not take the live director's slot with it.
            ServiceHub.Unregister(this);
        }

        private void OnEnable()
        {
            GameEvents.BiomeEntered += OnBiomeEntered;
            GameEvents.TimeOfDayChanged += OnTimeOfDayChanged;
            GameEvents.WeatherChanged += OnWeatherChanged;
            GameEvents.ModeChanged += OnModeChanged;

            if (_director != null || AudioDirector.TryResolve(out _director))
            {
                _director.MusicDuckChanged += OnDuckChanged;
                _duckGain = _director.MusicDuckGain;
            }
        }

        private void OnDisable()
        {
            GameEvents.BiomeEntered -= OnBiomeEntered;
            GameEvents.TimeOfDayChanged -= OnTimeOfDayChanged;
            GameEvents.WeatherChanged -= OnWeatherChanged;
            GameEvents.ModeChanged -= OnModeChanged;
            if (_director != null) _director.MusicDuckChanged -= OnDuckChanged;
        }

        private void OnDuckChanged(float gain) => _duckGain = gain;

        private void Update()
        {
            // Weather trim glides; a step change in music level would be exactly the kind
            // of abrupt transition this system exists to avoid.
            if (!Mathf.Approximately(_weatherTrim, _weatherTrimTarget))
                _weatherTrim = Mathf.MoveTowards(_weatherTrim, _weatherTrimTarget,
                                                 Time.unscaledDeltaTime * 0.5f);
            ApplyLevels();
        }

        private void ApplyLevels()
        {
            if (_fade != null) return;   // the fade coroutine owns deck volume while running
            float g = musicLevel * _duckGain * _weatherTrim;
            if (Active != null && Active.isPlaying) Active.volume = g;
            if (Idle != null && Idle.isPlaying) Idle.volume = 0f;
        }

        // ---- event handlers ----------------------------------------------------------

        private void OnBiomeEntered(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return;
            var normalised = biomeId.Trim().ToLowerInvariant();
            if (normalised == _biome) return;
            _biome = normalised;
            if (CanStartWorldMusic()) PlayTrack(SelectExplorationTrack(), biomeFade);
        }

        private void OnTimeOfDayChanged(TimeOfDay from, TimeOfDay to)
        {
            _timeOfDay = to;
            // A slow blend: the day and night arrangements share their harmony, so a long
            // crossfade sits on consonant material for its whole duration.
            if (CanStartWorldMusic()) PlayTrack(SelectExplorationTrack(), timeOfDayFade);
        }

        private void OnWeatherChanged(Weather from, Weather to)
        {
            _weather = to;
            _weatherTrimTarget = IsHeavy(to) ? Mathf.Pow(10f, heavyWeatherTrimDb / 20f) : 1f;
            if (CanStartWorldMusic()) PlayTrack(SelectExplorationTrack(), biomeFade);
        }

        private void OnModeChanged(GameMode from, GameMode to)
        {
            _mode = to;
            switch (to)
            {
                case GameMode.Exploring:
                    // Gated by the hold as well: a scripted episode never pushes a mode of
                    // its own, so every dialogue beat inside one ends on a pop back to
                    // Exploring — and during the opening that pop was starting the town
                    // theme over the professor's cold open, under a black screen.
                    if (!_cinematicHold) PlayTrack(SelectExplorationTrack(), outroFade);
                    break;

                case GameMode.EncounterIntro:
                    PlayEncounterSting();
                    break;

                case GameMode.Battle:
                    PlayTrack(_battleKind == BattleKind.Trainer
                        ? AudioIds.MusicBattleTrainer
                        : AudioIds.MusicBattleWild, battleFade);
                    break;

                case GameMode.BattleOutro:
                    // The fanfare is fired by BattleAudioPresenter on BattleEndedEvent and
                    // ducks the loop; here we only need the loop to recede under it.
                    FadeOutAll(outroFade);
                    break;

                // Menu, Dialogue and Cutscene deliberately leave the music running --
                // opening a menu should not restart the score.
            }
        }

        private static bool IsExplorationMode(GameMode mode) =>
            mode == GameMode.Exploring || mode == GameMode.Menu ||
            mode == GameMode.Dialogue || mode == GameMode.Boot;

        /// <summary>
        /// Whether an ambient arrival — biome, time of day, weather — may start a world
        /// track right now. In free play the answer is yes for every non-battle mode,
        /// which is what keeps the town theme running under a villager's dialogue box.
        /// Under a cinematic hold the answer is no: the arrival has already updated the
        /// selection state above, and that memory is the whole of what it gets to do
        /// until the scene hands the pad back.
        /// </summary>
        private bool CanStartWorldMusic() => !_cinematicHold && IsExplorationMode(_mode);

        /// <summary>
        /// The scripted-scene gate, driven by EpisodeRunner (by reflection — the overworld
        /// deliberately does not reference the audio assembly, for the reasons its opening
        /// theme hook records) when an episode takes the pad and again when it gives it
        /// back.
        ///
        /// It exists because an episode is invisible to the mode stack: TakeControl only
        /// freezes input, so between dialogue beats the mode reads Exploring and during
        /// them it reads Dialogue — both of which this director rightly treats as "leave
        /// the music alone, and follow the world" in free play. During the opening that
        /// meant every dialogue close and every streamed-in band started the town theme
        /// over the professor's introduction.
        ///
        /// While held, nothing ambient may *start* a track: biome, time and weather
        /// arrivals are remembered but not played, and the pop back to Exploring after a
        /// dialogue beat starts nothing. Explicit requests — the opening cue, battle
        /// themes, stings, FadeOutAll — are untouched, because a script asking by name is
        /// the one voice a scripted scene should have. On release, if the game is back in
        /// an exploration mode, the remembered world track fades in on the normal outro
        /// blend; a track that was already right is a no-op by PlayTrack's own rules.
        /// </summary>
        [UnityEngine.Scripting.Preserve] // reached only by reflection; stripping must not eat it
        public void SetCinematicHold(bool held)
        {
            if (_cinematicHold == held) return;
            _cinematicHold = held;
            if (!held && IsExplorationMode(_mode))
                PlayTrack(SelectExplorationTrack(), outroFade);
        }

        private static bool IsHeavy(Weather w) =>
            w == Weather.Rain || w == Weather.Sandstorm || w == Weather.Hail;

        // ---- selection ---------------------------------------------------------------

        /// <summary>Told by <see cref="BattleAudioPresenter"/> when a battle starts.</summary>
        public void SetBattleKind(BattleKind kind)
        {
            _battleKind = kind;
            if (_mode == GameMode.Battle)
                PlayTrack(kind == BattleKind.Trainer
                    ? AudioIds.MusicBattleTrainer
                    : AudioIds.MusicBattleWild, battleFade);
        }

        public bool IsNight =>
            _timeOfDay == TimeOfDay.Night || (duskUsesNightTheme && _timeOfDay == TimeOfDay.Dusk);

        public string SelectExplorationTrack()
        {
            switch (_biome)
            {
                case AudioIds.BiomeTown:
                    return IsNight ? AudioIds.MusicTownNight : AudioIds.MusicTownDay;
                case AudioIds.BiomeCave:
                    return AudioIds.MusicCave;        // no day/night variant: caves have no sky
                case AudioIds.BiomeLakeside:
                    return AudioIds.MusicLakeside;
                default:
                    return IsNight ? AudioIds.MusicRouteNight : AudioIds.MusicRouteDay;
            }
        }

        // ---- playback ----------------------------------------------------------------

        public void PlayTrack(string trackName, float fadeSeconds)
        {
            if (string.IsNullOrEmpty(trackName)) return;

            // Already on its way there. Repeated BiomeEntered events land here.
            if (trackName == _pendingTrack) return;

            bool fading = _fade != null;

            // Settled on it already, with nothing in flight that needs correcting.
            if (!fading && trackName == _currentTrack && Active != null && Active.isPlaying) return;

            // Everything a blend needs is resolved before one is started, so the coroutine
            // has no early exit of its own to strand the fade state on.
            if (_director == null && !AudioDirector.TryResolve(out _director)) return;
            if (_deckA == null || _deckB == null) return;

            // Asked to come back to the track we are in the middle of leaving -- clipping a
            // biome trigger and stepping straight back out of it. The clip is still on its
            // deck and has not reached silence, so the blend is simply reversed: the two
            // decks swap roles and the loop keeps its position rather than restarting. This
            // used to match _currentTrack and return, leaving the fade to complete into the
            // track the player had just walked out of.
            if (fading && trackName == _currentTrack)
            {
                var falling = Idle;
                if (falling != null && falling.isPlaying && falling.clip != null)
                {
                    StopCoroutine(_fade);
                    BeginFade(falling, Active, trackName, fadeSeconds, restartIncoming: false);
                    return;
                }
            }

            var clip = _director.Catalog != null ? _director.Catalog.GetClip(trackName) : null;
            if (clip == null)
            {
                Debug.LogWarning($"[MusicDirector] Track '{trackName}' missing from catalogue.", this);
                return;
            }

            AudioSource outgoing, incoming;
            if (fading)
            {
                StopCoroutine(_fade);

                // Mid-blend both decks are sounding, and the new track has to displace one
                // of them. Keep the louder -- the one the player is actually hearing -- and
                // build the new track on the quieter, so a retarget costs at most the
                // fainter half of a blend. Taking the rising deck unconditionally, as this
                // did, cut whatever it had faded in: entering a battle and learning a
                // sentence later that it is a trainer battle chopped the wild theme off
                // mid-note.
                bool keepActive = LevelOf(Active) >= LevelOf(Idle);
                outgoing = keepActive ? Active : Idle;
                incoming = keepActive ? Idle : Active;
            }
            else
            {
                outgoing = Active;
                incoming = Idle;
            }

            incoming.clip = clip;
            BeginFade(incoming, outgoing, trackName, fadeSeconds, restartIncoming: true);
        }

        /// <summary>Audible level of a deck; a deck that is not playing counts as silent.</summary>
        private static float LevelOf(AudioSource deck) =>
            deck != null && deck.isPlaying ? deck.volume : 0f;

        private void BeginFade(AudioSource incoming, AudioSource outgoing, string trackName,
                               float fadeSeconds, bool restartIncoming)
        {
            // Whatever the outgoing deck holds is the track being left, and that is what a
            // request to come back mid-blend has to match against. After a retarget it may
            // be the track the previous blend was heading for rather than the settled one.
            if (ReferenceEquals(outgoing, Active) && _pendingTrack != null) _currentTrack = _pendingTrack;
            _pendingTrack = trackName;

            if (restartIncoming)
            {
                incoming.loop = true;
                incoming.volume = 0f;
                incoming.time = 0f;
                incoming.outputAudioMixerGroup = _director.GroupFor(AudioBus.Music);
                incoming.Play();
            }

            _aIsActive = ReferenceEquals(incoming, _deckA);
            _fade = StartCoroutine(Crossfade(incoming, outgoing, trackName,
                                             Mathf.Max(0.05f, fadeSeconds)));
        }

        private IEnumerator Crossfade(AudioSource incoming, AudioSource outgoing,
                                      string trackName, float seconds)
        {
            // Both ends ramp from where they already are rather than from a fixed 0 and 1,
            // which is what lets a reversed or retargeted blend continue from the level
            // that is currently in the room instead of jumping to meet the curve.
            float inStart = incoming.volume;
            float outStart = LevelOf(outgoing);
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                float x = Mathf.Clamp01(t / seconds);
                float shaped = Mathf.Clamp01(fadeCurve.Evaluate(x));

                float inGain, outGain;
                if (equalPower)
                {
                    // sin/cos law: the two gains sum to constant power, so the blend does
                    // not sag in the middle the way a pair of linear ramps does.
                    inGain = Mathf.Sin(shaped * Mathf.PI * 0.5f);
                    outGain = Mathf.Cos(shaped * Mathf.PI * 0.5f);
                }
                else
                {
                    inGain = shaped;
                    outGain = 1f - shaped;
                }

                float target = musicLevel * _duckGain * _weatherTrim;
                if (incoming == null) break;
                incoming.volume = Mathf.Lerp(inStart, target, inGain);
                if (outgoing != null && outgoing.isPlaying) outgoing.volume = outGain * outStart;
                yield return null;
            }

            if (incoming != null) incoming.volume = musicLevel * _duckGain * _weatherTrim;
            if (outgoing != null)
            {
                outgoing.Stop();
                outgoing.clip = null;
            }

            _currentTrack = trackName;
            _pendingTrack = null;
            _fade = null;
        }

        public void FadeOutAll(float seconds)
        {
            _pendingTrack = null;
            _currentTrack = null;
            // Cleared as well as stopped: a fade handle left behind is one ApplyLevels
            // treats as still running, and it stops maintaining deck volume for good.
            if (_fade != null) { StopCoroutine(_fade); _fade = null; }
            if (_deckA == null || _deckB == null) return;
            _fade = StartCoroutine(FadeOutRoutine(Mathf.Max(0.05f, seconds)));
        }

        private IEnumerator FadeOutRoutine(float seconds)
        {
            float aStart = _deckA.volume;
            float bStart = _deckB.volume;
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Cos(Mathf.Clamp01(t / seconds) * Mathf.PI * 0.5f);
                _deckA.volume = aStart * k;
                _deckB.volume = bStart * k;
                yield return null;
            }
            _deckA.Stop();
            _deckB.Stop();
            _deckA.clip = null;
            _deckB.clip = null;
            _fade = null;
        }

        /// <summary>
        /// The bridge out of exploration. The sting runs on its own deck at full level
        /// while the exploration loop fades beneath it over the sting's own length.
        /// </summary>
        public void PlayEncounterSting()
        {
            if (_director == null && !AudioDirector.TryResolve(out _director)) return;
            var clip = _director.Catalog != null
                ? _director.Catalog.GetClip(AudioIds.MusicEncounterSting)
                : null;
            if (clip == null) return;

            _stingDeck.clip = clip;
            _stingDeck.loop = false;
            _stingDeck.volume = musicLevel;
            _stingDeck.outputAudioMixerGroup = _director.GroupFor(AudioBus.Music);
            _stingDeck.Play();

            // fade the exploration theme under the sting rather than cutting it
            FadeOutAll(Mathf.Max(0.4f, clip.length * 0.55f));
        }

        /// <summary>Plays a one-shot music cue (fanfare, capture sting) without disturbing the loop.</summary>
        public void PlaySting(string trackName, float volume = 1f)
        {
            if (_director == null && !AudioDirector.TryResolve(out _director)) return;
            var clip = _director.Catalog != null ? _director.Catalog.GetClip(trackName) : null;
            if (clip == null) return;
            _stingDeck.clip = clip;
            _stingDeck.loop = false;
            _stingDeck.volume = musicLevel * volume;
            _stingDeck.outputAudioMixerGroup = _director.GroupFor(AudioBus.Music);
            _stingDeck.Play();
        }

        public void SetMusicLevel(float linear01) => musicLevel = Mathf.Clamp01(linear01);
    }
}
