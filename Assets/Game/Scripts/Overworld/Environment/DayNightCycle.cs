using System;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The world clock.
    ///
    /// It owns exactly one thing: a normalised 0-1 position through the day, published every
    /// frame on <see cref="GameEvents.TimeOfDayProgress"/> and quantised to
    /// <see cref="TimeOfDay"/> boundaries on <see cref="GameEvents.TimeOfDayChanged"/>.
    ///
    /// It does not touch a single light, sky material or post-process volume — the VFX/rendering
    /// worker's LightingDirector subscribes and owns all of that. Keeping the clock free of
    /// rendering is what lets both systems be built and tuned independently.
    ///
    /// 0 is midnight, 0.5 is noon, so the value maps directly to a sun angle of
    /// <c>(normalised * 360) - 90</c> degrees for whoever wants one.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-95)]
    public sealed class DayNightCycle : MonoBehaviour
    {
        [Header("Cycle")]
        [Tooltip("Real seconds for a full 24h cycle. 1200 = a twenty-minute day.")]
        [Min(1f)][SerializeField] private float _dayLengthSeconds = 1200f;
        [Tooltip("Where the clock starts. 0 midnight, 0.25 dawn, 0.5 noon, 0.75 dusk.")]
        [Range(0f, 1f)][SerializeField] private float _startTime = 0.34f;
        [Tooltip("Freezes the clock. Useful for authoring and for holding a mood during a cutscene.")]
        [SerializeField] private bool _paused;
        [Tooltip("Multiplier on the clock rate. Raise to fast-forward without changing day length.")]
        [Min(0f)][SerializeField] private float _timeScale = 1f;

        [Header("Boundaries (normalised)")]
        [Tooltip("Start of Dawn. Below this it is Night.")]
        [Range(0f, 1f)][SerializeField] private float _dawnStart = 0.22f;
        [Range(0f, 1f)][SerializeField] private float _dayStart = 0.30f;
        [Range(0f, 1f)][SerializeField] private float _duskStart = 0.72f;
        [Range(0f, 1f)][SerializeField] private float _nightStart = 0.82f;

        [Header("Battle behaviour")]
        [Tooltip("Holds the clock while the player is in battle, so an encounter cannot silently "
                 + "skip past sunset and return you to a different world.")]
        [SerializeField] private bool _pauseOutsideExploration = true;

        [Header("Editor scrubbing")]
        [Tooltip("Drag to scrub. Only applied while 'Apply Scrub' is ticked, so it does not fight "
                 + "the running clock in play mode.")]
        [Range(0f, 1f)][SerializeField] private float _scrubTime = 0.34f;
        [SerializeField] private bool _applyScrub;

        [Header("Output")]
        [Tooltip("Optional inspector hook mirroring GameEvents.TimeOfDayProgress.")]
        [SerializeField] private FloatEvent _progressChanged = new FloatEvent();
        [SerializeField] private StringEvent _phaseChanged = new StringEvent();

        private static DayNightCycle _instance;
        private float _normalised;
        private TimeOfDay _phase = TimeOfDay.Day;
        private bool _initialised;

        public static DayNightCycle Instance => _instance;

        /// <summary>Normalised 0-1 position through the day. 0 is midnight.</summary>
        public float Normalised => _normalised;

        /// <summary>Current quantised phase, the value encounter tables gate on.</summary>
        public TimeOfDay Phase => _phase;

        /// <summary>Clock hour 0-24, for clock UI and NPC schedules.</summary>
        public float Hour => _normalised * 24f;

        /// <summary>Sun elevation angle in degrees, offered as a convenience to the lighting worker.</summary>
        public float SunAngle => _normalised * 360f - 90f;

        /// <summary>True between dusk and dawn. Cave and night-biased tables key off this.</summary>
        public bool IsNight => _phase == TimeOfDay.Night;

        /// <summary>Raised alongside the Core event, for listeners that prefer a direct reference.</summary>
        public event Action<TimeOfDay, TimeOfDay> PhaseChanged;

        public bool Paused { get => _paused; set => _paused = value; }

        private void Awake()
        {
            _instance = this;
            _normalised = Mathf.Repeat(_startTime, 1f);
            _phase = PhaseFor(_normalised);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Start()
        {
            // Publish once at boot so nothing spends its first frames at a default lighting state.
            Publish(force: true);
            _initialised = true;
        }

        private void Update()
        {
            if (ShouldTick())
            {
                var delta = Time.deltaTime * _timeScale / Mathf.Max(1f, _dayLengthSeconds);
                _normalised = Mathf.Repeat(_normalised + delta, 1f);
            }

            Publish(force: !_initialised);
        }

        private bool ShouldTick()
        {
            if (_paused) return false;
            if (!_pauseOutsideExploration) return true;

            // The clock only runs during exploration. If no flow is registered yet we are almost
            // certainly still booting, so run — a stalled clock at boot looks like a bug.
            if (!ServiceHub.TryGet<IGameFlow>(out var flow)) return true;
            return flow.Mode == GameMode.Exploring || flow.Mode == GameMode.Dialogue;
        }

        private void Publish(bool force)
        {
            GameEvents.RaiseTimeOfDayProgress(_normalised);
            _progressChanged.Invoke(_normalised);

            var phase = PhaseFor(_normalised);
            if (phase == _phase && !force) return;

            var previous = _phase;
            _phase = phase;
            if (phase != previous || force)
            {
                GameEvents.RaiseTimeOfDayChanged(previous, phase);
                PhaseChanged?.Invoke(previous, phase);
                _phaseChanged.Invoke(phase.ToString());
            }
        }

        private TimeOfDay PhaseFor(float t)
        {
            if (t >= _nightStart || t < _dawnStart) return TimeOfDay.Night;
            if (t < _dayStart) return TimeOfDay.Dawn;
            if (t < _duskStart) return TimeOfDay.Day;
            return TimeOfDay.Dusk;
        }

        /// <summary>
        /// Jumps the clock. Raises the boundary event if the phase changed, so a scripted
        /// "sleep until morning" is indistinguishable from time passing naturally.
        /// </summary>
        public void SetNormalisedTime(float normalised)
        {
            _normalised = Mathf.Repeat(normalised, 1f);
            _scrubTime = _normalised;
            Publish(force: true);
        }

        /// <summary>Jumps to the start of a phase. Used by the healing machine's rest option.</summary>
        public void SetPhase(TimeOfDay phase)
        {
            switch (phase)
            {
                case TimeOfDay.Dawn: SetNormalisedTime(_dawnStart + 0.01f); break;
                case TimeOfDay.Day: SetNormalisedTime(_dayStart + 0.05f); break;
                case TimeOfDay.Dusk: SetNormalisedTime(_duskStart + 0.01f); break;
                default: SetNormalisedTime(_nightStart + 0.05f); break;
            }
        }

        [ContextMenu("Scrub to Dawn")] private void ScrubDawn() => SetPhase(TimeOfDay.Dawn);
        [ContextMenu("Scrub to Day")] private void ScrubDay() => SetPhase(TimeOfDay.Day);
        [ContextMenu("Scrub to Dusk")] private void ScrubDusk() => SetPhase(TimeOfDay.Dusk);
        [ContextMenu("Scrub to Night")] private void ScrubNight() => SetPhase(TimeOfDay.Night);

        private void OnValidate()
        {
            // Keep the boundaries monotonic, otherwise PhaseFor silently returns nonsense.
            _dayStart = Mathf.Max(_dayStart, _dawnStart + 0.01f);
            _duskStart = Mathf.Max(_duskStart, _dayStart + 0.01f);
            _nightStart = Mathf.Max(_nightStart, _duskStart + 0.01f);

            if (_applyScrub && Application.isPlaying) SetNormalisedTime(_scrubTime);
            else if (_applyScrub) _normalised = _scrubTime;
        }
    }
}
