using System;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Chooses the weather and publishes it. Like the clock, it raises events and owns no
    /// rendering — no particles, no fog, no skybox. The VFX worker listens on
    /// <see cref="GameEvents.WeatherChanged"/> and <see cref="OverworldEvents.WeatherBlend"/>.
    ///
    /// Selection is a weighted draw biased two ways:
    ///
    /// - By zone. A lakeside is foggy and rainy; a route is mostly clear. See
    ///   <see cref="WorldZone.EffectiveWeatherBias"/>.
    /// - By transition affinity. Rain following Clear is natural, Sandstorm following Hail is not.
    ///   A flat weighted draw produces weather that flickers between unrelated states, which reads
    ///   as broken even when each individual choice was plausible.
    ///
    /// A cave suppresses weather entirely: the reported weather is Clear while you are inside, and
    /// the real weather is restored when you leave.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-94)]
    public sealed class WeatherDirector : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private ZoneDirector _zoneDirector;
        [SerializeField] private DayNightCycle _clock;

        [Header("Timing")]
        [Tooltip("Shortest a weather state is held, in real seconds.")]
        [Min(5f)][SerializeField] private float _minHoldSeconds = 90f;
        [Min(5f)][SerializeField] private float _maxHoldSeconds = 260f;
        [Tooltip("Seconds the cross-fade takes. VFX ramps its particles over this window.")]
        [SerializeField] private float _transitionSeconds = 6f;

        [Header("Behaviour")]
        [SerializeField] private Weather _startingWeather = Weather.Clear;
        [Tooltip("Turn off to pin the weather for authoring or for a scripted beat.")]
        [SerializeField] private bool _automatic = true;
        [Tooltip("Chance a roll simply extends the current weather rather than changing it. "
                 + "Without this, weather changes on a fixed metronome and feels mechanical.")]
        [Range(0f, 0.9f)][SerializeField] private float _persistenceChance = 0.35f;
        [Tooltip("World seed. Same seed plus same zone sequence reproduces the same weather.")]
        [SerializeField] private int _seed = 20260815;

        [Header("Output")]
        [SerializeField] private StringEvent _weatherChanged = new StringEvent();
        [SerializeField] private FloatEvent _transitionProgress = new FloatEvent();

        private static WeatherDirector _instance;
        private DeterministicRandom _rng;
        private Weather _current = Weather.Clear;
        private Weather _pending = Weather.Clear;
        private float _holdTimer;
        private float _holdDuration;
        private float _transitionTimer;
        private bool _transitioning;
        private bool _suppressed;
        // Reused so the weather roll allocates nothing; it runs rarely, but this is the kind of
        // per-tick garbage that is invisible until a profiler run late in the project.
        private readonly float[] _weightScratch = new float[6];

        public static WeatherDirector Instance => _instance;

        /// <summary>The weather the world is in, ignoring zone suppression.</summary>
        public Weather GlobalWeather => _current;

        /// <summary>
        /// Weather as the player experiences it right now — Clear inside a cave. This is the value
        /// encounter tables gate on and the value written into <see cref="EncounterRequest"/>.
        /// </summary>
        public Weather EffectiveWeather
        {
            get
            {
                var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;
                return zone != null ? zone.FilterWeather(_current) : _current;
            }
        }

        /// <summary>0-1 through the current cross-fade, or 1 when settled.</summary>
        public float TransitionProgress =>
            !_transitioning ? 1f : Mathf.Clamp01(_transitionTimer / Mathf.Max(0.01f, _transitionSeconds));

        public event Action<Weather, Weather> WeatherChanged;

        private void Awake()
        {
            _instance = this;
            _rng = new DeterministicRandom(_seed);
            _current = _startingWeather;
            _pending = _startingWeather;
            _holdDuration = _rng.Range(_minHoldSeconds, _maxHoldSeconds);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Start()
        {
            if (_zoneDirector == null) _zoneDirector = ZoneDirector.Instance;
            if (_clock == null) _clock = DayNightCycle.Instance;
            if (_zoneDirector != null) _zoneDirector.ZoneChanged += OnZoneChanged;

            // Announce the opening state so nothing sits at an unknown weather on frame one.
            GameEvents.RaiseWeatherChanged(Weather.Clear, EffectiveWeather);
            _weatherChanged.Invoke(EffectiveWeather.ToString());
            OverworldEvents.RaiseWeatherBlend(Weather.Clear, EffectiveWeather, 1f);
        }

        private void OnDisable()
        {
            if (_zoneDirector != null) _zoneDirector.ZoneChanged -= OnZoneChanged;
        }

        private void Update()
        {
            var dt = Time.deltaTime;

            if (_transitioning)
            {
                _transitionTimer += dt;
                var t = TransitionProgress;
                OverworldEvents.RaiseWeatherBlend(_current, _pending, t);
                _transitionProgress.Invoke(t);
                if (t >= 1f) CompleteTransition();
                return;
            }

            if (!_automatic) return;

            _holdTimer += dt;
            if (_holdTimer < _holdDuration) return;
            _holdTimer = 0f;
            _holdDuration = _rng.Range(_minHoldSeconds, _maxHoldSeconds);
            RollNextWeather();
        }

        private void OnZoneChanged(WorldZone previous, WorldZone next)
        {
            var suppressed = next != null && next.SuppressWeather;
            if (suppressed == _suppressed) return;
            _suppressed = suppressed;

            // Entering or leaving a cave changes what the player experiences without changing the
            // sky. Announce the effective value so the VFX worker can stop the rain indoors.
            var from = suppressed ? _current : Weather.Clear;
            var to = EffectiveWeather;
            GameEvents.RaiseWeatherChanged(from, to);
            _weatherChanged.Invoke(to.ToString());
            OverworldEvents.RaiseWeatherBlend(from, to, 1f);
        }

        private void RollNextWeather()
        {
            if (_rng.Chance(_persistenceChance)) return;

            var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;
            var bias = zone != null ? zone.EffectiveWeatherBias : WeatherBias.For(ZoneKind.Route);

            var total = 0f;
            var weights = _weightScratch;
            for (var i = 0; i < 6; i++)
            {
                var candidate = (Weather)i;
                var weight = bias.WeightOf(candidate) * Affinity(_current, candidate);
                weights[i] = weight;
                total += weight;
            }

            if (total <= 0f) return;

            var roll = _rng.NextFloat() * total;
            var chosen = _current;
            for (var i = 0; i < 6; i++)
            {
                roll -= weights[i];
                if (roll > 0f) continue;
                chosen = (Weather)i;
                break;
            }

            if (chosen != _current) BeginTransition(chosen);
        }

        /// <summary>
        /// How natural a transition reads. 0 forbids it outright, so hail never follows a sandstorm.
        /// </summary>
        private static float Affinity(Weather from, Weather to)
        {
            if (from == to) return 0f; // handled by the persistence roll, not by the weighted draw

            switch (from)
            {
                case Weather.Clear:
                    return to == Weather.Sandstorm || to == Weather.Hail ? 0.3f : 1f;
                case Weather.Rain:
                    // Rain clears, or thickens into fog. It does not become a sunny day instantly.
                    return to == Weather.Clear ? 1.4f : to == Weather.Fog ? 1f : to == Weather.Sun ? 0.15f : 0.2f;
                case Weather.Sun:
                    return to == Weather.Clear ? 1.6f : to == Weather.Rain ? 0.4f : 0.2f;
                case Weather.Fog:
                    return to == Weather.Clear ? 1.5f : to == Weather.Rain ? 0.9f : 0.15f;
                case Weather.Sandstorm:
                case Weather.Hail:
                    return to == Weather.Clear ? 2f : 0.1f;
                default:
                    return 1f;
            }
        }

        private void BeginTransition(Weather next)
        {
            _pending = next;
            _transitioning = true;
            _transitionTimer = 0f;
            OverworldEvents.RaiseWeatherBlend(_current, _pending, 0f);
        }

        private void CompleteTransition()
        {
            var previous = _current;
            _current = _pending;
            _transitioning = false;
            _transitionTimer = 0f;

            var effective = EffectiveWeather;
            GameEvents.RaiseWeatherChanged(previous, effective);
            _weatherChanged.Invoke(effective.ToString());
            WeatherChanged?.Invoke(previous, effective);
        }

        /// <summary>Forces a weather state. Instant skips the cross-fade — use only for save restore.</summary>
        public void SetWeather(Weather weather, bool instant = false)
        {
            if (weather == _current && !_transitioning) return;
            if (instant)
            {
                _pending = weather;
                CompleteTransition();
            }
            else
            {
                BeginTransition(weather);
            }
            _holdTimer = 0f;
        }
    }
}
