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
        // Where "bumped into it" is drawn. Measured pivot to pivot, so it has to cover the
        // player's capsule radius plus the creature's own footprint before the two sprites
        // visibly overlap — a battle that starts only on true geometric touch fires after the
        // creature is already inside the player, and one drawn much wider fires while there is
        // clear ground between them and reads as being grabbed from a distance. 1.35 m is a
        // little over one tile: close enough to be unambiguous, far enough to be avoidable at
        // walking speed, which is what makes choosing to dodge a roamer a real choice.
        [Tooltip("Distance at which contact starts the battle. A little over one tile.")]
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
        private IOverworldCreatureArt _art;
        private Animator _animator;
        private GameObject _model;
        private State _state = State.Spawning;
        private Vector3 _home;
        private DeterministicRandom _rng;
        private float _stateTimer;
        private float _alertness;
        private float _cooldownTimer;
        private CreatureAnimation _currentAnimation = CreatureAnimation.Idle;

        // --- Swimming ---------------------------------------------------------------------
        // A water species is moved by hand rather than by the NavMeshAgent, because the bake
        // deliberately excludes the Water layer (LevelLayoutBuilder.BuildNavigation collects
        // Ground, Environment and Interactable only). That exclusion is what keeps land roamers
        // out of the lake for free, and it is also why a Magikarp with an agent would be snapped
        // to the nearest bank the moment it spawned and spend the whole game flopping on grass.
        private bool _aquatic;
        private bool _configured;
        private Vector3 _swimTarget;
        private bool _hasSwimTarget;
        private float _moveSpeed;
        private Vector3 _swimVelocity;

        /// <summary>True for a species drawn from the zone's water table; it never leaves the water.</summary>
        public bool IsAquatic => _aquatic;

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
            StopMoving();

            if (_player != null)
            {
                var direction = Vector3.ProjectOnPlane(_player.position - transform.position, Vector3.up);
                if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
            }

            _view?.Play(CreatureAnimation.IdleBattle, 0.2f);
            _art?.Play(CreatureAnimation.IdleBattle);
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

        // The art outlives a despawn on purpose — a pooled roamer keeps its quad and material and
        // only rebinds the sheet — so it is released here rather than in OnDisable.
        private void OnDestroy() => _art?.Dispose();

        private void Start()
        {
            var playerObject = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
            if (playerObject != null) _player = playerObject.transform;

            // Only self-start when nobody configured us first. A spawner-built roamer is
            // Configure()d during the frame it is created, which is before this Start runs, and
            // re-running the setup here would throw away the species and the home point it was
            // just given and quietly restart it as whatever the serialized defaults say.
            if (_configured) return;

            BuildModel();
            EnterWander();
        }

        /// <summary>
        /// Configures a pooled creature at spawn. The spawner calls this instead of setting fields,
        /// so a recycled instance is fully reset — a half-reset roamer keeps the previous species'
        /// model and is a very confusing bug to find.
        /// </summary>
        public void Configure(int speciesId, int level, Temperament temperament, Vector3 home,
            bool aquatic = false)
        {
            _configured = true;
            _speciesId = speciesId;
            _level = level;
            _temperament = temperament;
            _home = home;
            _aquatic = aquatic;
            _state = State.Spawning;
            _alertness = 0f;
            _cooldownTimer = 0f;
            // Cleared with the rest of the per-spawn state. A pooled instance that was last a
            // staged ambusher would otherwise come back as a creature that never moves — or one
            // drawn half again its own size. The field rather than the property, because
            // BuildModel below binds at whatever the height resolves to now.
            _scriptedHold = false;
            _presenceScale = 1f;
            _swimVelocity = Vector3.zero;
            _hasSwimTarget = false;
            _rng = new DeterministicRandom(GetInstanceID() ^ speciesId * 7919);

            // Order matters here, and it is the whole reason this is not three lines.
            //
            // The agent is switched off entirely for a swimmer rather than left running with no
            // path: a NavMeshAgent on an object that is not on the navmesh logs "can only be
            // called on an active agent" once per frame, and one Magikarp is enough to bury the
            // console for the whole session.
            //
            // And the body is moved to its new home *before* the agent is re-enabled, because an
            // agent latches onto the navmesh at whatever position it is enabled at. A pooled
            // creature that was last a fish is still floating in the lake at this point, so
            // enabling first would try to place a land roamer out on the water, fail, and leave
            // it permanently off-mesh — a creature that stands still forever, one spawn in ten,
            // with nothing in the console to say why.
            if (_agent != null && _agent.enabled) _agent.enabled = false;
            transform.position = home;
            if (_agent != null) _agent.enabled = !aquatic;

            BuildModel();
            EnterWander();
        }

        /// <summary>
        /// Resolves the creature's art. Returning nothing is contractually survivable, so a
        /// missing sprite degrades to an invisible-but-functional roamer rather than an exception.
        ///
        /// The billboard is tried first and the rigged prefab second, which is the opposite of
        /// the order the battle uses and is deliberate. This is an HD-2D game: the only creature
        /// art that exists is the Gen 5 sprite set, and the only registered
        /// <see cref="ICreatureArtRegistry"/> — <c>DexDisplayHeights</c> — answers
        /// <c>GetCreaturePrefab</c> with null for every species by design, because it exists to
        /// serve heights and nothing else. A prefab-first roamer therefore resolved null, fell
        /// through to a fallback nobody had assigned, and walked the route invisibly. The prefab
        /// branch is kept below so a rigged 3D creature dropped in later still wins.
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

            if (_art == null && ServiceHub.TryGet<IOverworldCreatureArtFactory>(out var artFactory))
                _art = artFactory.Create(_modelRoot);

            if (_art != null)
            {
                _art.Bind(_speciesId, ResolveDisplayHeight());
                _art.SetVisible(true);
                _art.Play(CreatureAnimation.Idle);
                _currentAnimation = CreatureAnimation.Idle;
                ScaleColliderToSpecies();
                return;
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
        /// Drawn height in metres, from the dex-backed registry where one is up, falling back to
        /// the slice's hand-authored table so a roamer is still the right size before boot.
        /// Multiplied by <see cref="PresenceScale"/>, which is 1 for everything a spawner owns.
        /// </summary>
        private float ResolveDisplayHeight()
        {
            var height = SliceRoster.FallbackHeight(_speciesId);
            if (ServiceHub.TryGet<ICreatureArtRegistry>(out var registry))
            {
                var registryHeight = registry.GetDisplayHeight(_speciesId);
                if (registryHeight > 0.01f) height = registryHeight;
            }
            return height * _presenceScale;
        }

        /// <summary>
        /// Matches the agent's footprint to the creature's real size so a Geodude and a Gastly do
        /// not push each other around identically.
        ///
        /// Skipped entirely for a swimmer, whose agent is switched off and whose footprint
        /// therefore means nothing.
        /// </summary>
        private void ScaleColliderToSpecies()
        {
            if (_agent == null || !_agent.enabled) return;
            var height = ResolveDisplayHeight();
            _agent.height = Mathf.Max(0.2f, height);
            _agent.radius = Mathf.Clamp(height * 0.4f, 0.15f, 1.2f);
        }

        /// <summary>
        /// Hands this creature's movement to whoever staged it, and takes its own behaviour away.
        ///
        /// A creature an episode placed is furniture with a battle attached: the script decides
        /// where it stands, when it walks and when it fights. Everything autonomous is skipped
        /// while this is set — wandering, perception, leashing, and contact starting a battle —
        /// but the animation still runs off whatever speed the agent is being driven at, so a
        /// scripted walk still reads as a walk.
        ///
        /// Without it the ambusher wanders off its mark: <see cref="EnterWander"/> picks a new
        /// destination every few seconds inside a fourteen-metre home radius, which is further
        /// than three lines of dialogue are long. The creature the scene says is coming toward
        /// you would be somewhere behind the reeds by the time the player looked up.
        /// </summary>
        public bool ScriptedHold
        {
            get => _scriptedHold;
            set
            {
                _scriptedHold = value;
                if (value) StopMoving();
            }
        }

        private bool _scriptedHold;

        /// <summary>
        /// Multiplier on the drawn size — and, through <see cref="ScaleColliderToSpecies"/>, the
        /// footprint — over the species' dex height. 1 for every autonomous roamer: an ambient
        /// population is drawn true to the dex, and a Pidgey genuinely is 0.3 m tall.
        ///
        /// An episode sets this on the creatures it stages, because a staged creature is theatre.
        /// The small route species' real heights are barely-visible specks at the exploration
        /// camera's 8.5 m boom, and a scene whose lines are about the thing in the grass only
        /// works if the thing in the grass can be seen. Set after <see cref="Configure"/> — which
        /// resets it, like every other piece of per-spawn state — it rebinds the model at the new
        /// height, so the order of the two calls does not matter.
        /// </summary>
        public float PresenceScale
        {
            get => _presenceScale;
            set
            {
                var clamped = Mathf.Clamp(value, 0.25f, 4f);
                if (Mathf.Approximately(clamped, _presenceScale)) return;
                _presenceScale = clamped;
                if (_art != null)
                {
                    _art.Bind(_speciesId, ResolveDisplayHeight());
                    _art.Play(_currentAnimation);
                }
                ScaleColliderToSpecies();
            }
        }

        private float _presenceScale = 1f;

        private void Update()
        {
            var dt = Time.deltaTime;
            if (_cooldownTimer > 0f) _cooldownTimer -= dt;
            if (_state == State.Consumed) return;

            if (_scriptedHold)
            {
                DriveAnimation();
                return;
            }

            UpdatePerception(dt);
            UpdateState(dt);
            if (_aquatic) TickSwim(dt);
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
                // Eye height off the agent when there is one, off the species otherwise: a
                // swimmer has no enabled agent to read a height from.
                var bodyHeight = _agent != null && _agent.enabled ? _agent.height : ResolveDisplayHeight();
                var eye = transform.position + Vector3.up * Mathf.Max(0.2f, bodyHeight * 0.7f);
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
                    if (_stateTimer <= 0f || RemainingDistance < 1.5f) EnterWander();
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

            if (!CanMove || RemainingDistance >= 2f) return;

            var away = (transform.position - _player.position).normalized;
            var target = transform.position + away * 9f;

            if (_aquatic)
            {
                // Straight away, then sideways when the far bank is in the way. A fish in a small
                // pond runs out of "away" almost immediately, and one that keeps aiming at the
                // bank stalls against it in full view of the player.
                if (WaterBody.TrySampleAnySurface(target, out _)) MoveTo(target);
                else MoveTo(transform.position + Vector3.Cross(away, Vector3.up) * 6f);
                return;
            }

            if (NavMesh.SamplePosition(target, out var hit, 6f, NavMesh.AllAreas)) MoveTo(hit.position);
            // Cornered: cut sideways instead of grinding into the geometry behind you.
            else MoveTo(transform.position + Vector3.Cross(away, Vector3.up) * 6f);
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

            if (_player != null) MoveTo(_player.position);
        }

        private void LeashHome()
        {
            if (_state == State.Flee || _state == State.Consumed) return;
            if (Vector3.Distance(transform.position, _home) <= _leashRadius) return;
            MoveTo(_home);
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
            if (!CanMove) return;

            var offset = new Vector3(_rng.Range(-1f, 1f), 0f, _rng.Range(-1f, 1f)).normalized
                         * _rng.Range(_homeRadius * 0.25f, _homeRadius);

            // Swimmers aim at the raw point and let MoveTo reject it when it is over land; a
            // rejected destination just means this beat is a pause, which is indistinguishable
            // from a fish idling and costs nothing.
            if (_aquatic) MoveTo(_home + offset);
            else if (NavMesh.SamplePosition(_home + offset, out var hit, _homeRadius, NavMesh.AllAreas))
                MoveTo(hit.position);
        }

        private void EnterGraze()
        {
            _state = State.Graze;
            _stateTimer = _rng.Range(_grazeDurationRange.x, _grazeDurationRange.y);
            StopMoving();
        }

        private void EnterAlert()
        {
            _state = State.Alert;
            _stateTimer = _rng.Range(0.35f, 0.8f);
            SetSpeed(_alertSpeed);
            StopMoving();
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
            if (!_aquatic && _agent != null && _agent.isOnNavMesh) _agent.isStopped = false;
            if (propagateAlarm) PropagateAlarm();
        }

        private void EnterApproach()
        {
            _state = State.Approach;
            _stateTimer = 8f;
            SetSpeed(_chaseSpeed);
            if (!_aquatic && _agent != null && _agent.isOnNavMesh) _agent.isStopped = false;
        }

        private void EnterHold()
        {
            _state = State.Hold;
            _stateTimer = _rng.Range(2.5f, 5.5f);
            StopMoving();
        }

        private void EnterRetreat()
        {
            _state = State.Retreating;
            _stateTimer = 5f;
            _alertness = 0f;
            _cooldownTimer = 4f;
            SetSpeed(_wanderSpeed);
            MoveTo(_home);
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
            _moveSpeed = speed;
            if (!_aquatic && _agent != null) _agent.speed = speed;
        }

        // ---- Movement, over either a NavMeshAgent or open water --------------------------

        /// <summary>
        /// True when this creature can currently be told to go somewhere. A land roamer that has
        /// not landed on the navmesh silently ignores every destination, so asking first is what
        /// stops a creature standing still forever with no error to explain it.
        /// </summary>
        private bool CanMove => _aquatic || (_agent != null && _agent.isOnNavMesh);

        /// <summary>Metres per second actually being covered, for the animation choice.</summary>
        private float CurrentSpeed => _aquatic
            ? _swimVelocity.magnitude
            : (_agent != null ? _agent.velocity.magnitude : 0f);

        /// <summary>Metres still to travel, or zero when there is nowhere to go.</summary>
        private float RemainingDistance
        {
            get
            {
                if (_aquatic)
                    return _hasSwimTarget ? Vector3.Distance(transform.position, _swimTarget) : 0f;
                if (_agent == null || !_agent.isOnNavMesh || _agent.pathPending || !_agent.hasPath) return 0f;
                return _agent.remainingDistance;
            }
        }

        /// <summary>
        /// Sends the creature to a point. For a swimmer the point is accepted only if it is over
        /// water, which is the single rule that keeps a Magikarp in its lake.
        /// </summary>
        private void MoveTo(Vector3 destination)
        {
            if (_aquatic)
            {
                if (!WaterBody.TrySampleAnySurface(destination, out var surface)) return;
                _swimTarget = surface;
                _hasSwimTarget = true;
                return;
            }

            if (_agent == null || !_agent.isOnNavMesh) return;
            _agent.isStopped = false;
            _agent.SetDestination(destination);
        }

        private void StopMoving()
        {
            if (_aquatic)
            {
                _hasSwimTarget = false;
                return;
            }

            if (_agent == null || !_agent.isOnNavMesh) return;
            _agent.isStopped = true;
            _agent.ResetPath();
        }

        /// <summary>
        /// Hand-rolled steering for swimmers.
        ///
        /// Velocity is smoothed rather than applied directly so a fish does not reverse
        /// instantaneously when its target moves behind it, and the body is re-seated on the
        /// waterline every frame — a lake with a sloped bed or a surface that animates would
        /// otherwise let the sprite drift below the water it is supposed to be swimming on.
        /// </summary>
        private void TickSwim(float dt)
        {
            if (!_hasSwimTarget)
            {
                _swimVelocity = Vector3.MoveTowards(_swimVelocity, Vector3.zero, 4f * dt);
            }
            else
            {
                var toTarget = _swimTarget - transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude < 0.25f) _hasSwimTarget = false;
                var desired = toTarget.sqrMagnitude > 0.001f
                    ? toTarget.normalized * _moveSpeed
                    : Vector3.zero;
                _swimVelocity = Vector3.MoveTowards(_swimVelocity, desired, 6f * dt);
            }

            var next = transform.position + _swimVelocity * dt;

            // Refuse the step rather than correcting after it. Pushing a swimmer back once it has
            // already left the lake reads as it bouncing off an invisible wall; simply not taking
            // the step reads as it turning, which is what a fish in a pond actually does.
            if (WaterBody.TrySampleAnySurface(next, out var surface)) transform.position = surface;
            else
            {
                _swimVelocity = Vector3.zero;
                _hasSwimTarget = false;
            }

            if (_swimVelocity.sqrMagnitude > 0.01f)
            {
                var facing = new Vector3(_swimVelocity.x, 0f, _swimVelocity.z);
                if (facing.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.Slerp(transform.rotation,
                        Quaternion.LookRotation(facing), 1f - Mathf.Exp(-5f * dt));
            }
        }

        private void StartBattle()
        {
            var director = EncounterDirector.Instance;
            if (director == null || !IsAvailable) return;

            StopMoving();
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
            var speed = CurrentSpeed;
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
                _art?.Play(desired);
            }

            // Rigs that expose a plain Speed float still animate, even without an ICreatureView.
            if (_animator != null) _animator.SetFloat("Speed", speed);
        }

        // ---- Outcome hooks ---------------------------------------------------------------

        /// <summary>Caught or defeated. The creature leaves the world; the spawner refills later.</summary>
        public void Consume()
        {
            _state = State.Consumed;
            StopMoving();
            _swimVelocity = Vector3.zero;
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
