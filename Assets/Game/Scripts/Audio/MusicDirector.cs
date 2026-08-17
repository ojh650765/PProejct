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
    /// fire on every trigger re-entry -- cannot restart or stutter the music.
    ///
    /// The encounter sting is the one deliberate exception to crossfading, and it is
    /// still not a cut: it plays on its own deck while the exploration theme fades under
    /// it, and it was composed to end on the pitch the battle theme opens on so the
    /// hand-off lands on a shared note.
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

        public string CurrentTrack => _currentTrack;

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
            if (_instance == this) _instance = null;
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
            if (IsExplorationMode(_mode)) PlayTrack(SelectExplorationTrack(), biomeFade);
        }

        private void OnTimeOfDayChanged(TimeOfDay from, TimeOfDay to)
        {
            _timeOfDay = to;
            // A slow blend: the day and night arrangements share their harmony, so a long
            // crossfade sits on consonant material for its whole duration.
            if (IsExplorationMode(_mode)) PlayTrack(SelectExplorationTrack(), timeOfDayFade);
        }

        private void OnWeatherChanged(Weather from, Weather to)
        {
            _weather = to;
            _weatherTrimTarget = IsHeavy(to) ? Mathf.Pow(10f, heavyWeatherTrimDb / 20f) : 1f;
            if (IsExplorationMode(_mode)) PlayTrack(SelectExplorationTrack(), biomeFade);
        }

        private void OnModeChanged(GameMode from, GameMode to)
        {
            _mode = to;
            switch (to)
            {
                case GameMode.Exploring:
                    PlayTrack(SelectExplorationTrack(), outroFade);
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
            if (trackName == _currentTrack && Active != null && Active.isPlaying) return;
            if (trackName == _pendingTrack) return;

            if (_director == null && !AudioDirector.TryResolve(out _director)) return;
            var clip = _director.Catalog != null ? _director.Catalog.GetClip(trackName) : null;
            if (clip == null)
            {
                Debug.LogWarning($"[MusicDirector] Track '{trackName}' missing from catalogue.", this);
                return;
            }

            _pendingTrack = trackName;
            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(Crossfade(clip, trackName, Mathf.Max(0.05f, fadeSeconds)));
        }

        private IEnumerator Crossfade(AudioClip clip, string trackName, float seconds)
        {
            var outgoing = Active;
            var incoming = Idle;

            // Checked rather than assumed. A coroutine that throws reports a stack with only
            // its own name in it — in a release build, not even that — so the one place this
            // can be null is the one place it has to be tested.
            if (incoming == null || _director == null) yield break;

            incoming.clip = clip;
            incoming.loop = true;
            incoming.volume = 0f;
            incoming.time = 0f;
            incoming.outputAudioMixerGroup = _director.GroupFor(AudioBus.Music);
            incoming.Play();

            float target = musicLevel * _duckGain * _weatherTrim;
            float outStart = outgoing != null && outgoing.isPlaying ? outgoing.volume : 0f;
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

                target = musicLevel * _duckGain * _weatherTrim;
                incoming.volume = inGain * target;
                if (outgoing != null && outgoing.isPlaying) outgoing.volume = outGain * outStart;
                yield return null;
            }

            incoming.volume = musicLevel * _duckGain * _weatherTrim;
            if (outgoing != null)
            {
                outgoing.Stop();
                outgoing.clip = null;
            }

            _aIsActive = !_aIsActive;
            _currentTrack = trackName;
            _pendingTrack = null;
            _fade = null;
        }

        public void FadeOutAll(float seconds)
        {
            _pendingTrack = null;
            _currentTrack = null;
            if (_fade != null) StopCoroutine(_fade);
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
