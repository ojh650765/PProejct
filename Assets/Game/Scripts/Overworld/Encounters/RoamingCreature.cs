using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// How a species reacts to being noticed. This is the whole personality model — one enum —
    /// because four legible behaviours the player can learn beat twelve they cannot distinguish.
    /// </summary>
    public enum Temperament
    {
        /// <summary>Bolts the moment you get close. Pidgey, Rattata, Oddish.</summary>
        Skittish = 0,
        /// <summary>Comes to look at you, then loses interest. Pikachu, Poliwag.</summary>
        Curious = 1,
        /// <summary>Charges. Machop, Geodude.</summary>
        Aggressive = 2,
        /// <summary>Barely registers you. Grazers.</summary>
        Placid = 3,
        /// <summary>Ignores you at range, charges if you enter its patch. Zubat, Gastly.</summary>
        Territorial = 4,
    }

    /// <summary>
    /// A wild creature that actually exists in the world: it walks, grazes, notices you and
    /// reacts. Touching it starts a battle against <em>that</em> creature — its species and its
    /// level, not a table roll.
    ///
    /// Three things do most of the work in making these read as alive rather than as patrolling
    /// cubes:
    ///
    /// - <b>Idle variety.</b> Wandering alternates with grazing and looking around, with the mix
    ///   drawn per-creature from its own seed, so no two are in phase.
    /// - <b>Graded awareness.</b> Alertness accumulates with proximity and line of sight and
    ///   decays when you back off, so a creature notices you, hesitates, and only then commits.
    ///   An instant state flip at a radius is the single biggest tell that something is scripted.
    /// - <b>Alarm propagation.</b> One creature bolting startles its neighbours. A herd that
    ///   scatters together is the cheapest possible signal that the world is simulated.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public sealed class RoamingCreature : MonoBehaviour, IEncounterSource
    {
        private enum State { Spawning, Wander, Graze, Alert, Flee, Approach, Hold, Retreating, Consumed }

        [Header("Identity")]
        [SerializeField] private int _speciesId = SliceRoster.Pidgey;
        [Min(1)][SerializeField] private int _level = 4;
        [SerializeField] private Temperament _temperament = Temperament.Skittish;

        [Header("Model")]
        [Tooltip("Parent the art is instantiated under. Leave null to use this transform.")]
        [SerializeField] private Transform _modelRoot;
        [Tooltip("Prefab used when ICreatureArtRegistry is unavailable. Leave null for no visual — "
                 + "the real path resolves art through the registry.")]
        [SerializeField] private GameObject _fallbackModelPrefab;

        [Header("Territory")]
        [Tooltip("Radius the creature wanders inside, around wherever it was spawned.")]
        [SerializeField] private float _homeRadius = 14f;
        [Tooltip("Distance from home beyond which it gives up and walks back.")]
        [SerializeField] private float _leashRadius = 26f;

        [Header("Movement (m/s)")]
        [SerializeField] private float _wanderSpeed = 1.3f;
        [SerializeField] private float _alertSpeed = 2.2f;
        [SerializeField] private float _fleeSpeed = 4.4f;
        [SerializeField] private float _chaseSpeed = 3.6f;

        [Header("Perception")]
        [Tooltip("Distance at which the player starts registering at all.")]
        [SerializeField] private float _noticeRadius = 11f;
        [Tooltip("Half-angle of the vision cone in degrees. Beyond it, only proximity registers.")]
        [SerializeField] private float _visionHalfAngle = 70f;
        [Tooltip("Seconds of continuous close attention before the creature commits to a reaction.")]
        [SerializeField] private float _commitSeconds = 0.7f;
        [Tooltip("Blocks line of sight. Leave at Nothing to skip the occlusion check entirely.")]
        [SerializeField] private LayerMask _sightBlockers;

        [Header("Reaction")]
        [Tooltip("Distance a skittish creature tries to keep between itself and the player.")]
        [SerializeField] private float _fleeDistance = 18f;
        [Tooltip("Distance a curious creature settles at when it comes to look at you.")]
        [SerializeField] private float _curiousHoldDistance = 3.5f;
        [Tooltip("Radius within which a territorial creature treats you as an intruder.")]
        [SerializeField] private float _territoryRadius = 6f;
        [Tooltip("Distance at which contact starts the battle.")]
        [SerializeField] private float _contactDistance = 1.35f;
        [Tooltip("Radius over which this creature's panic startles others of its kind.")]
        [SerializeField] private float _alarmRadius = 12f;

        [Header("Idle rhythm")]
        [Tooltip("Seconds between wander destinations, drawn uniformly.")]
        [SerializeField] private Vector2 _wanderPauseRange = new Vector2(1.4f, 5.0f);
        [Tooltip("Chance a pause becomes a graze rather than a plain idle.")]
        [Range(0f, 1f)][SerializeField] private float _grazeChance = 0.45f;
        [SerializeField] private Vector2 _grazeDurationRange = new Vector2(2.5f, 6.0f);
        [Tooltip("Chance per idle beat of a look-around. Keeps heads moving during long grazes.")]
        [Range(0f, 1f)][SerializeField] private float _lookAroundChance = 0.35f;

        [Header("Debug")]
        [Tooltip("Spawns a capsule when no creature art can be resolved. Development only — the "
                 + "real path is ICreatureArtRegistry, and this defaults off.")]
        [SerializeField] private bool _debugSpawnPlaceholderCapsule;

        private NavMeshAgent _agent;
        private Transform _player;
        private ICreatureView _view;
        private Animator _animator;
        private GameObject _model;
        private State _state = State.Spawning;
        private Vector3 _home;
        private DeterministicRandom _rng;
        private float _stateTimer;
        private float _alertness;
        private float _cooldownTimer;
        private CreatureAnimation _currentAnimation = CreatureAnimation.Idle;

        private static readonly List<RoamingCreature> Live = new List<RoamingCreature>();

        public int SpeciesId => _speciesId;
        public int Level => _level;
        public Temperament Temperament => _temperament;

        /// <summary>Home point the creature leashes to. Set by the spawner at placement.</summary>
        public Vector3 Home => _home;

        /// <summary>False once it has been caught, beaten, or is walking off after a flee.</summary>
        public bool IsAvailable => _state != State.Consumed && _state != State.Retreating && _cooldownTimer <= 0f;

        /// <summary>Raised when the creature is removed, so the spawner can rebalance the population.</summary>
        public event Action<RoamingCreature> Despawned;

        public EncounterSourceKind SourceKind => EncounterSourceKind.Roamer;
        public Vector3 TelegraphPosition => transform.position;

        /// <summary>
        /// The approach beat for a visible creature. There is no grass to rustle — the creature
        /// itself is the telegraph — so it stops, squares up to the player and holds for a moment.
        /// Shorter than the grass rustle, because the player has already seen it coming.
        /// </summary>
        public float PlayApproachTelegraph()
        {
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }

            if (_player != null)
            {
                var direction = Vector3.ProjectOnPlane(_player.position - transform.position, Vector3.up);
                if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
            }

            _view?.Play(CreatureAnimation.IdleBattle, 0.2f);
            _currentAnimation = CreatureAnimation.IdleBattle;
            return 0.45f;
        }

        /// <summary>Every live roamer, for alarm propagation and spawner budgeting.</summary>
        public static IReadOnlyList<RoamingCreature> AllLive => Live;

        /// <summary>Clears the live registry. Called on play mode start; see OverworldLifecycle.</summary>
        internal static void ResetRegistry() => Live.Clear();

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _home = transform.position;
            _rng = new DeterministicRandom(GetInstanceID());
            if (_modelRoot == null) _modelRoot = transform;
        }

        private void OnEnable() => Live.Add(this);

        private void OnDisable()
        {
            Live.Remove(this);
            Despawned?.Invoke(this);
        }

        private void Start()
        {
            var playerObject = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
            if (playerObject != null) _player = playerObject.transform;

            BuildModel();
            EnterWander();
        }

        /// <summary>
        /// Configures a pooled creature at spawn. The spawner calls this instead of setting fields,
        /// so a recycled instance is fully reset — a half-reset roamer keeps the previous species'
        /// model and is a very confusing bug to find.
        /// </summary>
        public void Configure(int speciesId, int level, Temperament temperament, Vector3 home)
        {
            _speciesId = speciesId;
            _level = level;
            _temperament = temperament;
            _home = home;
            _state = State.Spawning;
            _alertness = 0f;
            _cooldownTimer = 0f;
            _rng = new DeterministicRandom(GetInstanceID() ^ speciesId * 7919);

            if (_agent != null && _agent.isOnNavMesh) _agent.Warp(home);
            else transform.position = home;

            BuildModel();
            EnterWander();
        }

        /// <summary>
        /// Resolves the creature's art through <see cref="ICreatureArtRegistry"/>. Returning null
        /// is contractually survivable, so a missing model degrades to an invisible-but-functional
        /// roamer rather than to an exception.
        /// </summary>
        private void BuildModel()
        {
            if (_model != null)
            {
                Destroy(_model);
                _model = null;
                _view = null;
                _animator = null;
            }

            GameObject prefab = null;
            if (ServiceHub.TryGet<ICreatureArtRegistry>(out var registry))
                prefab = registry.GetCreaturePrefab(_speciesId);
            if (prefab == null) prefab = _fallbackModelPrefab;

            if (prefab != null)
            {
                _model = Instantiate(prefab, _modelRoot);
                _model.transform.localPosition = Vector3.zero;
                _model.transform.localRotation = Quaternion.identity;
                _view = _model.GetComponentInChildren<ICreatureView>();
                _animator = _model.GetComponentInChildren<Animator>();
            }
            else if (_debugSpawnPlaceholderCapsule)
            {
                _model = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                _model.name = "DEBUG_Placeholder_" + SliceRoster.FallbackName(_speciesId);
                var placeholderCollider = _model.GetComponent<Collider>();
                if (placeholderCollider != null) Destroy(placeholderCollider);
                _model.transform.SetParent(_modelRoot, false);
                _model.transform.localScale = Vector3.one * 0.5f;
            }

            ScaleColliderToSpecies();
        }

        /// <summary>
        /// Matches the agent's footprint to the creature's real size so a Geodude and a Gastly do
        /// not push each other around identically.
        /// </summary>
        private void ScaleColliderToSpecies()
        {
            var height = SliceRoster.FallbackHeight(_speciesId);
            if (ServiceHub.TryGet<ICreatureArtRegistry>(out var registry))
            {
                var registryHeight = registry.GetDisplayHeight(_speciesId);
                if (registryHeight > 0.01f) height = registryHeight;
            }

            if (_agent != null)
            {
                _agent.height = Mathf.Max(0.2f, height);
                _agent.radius = Mathf.Clamp(height * 0.4f, 0.15f, 1.2f);
            }
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            if (_cooldownTimer > 0f) _cooldownTimer -= dt;
            if (_state == State.Consumed) return;

            UpdatePerception(dt);
            UpdateState(dt);
            DriveAnimation();
        }

        /// <summary>
        /// Builds alertness from proximity, facing and line of sight, and bleeds it off when the
        /// player retreats. The gradual build is what produces the hesitation beat.
        /// </summary>
        private void UpdatePerception(float dt)
        {
            if (_player == null || _state == State.Retreating)
            {
                _alertness = Mathf.MoveTowards(_alertness, 0f, dt);
                return;
            }

            var toPlayer = _player.position - transform.position;
            var distance = toPlayer.magnitude;

            if (distance > _noticeRadius)
            {
                _alertness = Mathf.MoveTowards(_alertness, 0f, dt * 0.7f);
                return;
            }

            // Proximity dominates: something at your feet registers whether or not you are facing it.
            var proximity = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, _noticeRadius));
            var angle = Vector3.Angle(transform.forward, toPlayer);
            var facing = angle <= _visionHalfAngle ? 1f : 0.35f;

            if (_sightBlockers.value != 0 && distance > 0.5f)
            {
                var eye = transform.position + Vector3.up * Mathf.Max(0.2f, _agent.height * 0.7f);
                if (Physics.Linecast(eye, _player.position + Vector3.up, _sightBlockers, QueryTriggerInteraction.Ignore))
                    facing *= 0.25f;
            }

            var gain = proximity * facing / Mathf.Max(0.05f, _commitSeconds);
            _alertness = Mathf.Clamp01(_alertness + gain * dt);
        }

        private void UpdateState(float dt)
        {
            _stateTimer -= dt;
            var distance = _player != null ? Vector3.Distance(transform.position, _player.position) : float.MaxValue;

            switch (_state)
            {
                case State.Wander:
                case State.Graze:
                    TickIdle(dt, distance);
                    break;

                case State.Alert:
                    FacePlayer(dt);
                    if (_stateTimer <= 0f) CommitReaction(distance);
                    break;

                case State.Flee:
                    TickFlee(distance);
                    break;

                case State.Approach:
                    TickApproach(distance);
                    break;

                case State.Hold:
                    FacePlayer(dt);
                    // Curiosity is finite; wandering off again is what stops a creature from
                    // standing and staring at the player forever.
                    if (_stateTimer <= 0f || distance > _noticeRadius) EnterWander();
                    else if (distance <= _contactDistance) StartBattle();
                    break;

                case State.Retreating:
                    if (_stateTimer <= 0f || (_agent.isOnNavMesh && !_agent.pathPending && _agent.remainingDistance < 1.5f))
                        EnterWander();
                    break;
            }

            LeashHome();
        }

        private void TickIdle(float dt, float distance)
        {
            if (_temperament != Temperament.Placid && _alertness >= 1f && IsAvailable)
            {
                EnterAlert();
                return;
            }

            // Territorial creatures do not need to build alertness — crossing the line is enough.
            if (_temperament == Temperament.Territorial && distance <= _territoryRadius && IsAvailable)
            {
                EnterAlert();
                return;
            }

            if (distance <= _contactDistance && IsAvailable)
            {
                StartBattle();
                return;
            }

            if (_stateTimer > 0f)
            {
                if (_state == State.Graze) TickGraze(dt);
                return;
            }

            if (_state == State.Graze || _rng.NextFloat() >= _grazeChance) EnterWander();
            else EnterGraze();
        }

        private void TickGraze(float dt)
        {
            // Occasional head-up glances during a long graze. Cheap, and the difference between a
            // creature eating and a creature frozen mid-animation.
            if (_rng.NextFloat() < _lookAroundChance * dt)
            {
                var yaw = _rng.Range(-110f, 110f);
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    transform.rotation * Quaternion.Euler(0f, yaw, 0f), 0.35f);
            }
        }

        private void TickFlee(float distance)
        {
            if (distance >= _fleeDistance || _stateTimer <= 0f)
            {
                EnterRetreat();
                return;
            }

            if (_agent.isOnNavMesh && (!_agent.hasPath || _agent.remainingDistance < 2f))
            {
                var away = (transform.position - _player.position).normalized;
                var target = transform.position + away * 9f;
                if (NavMesh.SamplePosition(target, out var hit, 6f, NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
                else
                    // Cornered: cut sideways instead of grinding into the geometry behind you.
                    _agent.SetDestination(transform.position + Vector3.Cross(away, Vector3.up) * 6f);
            }
        }

        private void TickApproach(float distance)
        {
            if (distance <= _contactDistance)
            {
                StartBattle();
                return;
            }

            var giveUp = _temperament == Temperament.Aggressive || _temperament == Temperament.Territorial
                ? _leashRadius
                : _noticeRadius * 1.2f;

            if (distance > giveUp || _stateTimer <= 0f)
            {
                EnterRetreat();
                return;
            }

            if (_temperament == Temperament.Curious && distance <= _curiousHoldDistance)
            {
                EnterHold();
                return;
            }

            if (_player != null && _agent.isOnNavMesh) _agent.SetDestination(_player.position);
        }

        private void LeashHome()
        {
            if (_state == State.Flee || _state == State.Consumed) return;
            if (Vector3.Distance(transform.position, _home) <= _leashRadius) return;
            if (!_agent.isOnNavMesh) return;
            _agent.SetDestination(_home);
        }

        private void FacePlayer(float dt)
        {
            if (_player == null) return;
            var direction = Vector3.ProjectOnPlane(_player.position - transform.position, Vector3.up);
            if (direction.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(direction), 1f - Mathf.Exp(-8f * dt));
        }

        // ---- State entries ---------------------------------------------------------------

        private void EnterWander()
        {
            _state = State.Wander;
            _stateTimer = _rng.Range(_wanderPauseRange.x, _wanderPauseRange.y);
            SetSpeed(_wanderSpeed);
            if (_agent == null || !_agent.isOnNavMesh) return;

            _agent.isStopped = false;
            var offset = new Vector3(_rng.Range(-1f, 1f), 0f, _rng.Range(-1f, 1f)).normalized
                         * _rng.Range(_homeRadius * 0.25f, _homeRadius);
            if (NavMesh.SamplePosition(_home + offset, out var hit, _homeRadius, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        private void EnterGraze()
        {
            _state = State.Graze;
            _stateTimer = _rng.Range(_grazeDurationRange.x, _grazeDurationRange.y);
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }
        }

        private void EnterAlert()
        {
            _state = State.Alert;
            _stateTimer = _rng.Range(0.35f, 0.8f);
            SetSpeed(_alertSpeed);
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }
        }

        private void CommitReaction(float distance)
        {
            switch (_temperament)
            {
                case Temperament.Skittish:
                    EnterFlee(propagateAlarm: true);
                    break;
                case Temperament.Aggressive:
                case Temperament.Territorial:
                    EnterApproach();
                    break;
                case Temperament.Curious:
                    if (distance <= _curiousHoldDistance) EnterHold();
                    else EnterApproach();
                    break;
                default:
                    EnterWander();
                    break;
            }
        }

        private void EnterFlee(bool propagateAlarm)
        {
            _state = State.Flee;
            _stateTimer = 6f;
            SetSpeed(_fleeSpeed);
            if (_agent != null && _agent.isOnNavMesh) _agent.isStopped = false;
            if (propagateAlarm) PropagateAlarm();
        }

        private void EnterApproach()
        {
            _state = State.Approach;
            _stateTimer = 8f;
            SetSpeed(_chaseSpeed);
            if (_agent != null && _agent.isOnNavMesh) _agent.isStopped = false;
        }

        private void EnterHold()
        {
            _state = State.Hold;
            _stateTimer = _rng.Range(2.5f, 5.5f);
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }
        }

        private void EnterRetreat()
        {
            _state = State.Retreating;
            _stateTimer = 5f;
            _alertness = 0f;
            _cooldownTimer = 4f;
            SetSpeed(_wanderSpeed);
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = false;
                _agent.SetDestination(_home);
            }
        }

        /// <summary>
        /// Startles nearby creatures of the same species. Herd panic is the strongest single cue
        /// that the world is simulated rather than scripted, and it costs one loop.
        /// </summary>
        private void PropagateAlarm()
        {
            for (var i = 0; i < Live.Count; i++)
            {
                var other = Live[i];
                if (other == null || other == this) continue;
                if (other._speciesId != _speciesId) continue;
                if (other._state == State.Flee || other._state == State.Consumed) continue;
                if (Vector3.Distance(other.transform.position, transform.position) > _alarmRadius) continue;
                other.EnterFlee(propagateAlarm: false); // one hop only, or a whole route stampedes
            }
        }

        private void SetSpeed(float speed)
        {
            if (_agent != null) _agent.speed = speed;
        }

        private void StartBattle()
        {
            var director = EncounterDirector.Instance;
            if (director == null || !IsAvailable) return;

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }
            _state = State.Hold;
            _cooldownTimer = 2f;
            director.TriggerRoamerEncounter(this);
        }

        // ---- Presentation ---------------------------------------------------------------

        /// <summary>
        /// Maps state to the shared <see cref="CreatureAnimation"/> vocabulary. Going through
        /// <see cref="ICreatureView"/> rather than driving the Animator directly means the same
        /// rig the battle uses works here with no extra authoring.
        /// </summary>
        private void DriveAnimation()
        {
            var speed = _agent != null ? _agent.velocity.magnitude : 0f;
            CreatureAnimation desired;

            if (_state == State.Graze) desired = CreatureAnimation.Idle;
            else if (_state == State.Alert) desired = CreatureAnimation.IdleBattle;
            else if (speed > _wanderSpeed * 1.6f) desired = CreatureAnimation.Run;
            else if (speed > 0.15f) desired = CreatureAnimation.Walk;
            else desired = CreatureAnimation.Idle;

            if (desired != _currentAnimation)
            {
                _currentAnimation = desired;
                _view?.Play(desired);
            }

            // Rigs that expose a plain Speed float still animate, even without an ICreatureView.
            if (_animator != null) _animator.SetFloat("Speed", speed);
        }

        // ---- Outcome hooks ---------------------------------------------------------------

        /// <summary>Caught or defeated. The creature leaves the world; the spawner refills later.</summary>
        public void Consume()
        {
            _state = State.Consumed;
            if (_agent != null && _agent.isOnNavMesh) _agent.isStopped = true;
            gameObject.SetActive(false);
        }

        /// <summary>The battle ended without removing it — it runs off and is briefly untouchable.</summary>
        public void Retreat()
        {
            _cooldownTimer = 8f;
            EnterFlee(propagateAlarm: false);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.5f);
            Gizmos.DrawWireSphere(Application.isPlaying ? _home : transform.position, _homeRadius);
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, _noticeRadius);
        }
    }
}
