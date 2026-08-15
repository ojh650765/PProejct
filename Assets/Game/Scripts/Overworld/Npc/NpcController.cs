using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>What an NPC does once it reaches a scheduled waypoint.</summary>
    public enum NpcActivity
    {
        Idle = 0,
        Wander = 1,
        Work = 2,
        Sit = 3,
        Sleep = 4,
        Watch = 5,
    }

    /// <summary>
    /// One entry in a daily routine: from <see cref="StartHour"/>, be at <see cref="Waypoint"/>
    /// doing <see cref="Activity"/>.
    /// </summary>
    [Serializable]
    public struct NpcScheduleEntry
    {
        [Range(0f, 24f)][Tooltip("Clock hour this entry takes over at.")]
        public float StartHour;

        [Tooltip("Where to be. Null means 'stay wherever you are'.")]
        public Transform Waypoint;

        public NpcActivity Activity;

        [Tooltip("Radius wandered around the waypoint when the activity is Wander.")]
        public float WanderRadius;
    }

    /// <summary>
    /// A townsperson with a daily routine.
    ///
    /// The routine is a sorted list of hours; the active entry is whichever one most recently
    /// started. That is deliberately simple — it means the NPC is in a sensible place immediately
    /// after a save load or a time scrub, with no need to replay the schedule forward. An
    /// event-driven schedule would put everyone in the wrong building after "sleep until morning".
    ///
    /// Idle behaviour on top of the routine is what stops the town looking like a waxwork: small
    /// weight shifts, glances at the player, and a wander radius that makes a "standing" NPC drift
    /// a couple of metres over a minute.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public sealed class NpcController : MonoBehaviour, IInteractable
    {
        [Header("Identity")]
        [SerializeField] private string _npcId = "npc_01";
        [SerializeField] private string _displayName = "Townsfolk";

        [Header("Dialogue")]
        [SerializeField] private DialogueSequence _defaultDialogue;
        [Tooltip("Played instead once the flag named below is set. Leave null to always use the default.")]
        [SerializeField] private DialogueSequence _alternateDialogue;
        [Tooltip("Flag that switches to the alternate dialogue. Empty disables the switch.")]
        [SerializeField] private string _alternateFlag = "";
        [SerializeField] private string _prompt = "Talk";
        [SerializeField] private DialogueRunner _dialogueRunner;

        [Header("Schedule")]
        [Tooltip("Entries are sorted by hour at Awake; authoring order does not matter.")]
        [SerializeField] private List<NpcScheduleEntry> _schedule = new List<NpcScheduleEntry>();
        [Tooltip("How close counts as 'arrived'.")]
        [SerializeField] private float _arrivalTolerance = 0.6f;
        [SerializeField] private float _walkSpeed = 1.4f;

        [Header("Idle behaviour")]
        [Tooltip("Turns to look at the player when they come within this distance.")]
        [SerializeField] private float _noticePlayerDistance = 4.5f;
        [Tooltip("Seconds between idle micro-actions — a glance, a shuffle.")]
        [SerializeField] private Vector2 _idleBeatRange = new Vector2(3f, 9f);
        [Tooltip("How far a 'standing' NPC may drift while idling. Small; this is fidget, not travel.")]
        [SerializeField] private float _idleDriftRadius = 1.5f;

        [Header("Animation")]
        [SerializeField] private Animator _animator;
        [SerializeField] private string _speedParam = "Speed";
        [SerializeField] private string _activityParam = "Activity";

        private NavMeshAgent _agent;
        private DayNightCycle _clock;
        private Transform _player;
        private DeterministicRandom _rng;
        private int _activeEntry = -1;
        private float _idleTimer;
        private bool _talking;
        private int _speedHash, _activityHash;
        private bool _hasSpeedParam, _hasActivityParam;

        public string NpcId => _npcId;
        public string DisplayName => _displayName;

        /// <summary>Activity the schedule currently prescribes.</summary>
        public NpcActivity CurrentActivity =>
            _activeEntry >= 0 && _activeEntry < _schedule.Count ? _schedule[_activeEntry].Activity : NpcActivity.Idle;

        public string InteractionPrompt => _prompt;

        public bool CanInteract(GameObject instigator) =>
            !_talking && ResolveDialogue() != null && (_dialogueRunner == null || !_dialogueRunner.IsPlaying);

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.speed = _walkSpeed;
            _rng = new DeterministicRandom(DeterministicRandom.HashString(_npcId));

            // Sorting here rather than trusting authoring order is what makes "most recent entry
            // wins" a valid way to resolve the schedule at any hour.
            _schedule.Sort((a, b) => a.StartHour.CompareTo(b.StartHour));

            if (_animator == null) _animator = GetComponentInChildren<Animator>();
            CacheAnimatorParams();
        }

        private void CacheAnimatorParams()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;
            var parameters = _animator.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == _speedParam) _hasSpeedParam = true;
                if (parameters[i].name == _activityParam) _hasActivityParam = true;
            }
            _speedHash = Animator.StringToHash(_speedParam);
            _activityHash = Animator.StringToHash(_activityParam);
        }

        private void Start()
        {
            _clock = DayNightCycle.Instance;
            if (_dialogueRunner == null) _dialogueRunner = DialogueRunner.Instance;

            var playerObject = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
            if (playerObject != null) _player = playerObject.transform;

            // Snap straight to the correct entry: an NPC that walks from its spawn point to the
            // bakery at boot looks like a bug, not like a routine.
            var entry = ResolveEntryIndex();
            if (entry >= 0) ApplyEntry(entry, warp: true);
        }

        private void Update()
        {
            if (_talking)
            {
                FacePlayer();
                DriveAnimator();
                return;
            }

            var entry = ResolveEntryIndex();
            if (entry != _activeEntry) ApplyEntry(entry, warp: false);

            TickIdle();
            DriveAnimator();
        }

        /// <summary>The entry whose start hour most recently passed, wrapping over midnight.</summary>
        private int ResolveEntryIndex()
        {
            if (_schedule.Count == 0) return -1;
            var hour = _clock != null ? _clock.Hour : 12f;

            var chosen = -1;
            for (var i = 0; i < _schedule.Count; i++)
                if (_schedule[i].StartHour <= hour) chosen = i;

            // Before the first entry of the day, the previous night's last entry is still running.
            return chosen >= 0 ? chosen : _schedule.Count - 1;
        }

        private void ApplyEntry(int index, bool warp)
        {
            _activeEntry = index;
            if (index < 0 || index >= _schedule.Count) return;

            var entry = _schedule[index];
            if (entry.Waypoint == null) return;

            if (warp)
            {
                if (NavMesh.SamplePosition(entry.Waypoint.position, out var hit, 4f, NavMesh.AllAreas))
                {
                    if (_agent.isOnNavMesh) _agent.Warp(hit.position);
                    else transform.position = hit.position;
                }
                transform.rotation = entry.Waypoint.rotation;
                return;
            }

            if (_agent.isOnNavMesh)
            {
                _agent.isStopped = false;
                _agent.SetDestination(entry.Waypoint.position);
            }
        }

        private void TickIdle()
        {
            if (_activeEntry < 0 || _activeEntry >= _schedule.Count) return;
            var entry = _schedule[_activeEntry];

            var arrived = !_agent.pathPending && _agent.remainingDistance <= _arrivalTolerance;
            if (!arrived) return;

            if (_player != null && Vector3.Distance(_player.position, transform.position) <= _noticePlayerDistance)
            {
                FacePlayer();
                return;
            }

            _idleTimer -= Time.deltaTime;
            if (_idleTimer > 0f) return;
            _idleTimer = _rng.Range(_idleBeatRange.x, _idleBeatRange.y);

            var radius = entry.Activity == NpcActivity.Wander
                ? Mathf.Max(entry.WanderRadius, 1f)
                : _idleDriftRadius;

            if (entry.Activity == NpcActivity.Sit || entry.Activity == NpcActivity.Sleep) return;

            var anchor = entry.Waypoint != null ? entry.Waypoint.position : transform.position;
            var offset = new Vector3(_rng.Range(-1f, 1f), 0f, _rng.Range(-1f, 1f)).normalized * _rng.Range(0.3f, radius);
            if (NavMesh.SamplePosition(anchor + offset, out var hit, radius, NavMesh.AllAreas) && _agent.isOnNavMesh)
                _agent.SetDestination(hit.position);
        }

        private void FacePlayer()
        {
            if (_player == null) return;
            var direction = Vector3.ProjectOnPlane(_player.position - transform.position, Vector3.up);
            if (direction.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(direction), 1f - Mathf.Exp(-6f * Time.deltaTime));
        }

        private void DriveAnimator()
        {
            if (_animator == null) return;
            if (_hasSpeedParam) _animator.SetFloat(_speedHash, _agent.velocity.magnitude);
            if (_hasActivityParam) _animator.SetInteger(_activityHash, (int)CurrentActivity);
        }

        private DialogueSequence ResolveDialogue()
        {
            if (!string.IsNullOrEmpty(_alternateFlag) && _alternateDialogue != null
                && ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete
                && concrete.GetFlagBool(_alternateFlag))
            {
                return _alternateDialogue;
            }
            return _defaultDialogue;
        }

        public void Interact(GameObject instigator)
        {
            var dialogue = ResolveDialogue();
            if (dialogue == null || _dialogueRunner == null) return;

            _talking = true;
            if (_agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }

            if (!_dialogueRunner.Play(dialogue, gameObject, OnDialogueFinished)) OnDialogueFinished(null);
        }

        private void OnDialogueFinished(string sequenceId)
        {
            _talking = false;
            if (_agent.isOnNavMesh) _agent.isStopped = false;

            if (!string.IsNullOrEmpty(sequenceId)
                && ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
            {
                concrete.SetFlagBool("dialogue_seen_" + sequenceId, true);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.8f);
            for (var i = 0; i < _schedule.Count; i++)
            {
                if (_schedule[i].Waypoint == null) continue;
                Gizmos.DrawWireSphere(_schedule[i].Waypoint.position, 0.4f);
                Gizmos.DrawLine(transform.position, _schedule[i].Waypoint.position);
            }
        }
    }
}
