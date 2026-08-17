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
    ///
    /// <b>Step-based grass encounters stay switched on.</b> Visible roamers are an addition here,
    /// not a replacement, and nothing in <see cref="TallGrassPatch"/> or
    /// <see cref="EncounterDirector.AccumulatePressure"/> has been disabled. The reason is what
    /// the tall grass is: this project draws it tall enough to hide a creature in, and it is
    /// instanced and displaced so it parts as the player walks through. Grass that can no longer
    /// produce anything is a prop that costs a draw call and lies about what it is for. Keeping
    /// both gives the two kinds of ground different meanings — open ground is where you see what
    /// is coming and can walk around it, grass is where you cannot — which is a choice the player
    /// makes with their feet, and it is a better feature than either half alone.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoamingCreatureSpawner : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private ZoneDirector _zoneDirector;
        [SerializeField] private DayNightCycle _clock;
        [SerializeField] private WeatherDirector _weather;
        [SerializeField] private Transform _player;
        [Tooltip("Optional prefab carrying RoamingCreature + NavMeshAgent. Leave empty and one is "
                 + "built at runtime; art is resolved per species either way, so one shape serves all.")]
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
        [Tooltip("Target population when no WorldZone is active and there is no zone budget to read.")]
        [Min(0)][SerializeField] private int _defaultBudget = 6;
        [Tooltip("Fraction of spawn attempts that try the water table instead of the land one. "
                 + "Only used when a water surface is actually loaded.")]
        [Range(0f, 1f)][SerializeField] private float _aquaticShare = 0.3f;

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

            // The water cast. Without these every fish fell through to Skittish and the whole
            // lake fled as one the moment the player reached the shore, which made the surface
            // read as empty at exactly the distance you want it to look busy.
            new TemperamentAssignment { SpeciesId = SliceRoster.Magikarp, Temperament = Temperament.Placid },
            new TemperamentAssignment { SpeciesId = SliceRoster.Goldeen, Temperament = Temperament.Skittish },
            new TemperamentAssignment { SpeciesId = SliceRoster.Horsea, Temperament = Temperament.Skittish },
            new TemperamentAssignment { SpeciesId = SliceRoster.Psyduck, Temperament = Temperament.Curious },
            new TemperamentAssignment { SpeciesId = SliceRoster.Slowpoke, Temperament = Temperament.Placid },
            new TemperamentAssignment { SpeciesId = SliceRoster.Staryu, Temperament = Temperament.Placid },
            new TemperamentAssignment { SpeciesId = SliceRoster.Krabby, Temperament = Temperament.Aggressive },
            new TemperamentAssignment { SpeciesId = SliceRoster.Squirtle, Temperament = Temperament.Skittish },
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

            // Found by tag rather than wired in the inspector, because the spawner is created by
            // PlayerRigSetup.BuildHosts and the player it needs is built in the same pass; a
            // serialized reference would have to survive every Rebuild Everything to stay valid.
            if (_player == null)
            {
                var playerObject = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
                if (playerObject != null) _player = playerObject.transform;
            }

            if (_player == null)
            {
                Debug.LogWarning("[RoamingCreatureSpawner] No object tagged Player in this scene, so "
                                 + "there is nothing to keep a population around. Roamers are off.", this);
                enabled = false;
                return;
            }

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
            if (_player == null) return;

            // Never mutate the population mid-encounter: recycling the creature the player is
            // about to fight would strand the battle.
            var director = EncounterDirector.Instance;
            if (director != null && director.SequenceRunning) return;

            // The second half of the prologue lockdown, and it is genuinely a second half:
            // EncounterDirector.CanEncounter stops a roamer *starting a battle*, and does nothing
            // about a roamer existing. Left running, the bank Linden is muttering on fills up with
            // Pidgey that walk into the player and produce nothing, which reads as broken rather
            // than as suppressed. Emptying rather than merely pausing, because a population
            // spawned before the flag was read would otherwise stand around for the whole act.
            if (StoryPhase.RoamersSuppressed)
            {
                if (_active.Count > 0) DespawnAll();
                return;
            }

            CullDistant();

            // A zone is an input, not a requirement.
            //
            // This used to return here when ActiveZone was null, which in this project meant
            // always: nothing builds a WorldZone or a ZoneDirector into any scene, so the whole
            // roamer population was gated behind a component that does not exist and not one
            // creature had ever spawned. EncounterDirector already had the right answer for the
            // same problem — it falls through to EncounterTable.BuiltInFor — and matching it here
            // means the two systems still cannot disagree about what lives where once zones do
            // get authored.
            var zone = _zoneDirector != null ? _zoneDirector.ActiveZone : null;

            var budget = Mathf.RoundToInt((zone != null ? zone.RoamerBudget : _defaultBudget) * _populationScale);
            var deficit = budget - _active.Count;
            if (deficit <= 0) return;

            var spawns = Mathf.Min(deficit, _maxSpawnsPerTick);
            for (var i = 0; i < spawns; i++) TrySpawn(zone);
        }

        /// <summary>
        /// The table this zone uses for a traversal state, or the slice's built-in one. Identical
        /// resolution to <see cref="EncounterDirector"/>, deliberately: the creature you can see
        /// walking has to be one the grass could also have produced.
        /// </summary>
        private static EncounterTable ResolveTable(WorldZone zone, TraversalState traversal)
            => zone != null
                ? zone.TableFor(traversal)
                : EncounterTable.BuiltInFor(ZoneKind.Route, traversal);

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

        /// <summary>
        /// Rolls one creature and places it.
        ///
        /// Which table is rolled decides where the creature goes, and that is the whole reason
        /// Magikarp never turns up in a field: it is a water-table species, so it is only ever
        /// rolled on a water attempt, and a water attempt only ever places on a
        /// <see cref="WaterBody"/>. Nothing here knows which species swim — that fact stays in
        /// the encounter tables, where it already was and where the grass and the surf both read
        /// it from.
        /// </summary>
        private void TrySpawn(WorldZone zone)
        {
            var time = _clock != null ? _clock.Phase : TimeOfDay.Day;
            var weather = _weather != null ? _weather.EffectiveWeather : Weather.Clear;

            // Water is attempted only in proportion to how much of it there is to attempt on.
            // With no lake loaded the roll is skipped entirely rather than rolled and thrown
            // away, so an inland route does not spend its whole budget failing to place fish.
            var aquatic = WaterBody.AllLive.Count > 0 && _rng.NextFloat() < _aquaticShare;

            var traversal = aquatic ? TraversalState.Water : TraversalState.TallGrass;
            var table = ResolveTable(zone, traversal);
            if (table == null) return;
            if (!table.TryRoll(ref _rng, time, weather, out var speciesId, out var level)) return;

            if (!TryFindSpawnPoint(zone, aquatic, out var point)) return;

            var creature = Rent();
            if (creature == null) return;

            creature.transform.SetPositionAndRotation(point, Quaternion.Euler(0f, _rng.Range(0f, 360f), 0f));
            creature.gameObject.SetActive(true);
            creature.Configure(speciesId, level, TemperamentFor(speciesId), point, aquatic);
            _active.Add(creature);
        }

        /// <summary>
        /// Finds a point in the annulus around the player to put a creature on. The inner radius
        /// is what stops creatures appearing in front of you; the outer keeps them close enough
        /// that the world feels populated rather than distant.
        /// </summary>
        private bool TryFindSpawnPoint(WorldZone zone, bool aquatic, out Vector3 point)
        {
            point = Vector3.zero;
            for (var attempt = 0; attempt < _placementAttempts; attempt++)
            {
                if (!TryProposeCandidate(zone, aquatic, out var candidate)) continue;

                var distance = Vector3.Distance(candidate, _player.position);
                if (distance < _minSpawnDistance || distance > _maxSpawnDistance) continue;

                if (aquatic)
                {
                    // Already confirmed to be on a water surface by the proposer; the navmesh
                    // excludes water entirely, so snapping to it here would drag the fish ashore.
                    point = candidate;
                    return true;
                }

                if (!NavMesh.SamplePosition(candidate, out var hit, _navSampleRadius, NavMesh.AllAreas)) continue;

                // Re-check after the snap: SamplePosition can drag a point back inside the ring.
                if (Vector3.Distance(hit.position, _player.position) < _minSpawnDistance) continue;

                point = hit.position;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Proposes one unvalidated candidate position.
        ///
        /// Zone anchors and volumes are preferred where a zone exists, because an author placing
        /// them has said something about where creatures belong. With no zone — which is every
        /// scene in this project today — the fallback is a ring around the player, which needs no
        /// authoring at all and still produces creatures at a sensible distance. The ring is
        /// sampled directly rather than over the whole level so the cost does not grow with the
        /// size of the map.
        /// </summary>
        private bool TryProposeCandidate(WorldZone zone, bool aquatic, out Vector3 candidate)
        {
            if (aquatic)
            {
                var body = WaterBody.AllLive[_rng.Range(0, WaterBody.AllLive.Count)];
                if (body != null && body.TryGetRandomSurfacePoint(ref _rng, out candidate)) return true;
                candidate = Vector3.zero;
                return false;
            }

            if (zone != null && zone.TryGetSpawnPoint(ref _rng, out candidate)) return true;

            var angle = _rng.Range(0f, Mathf.PI * 2f);
            var radius = _rng.Range(_minSpawnDistance, _maxSpawnDistance);
            candidate = _player.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            return true;
        }

        private RoamingCreature Rent()
        {
            for (var i = _pool.Count - 1; i >= 0; i--)
            {
                var pooled = _pool[i];
                _pool.RemoveAt(i);
                if (pooled != null) return pooled;
            }
            return _roamerPrefab != null ? Instantiate(_roamerPrefab, _spawnParent) : BuildRoamer();
        }

        /// <summary>
        /// Builds a roamer from nothing.
        ///
        /// The prefab field stays supported, but it cannot be the only path: every roamer is one
        /// NavMeshAgent and one sprite quad whose species is chosen at spawn, so a prefab asset
        /// would carry no per-species authoring at all — it would exist purely to be a thing to
        /// reference, and it would be a thing to reference that no scene rebuild recreates. The
        /// project already builds its overworld content procedurally for exactly this reason.
        /// </summary>
        private RoamingCreature BuildRoamer()
        {
            var go = new GameObject("Roamer");
            go.transform.SetParent(_spawnParent, false);

            var creatureLayer = LayerMask.NameToLayer(OverworldNames.LayerCreature);
            if (creatureLayer >= 0) go.layer = creatureLayer;

            // Placed on the navmesh before the agent exists.
            //
            // A NavMeshAgent validates its position the instant it is added, and complains —
            // "Failed to create agent because it is not close enough to the NavMesh" — before
            // any line after AddComponent can move it. An agent that fails to create is not
            // merely misplaced; every call on it throws for the rest of its life. So the
            // position is settled first, and where there is no navmesh within reach the agent
            // is not added at all: a creature that walks nowhere is better than one that
            // throws on every step.
            if (!UnityEngine.AI.NavMesh.SamplePosition(go.transform.position, out var onMesh, 6f,
                                                       UnityEngine.AI.NavMesh.AllAreas))
            {
                Debug.LogWarning($"[Roamers] No navmesh within 6 m of {go.transform.position}, so no agent " +
                                 "was created. Whatever was going to walk will stand still.");
                return null;
            }
            go.transform.position = onMesh.position;

            var agent = go.AddComponent<NavMeshAgent>();
            // Off the navmesh nothing else on the agent matters, and auto-braking is what stops a
            // wanderer overshooting its destination and visibly correcting.
            agent.autoBraking = true;
            agent.angularSpeed = 360f;
            agent.acceleration = 12f;
            agent.stoppingDistance = 0.35f;
            // Roamers push past each other rather than forming queues; a herd that politely
            // single-files around a rock is the tell that they are agents, not animals.
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

            return go.AddComponent<RoamingCreature>();
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
