using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>What produced an encounter, for telegraph and result handling.</summary>
    public enum EncounterSourceKind
    {
        TallGrass = 0,
        Water = 1,
        Roamer = 2,
        Trainer = 3,
        Scripted = 4,
    }

    /// <summary>
    /// Implemented by anything that can build encounter pressure and then play the approach beat:
    /// a grass patch rustles, a water surface ripples. Keeping this an interface means the
    /// director never needs to know which kind of thing fired.
    /// </summary>
    public interface IEncounterSource
    {
        EncounterSourceKind SourceKind { get; }
        Vector3 TelegraphPosition { get; }

        /// <summary>
        /// Play the tell — the rustle. Returns the seconds the director should wait before the
        /// battle transition begins, so a source with a longer animation gets its full beat.
        /// </summary>
        float PlayApproachTelegraph();
    }

    /// <summary>
    /// Owns every wild encounter in the overworld: the pressure model, the roll, the approach
    /// beat, the <see cref="EncounterRequest"/>, and applying the <see cref="EncounterResult"/>.
    ///
    /// The pressure model is the interesting part. A per-step coin flip produces two bad
    /// outcomes at once — long dry spells and back-to-back encounters — because it has no memory.
    /// Instead this integrates distance travelled into a pressure value and evaluates a hazard
    /// rate at fixed distance intervals, with the rate escalating the further past the expected
    /// distance you get. That bounds the dry spell without making a short crossing dangerous.
    ///
    /// Every roll is <see cref="DeterministicRandom.Hash01"/> of the world seed against a check
    /// counter, not a call into a shared generator. Two consequences: the same walk from the same
    /// save produces the same encounters, and no unrelated system can shift the sequence by
    /// consuming a draw.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]
    public sealed class EncounterDirector : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private PlayerLocomotion _player;
        [SerializeField] private ZoneDirector _zoneDirector;
        [SerializeField] private DayNightCycle _clock;
        [SerializeField] private WeatherDirector _weather;
        [SerializeField] private PlayerAnimationDriver _playerAnimation;

        [Header("Pressure model")]
        [Tooltip("Metres of travel between hazard evaluations. Smaller is smoother, not more frequent.")]
        [Min(0.05f)][SerializeField] private float _checkIntervalMetres = 0.6f;
        [Tooltip("Hazard multiplier at zero pressure. Below 1 makes the first few steps safe-ish.")]
        [SerializeField] private float _minEscalation = 0.55f;
        [Tooltip("Hazard multiplier once pressure is far past the table's mean distance. Above 1 "
                 + "bounds the dry spell.")]
        [SerializeField] private float _maxEscalation = 2.4f;
        [Tooltip("Pressure decays this many metres per second while you are out of the source. "
                 + "Stops the world remembering a walk you took five minutes ago.")]
        [SerializeField] private float _pressureDecayPerSecond = 3f;

        [Header("Approach beat")]
        [Tooltip("Extra seconds held after the source's own telegraph, before the transition starts. "
                 + "This is the beat where the grass shakes and the player reacts.")]
        [SerializeField] private float _extraTelegraphSeconds = 0.35f;
        [Tooltip("Seconds of immunity after returning from a battle, so you are not immediately "
                 + "jumped again while standing in the same patch.")]
        [SerializeField] private float _postEncounterImmunity = 2.5f;

        [Header("Determinism")]
        [Tooltip("World seed. Persisted in the save so a reload reproduces the same encounters.")]
        [SerializeField] private int _worldSeed = 20260815;

        [Header("Output")]
        [SerializeField] private Vector3Event _encounterTelegraphed = new Vector3Event();

        private static EncounterDirector _instance;

        private float _pressure;
        private float _sinceLastCheck;
        private int _checkCounter;
        private float _immunityTimer;
        private bool _sequenceRunning;
        private IEncounterSource _activeSource;
        private RoamingCreature _pendingRoamer;
        private TrainerController _pendingTrainer;
        private readonly List<int> _seenBuffer = new List<int>();

        public static EncounterDirector Instance => _instance;

        /// <summary>Accumulated pressure in metres. Exposed for debug overlays.</summary>
        public float Pressure => _pressure;

        /// <summary>True from the telegraph until the result has been applied.</summary>
        public bool SequenceRunning => _sequenceRunning;

        /// <summary>World seed. The save system reads and restores this.</summary>
        public int WorldSeed { get => _worldSeed; set => _worldSeed = value; }

        /// <summary>Encounter check index. Saved with the seed so a reload resumes the same stream.</summary>
        public int CheckCounter { get => _checkCounter; set => _checkCounter = value; }

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Start()
        {
            if (_zoneDirector == null) _zoneDirector = ZoneDirector.Instance;
            if (_clock == null) _clock = DayNightCycle.Instance;
            if (_weather == null) _weather = WeatherDirector.Instance;
        }

        private void Update()
        {
            if (_immunityTimer > 0f) _immunityTimer -= Time.deltaTime;
            // Bleed pressure off when the player is not in a source, so pressure reflects the walk
            // you are on rather than every walk you have ever taken.
            if (_activeSource == null && _pressure > 0f)
                _pressure = Mathf.Max(0f, _pressure - _pressureDecayPerSecond * Time.deltaTime);
            _activeSource = null;
        }

        /// <summary>
        /// Called every frame by whichever source the player is standing in. Feeding distance
        /// rather than time is what makes standing still in grass safe and sprinting through it
        /// dangerous, which is the behaviour players expect.
        /// </summary>
        public void AccumulatePressure(IEncounterSource source, float metres, float sourceMultiplier = 1f)
        {
            _activeSource = source;
            if (!CanEncounter()) return;
            if (metres <= 0f) return;

            var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;
            var zoneMultiplier = zone != null ? zone.EncounterRateMultiplier : 1f;
            if (zoneMultiplier <= 0f) return;

            var traversal = source.SourceKind == EncounterSourceKind.Water ? TraversalState.Water : TraversalState.TallGrass;
            var table = zone != null ? zone.TableFor(traversal) : EncounterTable.BuiltInFor(ZoneKind.Route, traversal);
            if (table == null) return;

            var weighted = metres * zoneMultiplier * Mathf.Max(0f, sourceMultiplier);
            _pressure += weighted;
            _sinceLastCheck += weighted;

            while (_sinceLastCheck >= _checkIntervalMetres)
            {
                _sinceLastCheck -= _checkIntervalMetres;
                if (_pressure < table.GraceMetres) continue;
                if (EvaluateHazard(table))
                {
                    BeginWildEncounter(source, table);
                    return;
                }
            }
        }

        /// <summary>
        /// One hazard evaluation over a fixed distance slice. The exponential form is what makes
        /// the result independent of how the distance was chunked up between frames.
        /// </summary>
        private bool EvaluateHazard(EncounterTable table)
        {
            var mean = Mathf.Max(1f, table.MetresPerEncounter);
            var overshoot = Mathf.Clamp01((_pressure - table.GraceMetres) / (mean * 2f));
            var escalation = Mathf.Lerp(_minEscalation, _maxEscalation, overshoot);
            var probability = 1f - Mathf.Exp(-(_checkIntervalMetres / mean) * escalation);

            _checkCounter++;
            var roll = DeterministicRandom.Hash01(_worldSeed ^ DeterministicRandom.HashString(table.TableId), _checkCounter);
            return roll < probability;
        }

        private bool CanEncounter()
        {
            if (_sequenceRunning) return false;
            if (_immunityTimer > 0f) return false;
            if (_zoneDirector != null && _zoneDirector.IsTransitioning) return false;
            if (ServiceHub.TryGet<IGameFlow>(out var flow) && flow.Mode != GameMode.Exploring) return false;
            return true;
        }

        private void BeginWildEncounter(IEncounterSource source, EncounterTable table)
        {
            var time = _clock != null ? _clock.Phase : TimeOfDay.Day;
            var weather = _weather != null ? _weather.EffectiveWeather : Weather.Clear;

            // Derive the species roll from the same seed and counter as the trigger roll, so the
            // whole encounter — whether and what — is one reproducible decision.
            var rng = new DeterministicRandom(_worldSeed ^ (_checkCounter * 8191));
            if (!table.TryRoll(ref rng, time, weather, out var speciesId, out var level))
            {
                // Nothing in the table matches the current conditions. Reset and carry on rather
                // than firing an encounter with no species — an empty battle is worse than none.
                _pressure = 0f;
                return;
            }

            var request = BuildRequest(speciesId, level, ref rng);
            StartCoroutine(RunEncounterSequence(source, request));
        }

        private EncounterRequest BuildRequest(int speciesId, int level, ref DeterministicRandom rng)
        {
            var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;
            var playerTransform = _player != null ? _player.transform : transform;

            return new EncounterRequest
            {
                Kind = BattleKind.Wild,
                WildSpeciesId = speciesId,
                WildLevel = level,
                Weather = _weather != null ? _weather.EffectiveWeather : Weather.Clear,
                TimeOfDay = _clock != null ? _clock.Phase : TimeOfDay.Day,
                BiomeId = zone != null ? zone.BiomeId : "unknown",
                WorldPosition = playerTransform.position,
                PlayerRotation = playerTransform.rotation,
                // A fresh sub-seed so the battle's own RNG is deterministic but independent of the
                // overworld stream — replaying a battle must not consume overworld rolls.
                Seed = rng.NextSeed(),
            };
        }

        private IEnumerator RunEncounterSequence(IEncounterSource source, EncounterRequest request)
        {
            _sequenceRunning = true;
            _pressure = 0f;
            _sinceLastCheck = 0f;

            // 1. The tell. The grass shakes and the player reacts before anything cuts.
            var telegraphSeconds = source != null ? source.PlayApproachTelegraph() : 0f;
            var position = source != null ? source.TelegraphPosition : request.WorldPosition;
            OverworldEvents.RaiseEncounterTelegraphed(position);
            _encounterTelegraphed.Invoke(position);
            if (_playerAnimation != null) _playerAnimation.PlayEmote();

            // 2. Freeze while the beat plays, so the player is not walking during their own
            //    surprise. The camera is untouched — cinematics owns every camera move.
            SetPlayerFrozen(true);
            var wait = Mathf.Max(0f, telegraphSeconds) + Mathf.Max(0f, _extraTelegraphSeconds);
            if (wait > 0f) yield return new WaitForSeconds(wait);

            // 3. Hand off. GameFlow owns the transition; we do not touch the camera or fade.
            Commit(request);
        }

        private void Commit(EncounterRequest request)
        {
            OverworldEvents.RaiseEncounterCommitted(request);

            if (!ServiceHub.TryGet<IGameFlow>(out var flow))
            {
                Debug.LogWarning("[EncounterDirector] No IGameFlow registered; encounter dropped.", this);
                Abort();
                return;
            }

            flow.RequestEncounter(request, ApplyResult);
        }

        private void Abort()
        {
            _sequenceRunning = false;
            _pendingRoamer = null;
            _pendingTrainer = null;
            SetPlayerFrozen(false);
            _immunityTimer = _postEncounterImmunity;
        }

        /// <summary>
        /// Applies the battle's outcome to the player profile and cleans up whatever spawned it.
        /// Called back by <see cref="IGameFlow"/> once the outro has finished, so by the time this
        /// runs the player is already standing where they were.
        /// </summary>
        private void ApplyResult(EncounterResult result)
        {
            if (result == null)
            {
                Abort();
                return;
            }

            ServiceHub.TryGet<IPlayerProfile>(out var profile);

            _seenBuffer.Clear();
            if (result.SpeciesSeen != null) _seenBuffer.AddRange(result.SpeciesSeen);
            for (var i = 0; i < _seenBuffer.Count; i++)
            {
                profile?.MarkSeen(_seenBuffer[i]);
                GameEvents.RaiseSpeciesSeen(_seenBuffer[i]);
            }

            if (result.CapturedCreature != null)
            {
                profile?.MarkCaught(result.CapturedCreature.SpeciesId);
                if (profile != null && !profile.TryAddToParty(result.CapturedCreature))
                {
                    // Party is full. A PlayerProfile sends it to storage; a foreign implementation
                    // may not, so say so rather than silently losing a capture.
                    if (profile is PlayerProfile concrete) concrete.SendToStorage(result.CapturedCreature);
                    else Debug.LogWarning("[EncounterDirector] Party full and storage unavailable; capture lost.", this);
                }
                GameEvents.RaiseCreatureCaught(result.CapturedCreature);
            }

            if (result.MoneyDelta != 0 && profile is PlayerProfile wallet) wallet.AddMoney(result.MoneyDelta);

            // The creature you fought is gone whether you caught it or beat it.
            if (_pendingRoamer != null)
            {
                var removed = result.Outcome == BattleOutcome.Captured || result.Outcome == BattleOutcome.PlayerVictory;
                if (removed) _pendingRoamer.Consume();
                else _pendingRoamer.Retreat();
                _pendingRoamer = null;
            }

            if (_pendingTrainer != null)
            {
                _pendingTrainer.NotifyBattleResolved(result.Outcome);
                _pendingTrainer = null;
            }

            OverworldEvents.RaiseEncounterResolved(result);
            OverworldEvents.RaisePartyChanged();

            _sequenceRunning = false;
            _immunityTimer = _postEncounterImmunity;
            SetPlayerFrozen(false);
        }

        private void SetPlayerFrozen(bool frozen)
        {
            if (_player != null) _player.SetMotionFrozen(frozen);
        }

        // ---- Explicit triggers ----------------------------------------------------------

        /// <summary>
        /// Starts a battle against a specific roaming creature. The species and level come from
        /// the creature that is standing in front of you, not from a table roll — that is the whole
        /// point of visible encounters.
        /// </summary>
        public void TriggerRoamerEncounter(RoamingCreature roamer)
        {
            if (roamer == null || !CanEncounter()) return;

            _pendingRoamer = roamer;
            var rng = new DeterministicRandom(_worldSeed ^ roamer.GetInstanceID());
            var request = BuildRequest(roamer.SpeciesId, roamer.Level, ref rng);
            request.WorldPosition = roamer.transform.position;

            StartCoroutine(RunEncounterSequence(roamer, request));
        }

        /// <summary>Starts a trainer battle. The party is resolved battle-side from the trainer id.</summary>
        public void TriggerTrainerEncounter(TrainerController trainer)
        {
            if (trainer == null || _sequenceRunning) return;

            _pendingTrainer = trainer;
            var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;
            var playerTransform = _player != null ? _player.transform : transform;
            var rng = new DeterministicRandom(_worldSeed ^ DeterministicRandom.HashString(trainer.TrainerId));

            var request = new EncounterRequest
            {
                Kind = BattleKind.Trainer,
                TrainerId = trainer.TrainerId,
                Weather = _weather != null ? _weather.EffectiveWeather : Weather.Clear,
                TimeOfDay = _clock != null ? _clock.Phase : TimeOfDay.Day,
                BiomeId = zone != null ? zone.BiomeId : "unknown",
                WorldPosition = playerTransform.position,
                PlayerRotation = playerTransform.rotation,
                Seed = rng.NextSeed(),
            };

            // No telegraph beat here: the trainer's approach walk and dialogue already served as
            // one, so a second pause would read as a hitch.
            _sequenceRunning = true;
            SetPlayerFrozen(true);
            Commit(request);
        }

        /// <summary>Clears accumulated pressure. Called on load and after a warp.</summary>
        public void ResetPressure()
        {
            _pressure = 0f;
            _sinceLastCheck = 0f;
            _immunityTimer = _postEncounterImmunity;
        }
    }
}
