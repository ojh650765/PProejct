using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>Maps a species to how it behaves in the world. Authored once per slice.</summary>
    [Serializable]
    public struct TemperamentAssignment
    {
        public int SpeciesId;
        public Temperament Temperament;
    }

    /// <summary>
    /// Keeps each zone stocked with visible wild creatures.
    ///
    /// The population is driven by the zone's own encounter table, so a creature you can see
    /// wandering the route is a creature the grass could have produced — the two systems never
    /// disagree about what lives where. Time of day and weather gate both identically, which is
    /// why Zubat appear in the cave after dark without any special-casing.
    ///
    /// Spawns are pushed outside a ring around the player so nothing ever pops into existence in
    /// view, and despawns happen only well beyond that ring so nothing vanishes while watched.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoamingCreatureSpawner : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private ZoneDirector _zoneDirector;
        [SerializeField] private DayNightCycle _clock;
        [SerializeField] private WeatherDirector _weather;
        [SerializeField] private Transform _player;
        [Tooltip("Prefab carrying RoamingCreature + NavMeshAgent. The creature's art is resolved "
                 + "at runtime through ICreatureArtRegistry, so one prefab serves every species.")]
        [SerializeField] private RoamingCreature _roamerPrefab;
        [Tooltip("Parent for spawned creatures. Keeps the hierarchy readable.")]
        [SerializeField] private Transform _spawnParent;

        [Header("Placement")]
        [Tooltip("Creatures never spawn closer than this to the player.")]
        [SerializeField] private float _minSpawnDistance = 22f;
        [Tooltip("Creatures never spawn further than this from the player.")]
        [SerializeField] private float _maxSpawnDistance = 60f;
        [Tooltip("Beyond this from the player a creature is recycled. Must exceed max spawn distance.")]
        [SerializeField] private float _despawnDistance = 90f;
        [Tooltip("How far off a sampled point NavMesh.SamplePosition may snap.")]
        [SerializeField] private float _navSampleRadius = 8f;
        [Tooltip("Attempts per spawn before giving up for this tick. Low, because we retry next tick.")]
        [SerializeField] private int _placementAttempts = 8;

        [Header("Rate")]
        [Tooltip("Seconds between population evaluations.")]
        [SerializeField] private float _evaluationInterval = 2f;
        [Tooltip("Most creatures spawned in a single evaluation, so a fresh zone fills in gradually "
                 + "rather than materialising all at once.")]
        [SerializeField] private int _maxSpawnsPerTick = 2;
        [Tooltip("Scales every zone's roamer budget. Lower for performance, 0 to disable roamers.")]
        [Range(0f, 2f)][SerializeField] private float _populationScale = 1f;

        [Header("Behaviour")]
        [Tooltip("Species not listed here default to Skittish.")]
        [SerializeField]
        private List<TemperamentAssignment> _temperaments = new List<TemperamentAssignment>
        {
            new TemperamentAssignment { SpeciesId = SliceRoster.Pidgey, Temperament = Temperament.Skittish },
            new TemperamentAssignment { SpeciesId = SliceRoster.Rattata, Temperament = Temperament.Skittish },
            new TemperamentAssignment { SpeciesId = SliceRoster.Oddish, Temperament = Temperament.Placid },
            new TemperamentAssignment { SpeciesId = SliceRoster.Pikachu, Temperament = Temperament.Curious },
            new TemperamentAssignment { SpeciesId = SliceRoster.Poliwag, Temperament = Temperament.Curious },
            new TemperamentAssignment { SpeciesId = SliceRoster.Machop, Temperament = Temperament.Aggressive },
            new TemperamentAssignment { SpeciesId = SliceRoster.Geodude, Temperament = Temperament.Aggressive },
            new TemperamentAssignment { SpeciesId = SliceRoster.Zubat, Temperament = Temperament.Territorial },
            new TemperamentAssignment { SpeciesId = SliceRoster.Gastly, Temperament = Temperament.Territorial },
        };

        [Header("Determinism")]
        [SerializeField] private int _seed = 991733;

        private readonly List<RoamingCreature> _pool = new List<RoamingCreature>();
        private readonly List<RoamingCreature> _active = new List<RoamingCreature>();
        private DeterministicRandom _rng;
        private float _timer;

        /// <summary>Creatures currently alive in the world under this spawner.</summary>
        public IReadOnlyList<RoamingCreature> Active => _active;

        private void Awake() => _rng = new DeterministicRandom(_seed);

        private void Start()
        {
            if (_zoneDirector == null) _zoneDirector = ZoneDirector.Instance;
            if (_clock == null) _clock = DayNightCycle.Instance;
            if (_weather == null) _weather = WeatherDirector.Instance;
            if (_spawnParent == null) _spawnParent = transform;

            if (_despawnDistance <= _maxSpawnDistance)
            {
                _despawnDistance = _maxSpawnDistance * 1.4f;
                Debug.LogWarning("[RoamingCreatureSpawner] Despawn distance must exceed max spawn distance; widened.", this);
            }
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < _evaluationInterval) return;
            _timer = 0f;
            Evaluate();
        }

        private void Evaluate()
        {
            if (_player == null || _roamerPrefab == null) return;

            // Never mutate the population mid-encounter: recycling the creature the player is
            // about to fight would strand the battle.
            var director = EncounterDirector.Instance;
            if (director != null && director.SequenceRunning) return;

            CullDistant();

            var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;
            if (zone == null) return;

            var budget = Mathf.RoundToInt(zone.RoamerBudget * _populationScale);
            var deficit = budget - _active.Count;
            if (deficit <= 0) return;

            var spawns = Mathf.Min(deficit, _maxSpawnsPerTick);
            for (var i = 0; i < spawns; i++) TrySpawn(zone);
        }

        private void CullDistant()
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var creature = _active[i];
                if (creature == null)
                {
                    _active.RemoveAt(i);
                    continue;
                }

                if (!creature.gameObject.activeSelf)
                {
                    // Caught or beaten. Return it to the pool for reuse.
                    _active.RemoveAt(i);
                    _pool.Add(creature);
                    continue;
                }

                if (Vector3.Distance(creature.transform.position, _player.position) < _despawnDistance) continue;

                creature.gameObject.SetActive(false);
                _active.RemoveAt(i);
                _pool.Add(creature);
            }
        }

        private void TrySpawn(WorldZone zone)
        {
            var time = _clock != null ? _clock.Phase : TimeOfDay.Day;
            var weather = _weather != null ? _weather.EffectiveWeather : Weather.Clear;

            var table = zone.TableFor(TraversalState.TallGrass);
            if (table == null) return;
            if (!table.TryRoll(ref _rng, time, weather, out var speciesId, out var level)) return;

            if (!TryFindSpawnPoint(zone, out var point)) return;

            var creature = Rent();
            if (creature == null) return;

            creature.transform.SetPositionAndRotation(point, Quaternion.Euler(0f, _rng.Range(0f, 360f), 0f));
            creature.gameObject.SetActive(true);
            creature.Configure(speciesId, level, TemperamentFor(speciesId), point);
            _active.Add(creature);
        }

        /// <summary>
        /// Finds a NavMesh point inside the zone that sits in the annulus around the player. The
        /// inner radius is what stops creatures appearing in front of you; the outer keeps them
        /// close enough that the world feels populated rather than distant.
        /// </summary>
        private bool TryFindSpawnPoint(WorldZone zone, out Vector3 point)
        {
            point = Vector3.zero;
            for (var attempt = 0; attempt < _placementAttempts; attempt++)
            {
                if (!zone.TryGetSpawnPoint(ref _rng, out var candidate)) continue;

                var distance = Vector3.Distance(candidate, _player.position);
                if (distance < _minSpawnDistance || distance > _maxSpawnDistance) continue;

                if (!NavMesh.SamplePosition(candidate, out var hit, _navSampleRadius, NavMesh.AllAreas)) continue;

                // Re-check after the snap: SamplePosition can drag a point back inside the ring.
                if (Vector3.Distance(hit.position, _player.position) < _minSpawnDistance) continue;

                point = hit.position;
                return true;
            }
            return false;
        }

        private RoamingCreature Rent()
        {
            for (var i = _pool.Count - 1; i >= 0; i--)
            {
                var pooled = _pool[i];
                _pool.RemoveAt(i);
                if (pooled != null) return pooled;
            }
            return Instantiate(_roamerPrefab, _spawnParent);
        }

        private Temperament TemperamentFor(int speciesId)
        {
            for (var i = 0; i < _temperaments.Count; i++)
                if (_temperaments[i].SpeciesId == speciesId) return _temperaments[i].Temperament;
            return Temperament.Skittish;
        }

        /// <summary>Clears every roamer. Used before a save load, so the world repopulates fresh.</summary>
        public void DespawnAll()
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i] == null) continue;
                _active[i].gameObject.SetActive(false);
                _pool.Add(_active[i]);
            }
            _active.Clear();
        }
    }
}
