using System;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// Blends the ambience layers.
    ///
    /// The generator ships ten independent loops rather than one finished bed per
    /// location, which is what makes a genuine crossfade possible: entering a cave is
    /// birdsong and grass wind falling to zero while drips and rumble rise, over the
    /// same two seconds, with no clip ever starting or stopping audibly. Every gain in
    /// this component moves through <see cref="Mathf.MoveTowards"/> at a configured rate;
    /// nothing is ever assigned a new level directly.
    ///
    /// Point sources -- the waterfall being the one that matters -- are separate
    /// 3D-positioned emitters registered by the overworld, so their level and direction
    /// come from the listener's position rather than from a biome weight.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Ambience Director")]
    [DefaultExecutionOrder(-400)]
    public sealed class AmbienceDirector : MonoBehaviour
    {
        /// <summary>Per-layer weights, in the order of <see cref="AudioIds.AmbienceLayers"/>.</summary>
        [Serializable]
        public struct LayerMix
        {
            [Range(0f, 1f)] public float birdsong;
            [Range(0f, 1f)] public float windGrass;
            [Range(0f, 1f)] public float windHigh;
            [Range(0f, 1f)] public float waterLapping;
            [Range(0f, 1f)] public float waterfall;
            [Range(0f, 1f)] public float caveDrips;
            [Range(0f, 1f)] public float caveRumble;
            [Range(0f, 1f)] public float rain;
            [Range(0f, 1f)] public float nightInsects;
            [Range(0f, 1f)] public float townMurmur;

            public void Write(float[] target)
            {
                target[0] = birdsong;
                target[1] = windGrass;
                target[2] = windHigh;
                target[3] = waterLapping;
                target[4] = waterfall;
                target[5] = caveDrips;
                target[6] = caveRumble;
                target[7] = rain;
                target[8] = nightInsects;
                target[9] = townMurmur;
            }

            public static LayerMix Of(float birds, float wGrass, float wHigh, float water,
                                      float fall, float drips, float rumble, float rainAmt,
                                      float insects, float town) => new LayerMix
            {
                birdsong = birds, windGrass = wGrass, windHigh = wHigh, waterLapping = water,
                waterfall = fall, caveDrips = drips, caveRumble = rumble, rain = rainAmt,
                nightInsects = insects, townMurmur = town,
            };
        }

        [Serializable]
        public sealed class BiomeAmbience
        {
            [Tooltip("Matched case-insensitively against GameEvents.BiomeEntered.")]
            public string biomeId = AudioIds.BiomeRoute;
            public LayerMix day;
            public LayerMix night;
        }

        [Header("Profiles")]
        [SerializeField] private List<BiomeAmbience> profiles = new List<BiomeAmbience>();

        [Header("Blend")]
        [Tooltip("Seconds for a layer to travel the full 0-1 range. Higher is slower and softer.")]
        [SerializeField, Range(0.2f, 15f)] private float blendSeconds = 2.5f;

        [SerializeField, Range(0f, 1f)] private float ambienceLevel = 1f;

        [Tooltip("Stop a layer once it has been silent for a moment, and restart it from a " +
                 "random position when it is wanted again. Saves ten permanent decoders and " +
                 "stops the layers phase-locking to each other.")]
        [SerializeField] private bool stopSilentLayers = true;

        [Header("Thunder")]
        [SerializeField] private bool thunderInRain = true;
        [SerializeField] private Vector2 thunderIntervalSeconds = new Vector2(14f, 46f);

        [Header("3D emitters")]
        [SerializeField, Range(1, 8)] private int emitterCapacity = 4;

        private readonly float[] _target = new float[10];
        private readonly float[] _current = new float[10];
        private readonly float[] _silentFor = new float[10];
        private AudioSource[] _layerSources;

        private AudioSourcePool _emitterPool;
        private readonly List<Emitter> _emitters = new List<Emitter>();

        private string _biome = AudioIds.BiomeNone;
        private TimeOfDay _timeOfDay = TimeOfDay.Day;
        private Weather _weather = Weather.Clear;
        private AudioDirector _director;
        private float _nextThunder;
        private int _thunderIndex;

        /// <summary>
        /// True while a scripted episode holds the beds. See <see cref="SetCinematicHold"/>.
        /// </summary>
        private bool _cinematicHold;

        /// <summary>
        /// Master gain the hold drives: 1 in free play, 0 while held, moving through
        /// MoveTowards like every other level in this component. Volume is the only thing
        /// the hold touches — targets keep tracking biome, time and weather underneath it,
        /// so the release fades in whatever the world wants now, not what it wanted when
        /// the scene took the beds.
        /// </summary>
        private float _holdGain = 1f;

        private const float HoldFadeOutSeconds = 0.8f;
        private const float HoldFadeInSeconds = 1f;

        private sealed class Emitter
        {
            public AudioSource Source;
            public Transform Anchor;
            public float Volume;
            /// <summary>False while the clip's data is still on its way; see TryStartSource.</summary>
            public bool Started;
        }

        // ------------------------------------------------------------------------------

        private void Reset() => profiles = DefaultProfiles();

        private static AmbienceDirector _instance;

        private void Awake()
        {
            // First one wins, and any later arrival removes itself.
            //
            // A band streamed in additively brings a second GameHosts, and its Awake runs
            // during the load: it took the hub slot, allocated ten more layer sources, and
            // was then stripped along with the rest of the duplicate hosts, leaving the hub
            // holding a destroyed director and the ambience silent for the session.
            if (_instance != null && _instance != this)
            {
                // Disabled as well as destroyed: Destroy does not take effect until the end
                // of the frame, and Update on a director that never built its layer sources
                // is a null reference the moment it runs.
                enabled = false;
                Destroy(this);
                return;
            }
            _instance = this;

            if (profiles == null || profiles.Count == 0) profiles = DefaultProfiles();
            AudioDirector.TryResolve(out _director);

            var group = _director != null ? _director.GroupFor(AudioBus.Ambience) : null;
            var layerPool = new AudioSourcePool(transform, "AmbienceLayers",
                                                AudioIds.AmbienceLayers.Length, group);
            _layerSources = new AudioSource[AudioIds.AmbienceLayers.Length];
            for (int i = 0; i < _layerSources.Length; i++)
            {
                _layerSources[i] = layerPool.RentReserved();
                if (_layerSources[i] == null) continue;
                _layerSources[i].loop = true;
                _layerSources[i].volume = 0f;
                _layerSources[i].outputAudioMixerGroup = group;
                _layerSources[i].clip = ClipFor(AudioIds.AmbienceLayers[i]);
            }

            _emitterPool = new AudioSourcePool(transform, "AmbienceEmitters", emitterCapacity,
                                               group, spatial: true);
            ServiceHub.Register(this);
            Recalculate();
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;
            ServiceHub.Unregister(this);
        }

        private void OnEnable()
        {
            GameEvents.BiomeEntered += OnBiomeEntered;
            GameEvents.TimeOfDayChanged += OnTimeOfDayChanged;
            GameEvents.WeatherChanged += OnWeatherChanged;
        }

        private void OnDisable()
        {
            GameEvents.BiomeEntered -= OnBiomeEntered;
            GameEvents.TimeOfDayChanged -= OnTimeOfDayChanged;
            GameEvents.WeatherChanged -= OnWeatherChanged;
        }

        private AudioClip ClipFor(string name)
        {
            if (_director == null && !AudioDirector.TryResolve(out _director)) return null;
            return _director.Catalog != null ? _director.Catalog.GetClip(name) : null;
        }

        private void OnBiomeEntered(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return;
            var normalised = biomeId.Trim().ToLowerInvariant();
            if (normalised == _biome) return;
            _biome = normalised;
            Recalculate();
        }

        /// <summary>
        /// The player is on a menu, not in a place: every bed fades out.
        ///
        /// Counterpart to <c>MusicDirector.LeaveWorld</c> and closing the same bug from the
        /// other side. The beds used to default to the route's, so the login screen and the
        /// title came up over birdsong and wind in grass — the user heard it as 메인 메뉴에서
        /// 새소리. Fading rather than stopping because the levels are already a MoveTowards
        /// and blendSeconds is what a bed is supposed to take to leave.
        /// </summary>
        public void LeaveWorld()
        {
            if (_biome == AudioIds.BiomeNone) return;
            _biome = AudioIds.BiomeNone;
            Recalculate();
        }

        private void OnTimeOfDayChanged(TimeOfDay from, TimeOfDay to)
        {
            _timeOfDay = to;
            Recalculate();
        }

        private void OnWeatherChanged(Weather from, Weather to)
        {
            _weather = to;
            Recalculate();
            if (to == Weather.Rain) ScheduleThunder();
        }

        // ---- mixing ------------------------------------------------------------------

        /// <summary>Rebuilds the target weights from biome, time of day and weather.</summary>
        public void Recalculate()
        {
            var profile = FindProfile(_biome);

            // Nowhere has no weather either. Without this the rain layer still comes up over a
            // menu whenever the clock has it raining in a world nobody is looking at.
            if (ReferenceEquals(profile, Silent))
            {
                Array.Clear(_target, 0, _target.Length);
                return;
            }

            bool night = _timeOfDay == TimeOfDay.Night || _timeOfDay == TimeOfDay.Dusk;
            (night ? profile.night : profile.day).Write(_target);

            // Dawn and dusk are transitional: blend the two sets rather than picking one,
            // so first light brings the birds up while the crickets are still fading.
            if (_timeOfDay == TimeOfDay.Dawn || _timeOfDay == TimeOfDay.Dusk)
            {
                var other = new float[10];
                (_timeOfDay == TimeOfDay.Dawn ? profile.night : profile.day).Write(other);
                for (int i = 0; i < _target.Length; i++)
                    _target[i] = Mathf.Lerp(_target[i], other[i], 0.5f);
            }

            ApplyWeather(_target);
        }

        private void ApplyWeather(float[] w)
        {
            switch (_weather)
            {
                case Weather.Rain:
                    w[7] = Mathf.Max(w[7], 0.9f);       // rain
                    w[0] *= 0.15f;                       // birds shelter
                    w[8] *= 0.25f;                       // insects too
                    w[1] *= 0.7f;
                    break;
                case Weather.Hail:
                    w[7] = Mathf.Max(w[7], 0.5f);
                    w[2] = Mathf.Max(w[2], 0.7f);
                    w[0] *= 0.1f;
                    break;
                case Weather.Sandstorm:
                    w[2] = 1f;                           // high wind dominates
                    w[0] = 0f;
                    w[1] *= 0.4f;
                    break;
                case Weather.Fog:
                    w[0] *= 0.45f;
                    w[2] *= 0.5f;
                    break;
                case Weather.Sun:
                    w[0] = Mathf.Min(1f, w[0] * 1.15f);
                    break;
            }
        }

        /// <summary>
        /// Every layer at zero, for <see cref="AudioIds.BiomeNone"/>.
        ///
        /// Deliberately not reached through the fallback below: an unrecognised biome id is a
        /// typo somewhere and the route beds answer it better than silence does, whereas no
        /// biome at all means there is no world on screen and the beds would be wrong at any
        /// volume. One instance, never mutated -- Write only reads it.
        /// </summary>
        private static readonly BiomeAmbience Silent =
            new BiomeAmbience { biomeId = AudioIds.BiomeNone };

        private BiomeAmbience FindProfile(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return Silent;

            for (int i = 0; i < profiles.Count; i++)
            {
                if (profiles[i] != null &&
                    string.Equals(profiles[i].biomeId, biomeId, StringComparison.OrdinalIgnoreCase))
                    return profiles[i];
            }
            return profiles.Count > 0 ? profiles[0] : Silent;
        }

        private void Update()
        {
            float step = Time.deltaTime / Mathf.Max(0.05f, blendSeconds);

            _holdGain = Mathf.MoveTowards(_holdGain, _cinematicHold ? 0f : 1f,
                Time.deltaTime / (_cinematicHold ? HoldFadeOutSeconds : HoldFadeInSeconds));

            for (int i = 0; i < _current.Length; i++)
            {
                _current[i] = Mathf.MoveTowards(_current[i], _target[i], step);
                var src = _layerSources[i];
                if (src == null) continue;

                if (src.clip == null) src.clip = ClipFor(AudioIds.AmbienceLayers[i]);
                if (src.clip == null) continue;

                float gain = _current[i] * ambienceLevel * _holdGain;
                src.volume = gain;

                if (gain > 0.001f)
                {
                    _silentFor[i] = 0f;
                    // A held director never starts a bed: the target keeps tracking the
                    // world, but nothing new may sound until the scene hands the beds back.
                    // This runs every frame a wanted bed is silent, so a clip still
                    // decoding costs nothing: it starts the frame its data lands.
                    if (!src.isPlaying && !_cinematicHold) TryStartSource(src, src.clip);
                }
                else
                {
                    _silentFor[i] += Time.deltaTime;
                    if (stopSilentLayers && src.isPlaying && _silentFor[i] > 1.5f) src.Stop();
                }
            }

            UpdateEmitters();
            UpdateThunder();
        }

        /// <summary>
        /// The scripted-scene gate, driven by EpisodeRunner (by reflection — the overworld
        /// deliberately does not reference the audio assembly) when the opening takes the
        /// beds and again when it gives them back. The twin of
        /// <see cref="MusicDirector.SetCinematicHold"/>, and the release rides the same
        /// every-path-out guarantee that one does.
        ///
        /// While held, everything this director voices fades to silence — beds and 3D
        /// emitters both — and nothing new may start; biome, time and weather arrivals
        /// still update the targets, so release fades in what the world wants now. The
        /// whole effect is one gain, so calling this before Awake has built the sources
        /// only moves a float.
        /// </summary>
        [UnityEngine.Scripting.Preserve] // reached only by reflection; stripping must not eat it
        public void SetCinematicHold(bool held)
        {
            if (_cinematicHold == held) return;
            _cinematicHold = held;
        }

        /// <summary>
        /// Starts a looping source only once its clip's data has arrived. Play() itself on
        /// an unloaded clip is what logs "Trying to get length of sound which is not loaded
        /// yet" from inside Unity on the web build — every clip here ships with
        /// preloadAudioData off, so the data must be asked for and waited on. Returns true
        /// once the source is playing (or never will be), false while the caller should
        /// keep asking.
        /// </summary>
        private static bool TryStartSource(AudioSource src, AudioClip clip)
        {
            switch (clip.loadState)
            {
                case AudioDataLoadState.Loaded:
                    // random entry point: the beds are stationary textures, so this is
                    // inaudible, and it stops every layer restarting in lockstep. Safe to
                    // ask only now: length is unknowable before the data is in.
                    src.time = UnityEngine.Random.Range(0f, Mathf.Max(0.01f, clip.length - 0.1f));
                    src.Play();
                    return true;

                case AudioDataLoadState.Unloaded:
                    clip.LoadAudioData();
                    if (clip.loadState != AudioDataLoadState.Unloaded) return false;
                    // Still Unloaded after the kick: this clip's data is not
                    // LoadAudioData's to fetch (a loadType the engine buffers for
                    // itself). Start it cold, from 0, rather than never.
                    src.Play();
                    return true;

                case AudioDataLoadState.Failed:
                    // Nothing will ever arrive; stop asking and stay silent.
                    return true;

                default: // Loading
                    return false;
            }
        }

        // ---- 3D emitters -------------------------------------------------------------

        /// <summary>
        /// Attaches a looping ambience clip to a world position. Use for the waterfall and
        /// any other point source; the clip should be mono so Unity can spatialise it.
        /// Returns null when the pool is full or the clip is missing.
        /// </summary>
        public AudioSource RegisterEmitter(string clipName, Transform anchor, float volume = 1f,
                                           float minDistance = 4f, float maxDistance = 40f)
        {
            if (anchor == null) return null;
            var clip = ClipFor(clipName);
            if (clip == null)
            {
                Debug.LogWarning($"[AmbienceDirector] Emitter clip '{clipName}' missing.", this);
                return null;
            }
            var src = _emitterPool.RentReserved();
            if (src == null)
            {
                Debug.LogWarning("[AmbienceDirector] Emitter pool full; ignoring " + clipName, this);
                return null;
            }

            src.clip = clip;
            src.loop = true;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = minDistance;
            src.maxDistance = maxDistance;
            src.volume = volume * ambienceLevel * _holdGain;
            src.outputAudioMixerGroup = _director != null ? _director.GroupFor(AudioBus.Ambience) : null;
            src.transform.position = anchor.position;

            // A clip whose data has not arrived registers pending rather than playing —
            // see TryStartSource — and UpdateEmitters starts it the frame the data lands.
            _emitters.Add(new Emitter
            {
                Source = src, Anchor = anchor, Volume = volume,
                Started = TryStartSource(src, clip),
            });
            return src;
        }

        public void UnregisterEmitter(AudioSource source)
        {
            for (int i = _emitters.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_emitters[i].Source, source)) continue;
                _emitterPool.Release(source);
                _emitters.RemoveAt(i);
                return;
            }
        }

        private void UpdateEmitters()
        {
            for (int i = _emitters.Count - 1; i >= 0; i--)
            {
                var e = _emitters[i];
                if (e.Anchor == null || e.Source == null)
                {
                    if (e.Source != null) _emitterPool.Release(e.Source);
                    _emitters.RemoveAt(i);
                    continue;
                }
                e.Source.transform.position = e.Anchor.position;
                e.Source.volume = e.Volume * ambienceLevel * _holdGain;
                // Deferred start for a registration that arrived before its data did.
                if (!e.Started) e.Started = TryStartSource(e.Source, e.Source.clip);
            }
        }

        // ---- thunder -----------------------------------------------------------------

        private void ScheduleThunder() =>
            _nextThunder = Time.time + UnityEngine.Random.Range(thunderIntervalSeconds.x,
                                                               thunderIntervalSeconds.y);

        private void UpdateThunder()
        {
            if (!thunderInRain || _weather != Weather.Rain) return;
            // Thunder is a one-shot on the SFX bus, so the hold gain cannot silence it;
            // gate it here or a clap lands in the middle of a held scene.
            if (_cinematicHold) return;
            if (Time.time < _nextThunder) return;
            if (_director != null)
            {
                _director.PlaySfx(AudioIds.Thunder[_thunderIndex % AudioIds.Thunder.Length],
                                  UnityEngine.Random.Range(0.55f, 0.9f));
                _thunderIndex++;
            }
            ScheduleThunder();
        }

        public void SetAmbienceLevel(float linear01) => ambienceLevel = Mathf.Clamp01(linear01);

        // ---- defaults ----------------------------------------------------------------

        /// <summary>
        /// Sensible starting weights so the component works the moment it is dropped in a
        /// scene. Ordering of arguments matches LayerMix.Of:
        /// birds, windGrass, windHigh, waterLapping, waterfall, drips, rumble, rain,
        /// insects, townMurmur.
        /// </summary>
        public static List<BiomeAmbience> DefaultProfiles() => new List<BiomeAmbience>
        {
            new BiomeAmbience
            {
                biomeId = AudioIds.BiomeRoute,
                day   = LayerMix.Of(0.75f, 0.80f, 0.20f, 0f,    0f,    0f,    0f,    0f, 0f,    0f),
                night = LayerMix.Of(0.05f, 0.45f, 0.55f, 0f,    0f,    0f,    0f,    0f, 0.80f, 0f),
            },
            new BiomeAmbience
            {
                biomeId = AudioIds.BiomeTown,
                day   = LayerMix.Of(0.35f, 0.30f, 0.10f, 0f,    0f,    0f,    0f,    0f, 0f,    0.85f),
                night = LayerMix.Of(0f,    0.25f, 0.25f, 0f,    0f,    0f,    0f,    0f, 0.45f, 0.30f),
            },
            new BiomeAmbience
            {
                biomeId = AudioIds.BiomeCave,
                // No sky, so day and night are identical -- and the absence of any wind or
                // insect layer is what makes a cave read as enclosed.
                day   = LayerMix.Of(0f, 0f, 0f, 0f, 0f, 0.70f, 0.85f, 0f, 0f, 0f),
                night = LayerMix.Of(0f, 0f, 0f, 0f, 0f, 0.70f, 0.85f, 0f, 0f, 0f),
            },
            new BiomeAmbience
            {
                biomeId = AudioIds.BiomeLakeside,
                day   = LayerMix.Of(0.55f, 0.55f, 0.15f, 0.85f, 0.20f, 0f, 0f, 0f, 0f,    0f),
                night = LayerMix.Of(0f,    0.30f, 0.35f, 0.80f, 0.20f, 0f, 0f, 0f, 0.65f, 0f),
            },
        };
    }
}
