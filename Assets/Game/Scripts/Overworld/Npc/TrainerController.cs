using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A trainer who watches a stretch of route, spots the player, walks over and challenges them.
    ///
    /// The spot is a three-stage check — range, then a vision cone, then a line-of-sight raycast —
    /// so a trainer never notices you through a rock. Once spotted, the sequence is authored
    /// rather than instant: exclamation, approach walk, dialogue, battle. Every stage has a hook
    /// for the VFX, audio and UI workers, and none of them are required for it to function.
    ///
    /// Defeat is persisted as a flag on the profile, so a beaten trainer stays beaten across a
    /// reload, and rematches are gated on an in-game cooldown rather than on a real-time one.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public sealed class TrainerController : MonoBehaviour, IInteractable
    {
        [Header("Data")]
        [SerializeField] private TrainerDefinition _definition;

        [Header("Sight")]
        [Tooltip("How far down the route the trainer watches.")]
        [SerializeField] private float _sightRange = 12f;
        [Tooltip("Half-angle of the vision cone in degrees. Narrow reads as 'watching the path'.")]
        [SerializeField] private float _sightHalfAngle = 22f;
        [Tooltip("Geometry that blocks the trainer's view.")]
        [SerializeField] private LayerMask _sightBlockers;
        [Tooltip("Eye height the line-of-sight check is cast from.")]
        [SerializeField] private float _eyeHeight = 1.6f;
        [Tooltip("Seconds the player must stay in the cone before the trainer commits. Small, but "
                 + "enough that clipping the edge of the cone does not trigger a challenge.")]
        [SerializeField] private float _confirmSeconds = 0.2f;
        [Tooltip("Trainers only watch during these phases. Some go home at night.")]
        [SerializeField] private TimeOfDayMask _activeTimes = TimeOfDayMask.Any;

        [Header("Approach")]
        [Tooltip("How close the trainer walks before speaking.")]
        [SerializeField] private float _stopDistance = 2.2f;
        [SerializeField] private float _approachSpeed = 3.4f;
        [Tooltip("Seconds the exclamation beat holds before the trainer starts walking.")]
        [SerializeField] private float _exclamationSeconds = 0.9f;
        [Tooltip("Give up and battle from where they are after this long. Stops a blocked path "
                 + "from stranding the player in a frozen state forever.")]
        [SerializeField] private float _approachTimeout = 6f;

        [Header("Wiring")]
        [SerializeField] private DialogueRunner _dialogueRunner;
        [SerializeField] private OverworldCameraRig _cameraRig;
        [SerializeField] private PlayerLocomotion _player;
        [SerializeField] private OverworldInputReader _input;

        [Header("Animation")]
        [SerializeField] private Animator _animator;
        [SerializeField] private string _speedParam = "Speed";

        [Header("Presentation hooks")]
        [Tooltip("Wire the exclamation mark VFX and sting here.")]
        [SerializeField] private StringEvent _spotted = new StringEvent();
        [SerializeField] private StringEvent _battleStarting = new StringEvent();

        private NavMeshAgent _agent;
        private DayNightCycle _clock;
        private Transform _playerTransform;
        private Quaternion _restRotation;
        private Vector3 _restPosition;
        private float _confirmTimer;
        private bool _engaged;
        private bool _sequenceRunning;
        private int _speedHash;
        private bool _hasSpeedParam;

        /// <summary>Id written into <see cref="EncounterRequest.TrainerId"/>.</summary>
        public string TrainerId => _definition != null ? _definition.TrainerId : name;

        public TrainerDefinition Definition => _definition;

        /// <summary>True once the player has beaten them at least once.</summary>
        public bool IsDefeated => Profile != null && Profile.GetFlagBool(DefeatFlagKey);

        private string DefeatFlagKey => _definition != null ? _definition.DefeatFlag : "trainer_defeated_" + name;

        private PlayerProfile Profile =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) ? profile as PlayerProfile : null;

        public string InteractionPrompt => Loc.Get(IsDefeated && CanRematch() ? "ui.rematch" : "ui.talk");

        public bool CanInteract(GameObject instigator) =>
            !_sequenceRunning && _definition != null && !StandsDown;

        /// <summary>
        /// Whether this trainer must ignore the player entirely — no notice, no prompt, no fight.
        ///
        /// Two reasons, and they are both about a fight happening before the game can hold one.
        ///
        /// The prologue is the first: until the Pokédex is granted the player may have no party
        /// at all, and a trainer who challenges an empty party opens a battle that cannot be
        /// fought and cannot be left. See <see cref="StoryPhase"/> for what else stands down with
        /// it and why they lift together.
        ///
        /// A story beat this same character still owes is the second. Kes is a trainer and a
        /// story character at once, and his rival battle is built by an episode — it is the only
        /// thing in the project that can field the counter to the player's starter, because
        /// <see cref="TrainerDefinition"/> has no notion of one and carries a placeholder species
        /// in its place. Whichever of the two noticing rules fires first wins, so without this
        /// the fight is decided by the angle the player walks in at, and half the time it is a
        /// battle against species id zero.
        /// </summary>
        private bool StandsDown
        {
            get
            {
                if (StoryPhase.TrainerChallengesSuppressed) return true;
                var beat = GetComponent<StoryEncounter>();
                return beat != null && beat.Pending;
            }
        }

        /// <summary>
        /// Applied by the level builder from the authored layout.
        ///
        /// The sight cone is per-trainer in the design and it matters: the youngster's
        /// 9 m / 22 deg cone "crosses the road from the south verge, between the two
        /// tall-grass fields", which is a composed encounter, not a default.
        /// </summary>
        public void Configure(string trainerId, float sightRange, float sightHalfAngle)
        {
            // The id is the object's name when no definition is assigned — see TrainerId
            // above — so naming it is how the layout's trainerId reaches
            // EncounterRequest. A definition asset, once one exists, wins over this.
            // Built from the trainer table when nothing was authored, which on a generated
            // level is always. Without a definition CanInteract is false and the trainer
            // cannot be challenged at all — they stand there with a sight cone and no fight
            // behind it.
            if (_definition == null && !string.IsNullOrEmpty(trainerId))
                _definition = TrainerBook.Shared?.Build(trainerId);
            if (_definition == null && !string.IsNullOrEmpty(name))
                _definition = TrainerBook.Shared?.Build(name);

            if (!string.IsNullOrEmpty(trainerId) && _definition == null) gameObject.name = trainerId;
            if (sightRange > 0f) _sightRange = sightRange;
            if (sightHalfAngle > 0f) _sightHalfAngle = sightHalfAngle;
        }

        private void Awake()
        {
            // The definition does not reliably survive the trip through the scene file. The
            // build-time Configure creates it with HideAndDontSave — the flag that keeps a
            // runtime object OUT of the saved scene — so a scene that has been saved and
            // reloaded (or built) can wake up with the serialized field empty and a trainer
            // who cannot be challenged, whose party the battle side cannot resolve, and whose
            // dialogue never plays. Re-resolving from the table here makes the definition a
            // runtime fact rather than an editor-time one: the builder names each trainer
            // either by its layout name (which the table indexes through the row's
            // sceneObject) or, when the build-time lookup failed, by the trainer id itself,
            // so this object's own name reaches the row by either handle.
            if (_definition == null) _definition = TrainerBook.Shared?.Build(name);

            _agent = GetComponent<NavMeshAgent>();
            _restRotation = transform.rotation;
            _restPosition = transform.position;
            if (_animator == null) _animator = GetComponentInChildren<Animator>();

            if (_animator != null && _animator.runtimeAnimatorController != null)
            {
                var parameters = _animator.parameters;
                for (var i = 0; i < parameters.Length; i++)
                    if (parameters[i].name == _speedParam) _hasSpeedParam = true;
                _speedHash = Animator.StringToHash(_speedParam);
            }
        }

        private void OnEnable() => TrainerRegistry.Register(_definition);

        private void Start()
        {
            _clock = DayNightCycle.Instance;
            if (_dialogueRunner == null) _dialogueRunner = DialogueRunner.Instance;

            var playerObject = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
            if (playerObject != null)
            {
                _playerTransform = playerObject.transform;
                if (_player == null) _player = playerObject.GetComponent<PlayerLocomotion>();
                if (_input == null) _input = playerObject.GetComponent<OverworldInputReader>();
            }
        }

        private void Update()
        {
            if (_animator != null && _hasSpeedParam) _animator.SetFloat(_speedHash, _agent.velocity.magnitude);

            if (_sequenceRunning || _engaged) return;
            if (_definition == null || _playerTransform == null) return;
            if (IsDefeated) return; // a beaten trainer no longer ambushes; they can still be talked to
            if (StandsDown) return;
            if (!IsActiveNow()) return;

            if (CanSeePlayer())
            {
                _confirmTimer += Time.deltaTime;
                if (_confirmTimer >= _confirmSeconds) StartCoroutine(RunChallenge(isRematch: false));
            }
            else
            {
                _confirmTimer = 0f;
            }
        }

        private bool IsActiveNow()
        {
            if (_activeTimes == TimeOfDayMask.Any) return true;
            var phase = _clock != null ? _clock.Phase : TimeOfDay.Day;
            return (_activeTimes & phase.ToMask()) != 0;
        }

        /// <summary>Range, then cone, then line of sight — cheapest test first.</summary>
        private bool CanSeePlayer()
        {
            var toPlayer = _playerTransform.position - transform.position;
            var distance = toPlayer.magnitude;
            if (distance > _sightRange) return false;

            var planar = Vector3.ProjectOnPlane(toPlayer, Vector3.up);
            if (planar.sqrMagnitude < 0.0001f) return true;
            if (Vector3.Angle(transform.forward, planar) > _sightHalfAngle) return false;

            if (_sightBlockers.value != 0)
            {
                var eye = transform.position + Vector3.up * _eyeHeight;
                var target = _playerTransform.position + Vector3.up * 1.0f;
                if (Physics.Linecast(eye, target, _sightBlockers, QueryTriggerInteraction.Ignore)) return false;
            }

            return true;
        }

        public void Interact(GameObject instigator)
        {
            if (_sequenceRunning || _definition == null) return;
            // Also tested here and not only in CanInteract: StoryEncounter.Battle() calls this
            // directly, and so would anything else that reaches a trainer without going through
            // the interaction prompt.
            if (StandsDown) return;

            if (IsDefeated)
            {
                if (CanRematch()) StartCoroutine(RunChallenge(isRematch: true));
                else PlayIdleChatter();
                return;
            }

            StartCoroutine(RunChallenge(isRematch: false));
        }

        private void PlayIdleChatter()
        {
            var sequence = _definition.AfterDefeat;
            if (sequence != null && _dialogueRunner != null) _dialogueRunner.Play(sequence, gameObject);
        }

        /// <summary>Rematch gating: allowed, and enough in-game hours since the last defeat.</summary>
        public bool CanRematch()
        {
            if (_definition == null || !_definition.AllowsRematch) return false;
            var profile = Profile;
            if (profile == null || _clock == null) return _definition.AllowsRematch;

            var lastHour = profile.GetFlagInt(_definition.LastDefeatHourFlag, int.MinValue);
            if (lastHour == int.MinValue) return true;

            // Hours are accumulated across days by the caller, so this is a simple difference.
            var elapsed = TotalHours() - lastHour;
            return elapsed >= _definition.RematchCooldownHours;
        }

        private int TotalHours()
        {
            if (_clock == null) return 0;
            // Play time gives a monotonically increasing hour count; the clock alone wraps daily
            // and would make a cooldown expire the moment midnight passed.
            var profile = Profile;
            var playHours = profile != null ? profile.PlayTimeSeconds / 3600f : 0f;
            return Mathf.FloorToInt(_clock.Hour + playHours * 24f);
        }

        private IEnumerator RunChallenge(bool isRematch)
        {
            _sequenceRunning = true;
            _engaged = true;
            _confirmTimer = 0f;

            // 1. Notice. Freeze the player here so the challenge cannot be walked out of, and let
            //    the camera turn — never cut — toward the trainer.
            FreezePlayer(true);
            OverworldEvents.RaiseTrainerSpotted(TrainerId);
            _spotted.Invoke(TrainerId);
            if (_cameraRig != null) _cameraRig.LookToward(transform.position);

            if (_exclamationSeconds > 0f) yield return new WaitForSeconds(_exclamationSeconds);

            // 2. Approach. Timed out rather than trusted, so blocked geometry cannot strand us.
            yield return ApproachPlayer();

            // 3. Challenge dialogue, then the battle.
            var intro = isRematch && _definition.RematchIntro != null
                ? _definition.RematchIntro
                : _definition.PreBattle;

            if (intro != null && _dialogueRunner != null)
            {
                _dialogueRunner.Play(intro, gameObject);
                while (_dialogueRunner.IsPlaying) yield return null;
            }

            _battleStarting.Invoke(TrainerId);

            var director = EncounterDirector.Instance;
            if (director != null)
            {
                director.TriggerTrainerEncounter(this);
            }
            else
            {
                Debug.LogWarning("[TrainerController] No EncounterDirector; challenge abandoned.", this);
                NotifyBattleResolved(BattleOutcome.Fled);
            }
        }

        private IEnumerator ApproachPlayer()
        {
            if (_playerTransform == null || _agent == null || !_agent.isOnNavMesh) yield break;

            _agent.speed = _approachSpeed;
            _agent.isStopped = false;
            _agent.stoppingDistance = _stopDistance;

            var elapsed = 0f;
            while (elapsed < _approachTimeout)
            {
                elapsed += Time.deltaTime;
                var distance = Vector3.Distance(transform.position, _playerTransform.position);
                if (distance <= _stopDistance + 0.15f) break;
                _agent.SetDestination(_playerTransform.position);
                yield return null;
            }

            _agent.isStopped = true;
            _agent.ResetPath();

            // Face the player before speaking; talking to someone's shoulder reads as broken.
            var lookDirection = Vector3.ProjectOnPlane(_playerTransform.position - transform.position, Vector3.up);
            if (lookDirection.sqrMagnitude > 0.001f)
            {
                var target = Quaternion.LookRotation(lookDirection);
                var turnElapsed = 0f;
                while (turnElapsed < 0.35f)
                {
                    turnElapsed += Time.deltaTime;
                    transform.rotation = Quaternion.Slerp(transform.rotation, target, turnElapsed / 0.35f);
                    yield return null;
                }
                transform.rotation = target;
            }
        }

        /// <summary>
        /// Called by <see cref="EncounterDirector"/> once the battle has resolved and the player is
        /// back in the overworld. Records the defeat and plays the closing line.
        /// </summary>
        public void NotifyBattleResolved(BattleOutcome outcome)
        {
            _sequenceRunning = false;
            _engaged = false;
            FreezePlayer(false);

            var profile = Profile;
            if (outcome == BattleOutcome.PlayerVictory && profile != null && _definition != null)
            {
                profile.SetFlagBool(_definition.DefeatFlag, true);
                profile.SetFlagInt(_definition.WinCountFlag, profile.GetFlagInt(_definition.WinCountFlag) + 1);
                profile.SetFlagInt(_definition.LastDefeatHourFlag, TotalHours());
            }

            var closing = outcome == BattleOutcome.PlayerVictory
                ? _definition?.OnDefeat
                : _definition?.OnPlayerDefeat;

            if (closing != null && _dialogueRunner != null && !_dialogueRunner.IsPlaying)
                _dialogueRunner.Play(closing, gameObject, _ => ReturnToPost());
            else
                ReturnToPost();
        }

        /// <summary>Walks back to where they were standing, so the route looks the same next time.</summary>
        private void ReturnToPost()
        {
            if (_agent == null || !_agent.isOnNavMesh) return;
            _agent.isStopped = false;
            _agent.stoppingDistance = 0.2f;
            _agent.SetDestination(_restPosition);
            StartCoroutine(RestoreFacing());
        }

        private IEnumerator RestoreFacing()
        {
            while (_agent.pathPending || _agent.remainingDistance > 0.35f) yield return null;
            _agent.isStopped = true;

            var elapsed = 0f;
            var start = transform.rotation;
            while (elapsed < 0.5f)
            {
                elapsed += Time.deltaTime;
                transform.rotation = Quaternion.Slerp(start, _restRotation, elapsed / 0.5f);
                yield return null;
            }
            transform.rotation = _restRotation;
        }

        private void FreezePlayer(bool frozen)
        {
            if (_player != null) _player.SetMotionFrozen(frozen);
            if (_input != null) _input.InputEnabled = !frozen;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.9f);
            var forward = transform.forward * _sightRange;
            var left = Quaternion.Euler(0f, -_sightHalfAngle, 0f) * forward;
            var right = Quaternion.Euler(0f, _sightHalfAngle, 0f) * forward;
            var eye = transform.position + Vector3.up * _eyeHeight;
            Gizmos.DrawRay(eye, left);
            Gizmos.DrawRay(eye, right);
            Gizmos.DrawRay(eye, forward);
        }
    }
}
