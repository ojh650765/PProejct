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
    /// One line set an NPC switches to once a flag is set.
    ///
    /// A list rather than the single <c>_alternateFlag</c> that used to be here, because the
    /// characters this town needs have three states, not two: Bram refuses the ramp, then sends
    /// you to the lab, then opens it. Two fields cannot express three states, and the shape a
    /// third would have taken — another pair of fields — does not stop at three either.
    ///
    /// Tested in authoring order, so the most progressed state is listed first. Reversed, Bram
    /// tests "has a Pokémon" before "has the dex" and spends the rest of the game refusing to
    /// open a gate he has already opened.
    /// </summary>
    [Serializable]
    public struct NpcDialogueState
    {
        [Tooltip("Profile flag that selects this line set.")]
        public string Flag;

        [Tooltip("Sequence id in dialogue.json.")]
        public string SequenceId;
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

        [Header("Dialogue (asset)")]
        [Tooltip("A conversation authored as an asset. The right home for a one-off; leave it " +
                 "empty on a generated NPC and use the sequence ids below.")]
        [SerializeField] private DialogueSequence _defaultDialogue;
        [Tooltip("Played instead once the flag named below is set. Leave null to always use the default.")]
        [SerializeField] private DialogueSequence _alternateDialogue;
        [Tooltip("Flag that switches to the alternate dialogue. Empty disables the switch.")]
        [SerializeField] private string _alternateFlag = "";

        [Header("Dialogue (book)")]
        [Tooltip("Sequence id in dialogue.json, used when no asset is assigned. This is the path " +
                 "the generated town takes: the level is rebuilt from slice_layout.json, so " +
                 "nothing can drag a ScriptableObject onto these NPCs and keep it there.")]
        [SerializeField] private string _defaultSequenceId = "";
        [Tooltip("Flag-gated alternates, most progressed first. The first flag that is set wins; " +
                 "none set falls through to the default id above.")]
        [SerializeField] private List<NpcDialogueState> _dialogueStates = new List<NpcDialogueState>();
        [Tooltip("String table key, not the text. The prompt sits on the HUD next to a Korean " +
                 "dialogue box, so a literal typed here would be the one English word on screen.")]
        [SerializeField] private string _promptKey = "ui.talk";
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

        public string InteractionPrompt => Loc.Get(_promptKey);

        /// <summary>
        /// Deliberately asks whether there is *something to say* rather than building it. This
        /// is polled every frame by the interaction prompt, and <see cref="DialogueBook.Build"/>
        /// allocates a ScriptableObject per call — the previous shape would have leaked one
        /// object per frame for as long as the player stood in front of a townsperson.
        /// </summary>
        public bool CanInteract(GameObject instigator) =>
            !_talking && HasDialogue && (_dialogueRunner == null || !_dialogueRunner.IsPlaying);

        /// <summary>
        /// Applied by the level builder from the authored layout.
        ///
        /// These are private serialized fields because the inspector is their normal
        /// home, but the level is generated: six residents and four trainers are
        /// authored in slice_layout.json with positions, schedules and sight cones, and
        /// nothing was reading them. Hand-placing them once would be thrown away by the
        /// next regeneration.
        /// </summary>
        public void Configure(string npcId, string displayName, IReadOnlyList<NpcScheduleEntry> schedule)
        {
            if (!string.IsNullOrEmpty(npcId)) _npcId = npcId;
            if (!string.IsNullOrEmpty(displayName)) _displayName = displayName;
            if (schedule != null && schedule.Count > 0)
            {
                _schedule.Clear();
                _schedule.AddRange(schedule);
            }
        }

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();

            // Switched off before anything moves it, and back on once it is standing somewhere
            // valid.
            //
            // An agent complains the moment it is enabled — "Failed to create agent because it
            // is not close enough to the NavMesh" — and that happens during the component's own
            // enable, before any of our code has had a chance to put it somewhere better. The
            // snap below already knew how to fix the position; it simply always ran too late to
            // stop the error, and an agent that failed to create stays broken for the session.
            _agent.enabled = false;
            _agent.speed = _walkSpeed;
            _rng = new DeterministicRandom(DeterministicRandom.HashString(_npcId));

            // Sorting here rather than trusting authoring order is what makes "most recent entry
            // wins" a valid way to resolve the schedule at any hour.
            _schedule.Sort((a, b) => a.StartHour.CompareTo(b.StartHour));

            if (_animator == null) _animator = GetComponentInChildren<Animator>();
            CacheAnimatorParams();

            SnapToNavMesh();

            // Turned on, not restored.
            //
            // The level builder saves every authored agent switched off precisely so that
            // enabling one at a bad position cannot fail, and its log says this component
            // "places and enables them" — but this restored the saved state instead, which
            // is off. Every authored NPC therefore ran the whole session with a dead agent,
            // reported itself as not being on the navmesh, and stood still; the message
            // blamed a stale bake, so the bake was rebuilt and nothing changed.
            //
            // SnapToNavMesh switches it back off where there is genuinely no mesh to stand
            // on — an interior, the battle arena — so this is safe to do unconditionally.
            _agent.enabled = true;

            // Sampled again with the agent live, because Warp is the only thing that tells an
            // enabled agent where it is; moving the transform under a disabled one is what
            // makes the enable succeed, and this is what makes it agree afterwards.
            SnapToNavMesh();
        }

        /// <summary>
        /// Puts the agent on the navmesh before anything asks it to walk.
        ///
        /// The layout places people from the height field, and the bake carves around
        /// buildings, barrier volumes and props — so a position that is correct on the
        /// ground can still be a metre off the walkable surface. Unity's response is
        /// "Failed to create agent because it is not close enough to the NavMesh", after
        /// which the agent exists but every call on it throws, and the character stands
        /// still for the rest of the session looking like a placement bug.
        ///
        /// Warping to the nearest walkable point is better than either leaving them stuck
        /// or moving the layout: the layout is generated and would drift back, and a metre
        /// of correction is invisible where being frozen is not.
        /// </summary>
        private void SnapToNavMesh()
        {
            if (_agent == null) return;
            if (_agent.enabled && _agent.isOnNavMesh) return;

            if (NavMesh.SamplePosition(transform.position, out var hit, 3f, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                if (!_agent.enabled) return;        // placed; the enable will take it from here
                if (_agent.isOnNavMesh) return;
                _agent.Warp(hit.position);
                return;
            }

            // No navmesh anywhere in this scene — an interior or the battle arena, where an
            // agent is not merely misplaced but meaningless. Switching it off is quieter and
            // more honest than leaving one that throws on every call.
            if (!NavMesh.SamplePosition(transform.position, out _, 500f, NavMesh.AllAreas))
            {
                _agent.enabled = false;
                return;
            }

            // No navmesh within reach, so put them on the ground at least.
            //
            // Standing still is survivable; standing still three metres in the air is not — a
            // character hanging over the valley is the first thing anyone notices and it reads
            // as the whole game being broken. The navmesh is the better answer because it is
            // what walking needs, but when it is not there the ground still is, and a raycast
            // finds it. They will not move, and the warning below says so.
            if (Physics.Raycast(transform.position + Vector3.up * 4f, Vector3.down,
                                out var ground, 40f, ~0, QueryTriggerInteraction.Ignore))
            {
                var drop = transform.position.y - ground.point.y;
                transform.position = ground.point;
                if (drop > 0.25f)
                    Debug.LogWarning($"[Npc] '{name}' was {drop:F1} m above the ground and has " +
                                     "been set down on it. Their authored position has the wrong " +
                                     "height — check the height grid the layout sampled.", this);
            }

            // Nothing within three metres is a level fault, not a rounding error, so name
            // the character and where they are rather than letting them silently do nothing.
            Debug.LogWarning($"[Npc] '{name}' is at {transform.position} with no navmesh " +
                             "within 3 m. They are walled in — check the barrier volumes and " +
                             "props around them.", this);
            _agent.enabled = false;
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

        private bool _warnedOffMesh;

        /// <summary>Turns to the player when they are close. True when it did.</summary>
        private bool FacePlayerIfNear()
        {
            if (_player == null) return false;
            if (Vector3.Distance(_player.position, transform.position) > _noticePlayerDistance) return false;
            FacePlayer();
            return true;
        }

        private void TickIdle()
        {
            if (_activeEntry < 0 || _activeEntry >= _schedule.Count) return;

            // An agent that is not on the mesh has no remaining distance to ask about,
            // and asking throws once a frame forever. ApplyEntry already guarded this and
            // this did not, so an NPC standing anywhere the mesh does not reach filled
            // the console. It happens for a real reason -- the terrain is regenerated and
            // the mesh has to be rebuilt with it -- so say so once and stand still rather
            // than pretending to walk.
            if (!_agent.isOnNavMesh)
            {
                if (!_warnedOffMesh)
                {
                    _warnedOffMesh = true;
                    Debug.LogWarning($"[Npc] '{name}' is not on a NavMesh, so it will stand " +
                                     "still. Rebuild the level's navigation — the terrain is " +
                                     "generated, so a bake from before the last rebuild does " +
                                     "not cover it.", this);
                }
                FacePlayerIfNear();
                return;
            }
            _warnedOffMesh = false;

            var entry = _schedule[_activeEntry];
            var arrived = !_agent.pathPending && _agent.remainingDistance <= _arrivalTolerance;
            if (!arrived) return;

            if (FacePlayerIfNear()) return;

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

        /// <summary>
        /// The conversation authored as an asset, or null. The asset path wins over the book
        /// when both are set: an asset is something a person deliberately dropped on this one
        /// NPC, and a generated id is the default everybody gets.
        /// </summary>
        private DialogueSequence ResolveAsset()
        {
            if (!string.IsNullOrEmpty(_alternateFlag) && _alternateDialogue != null && Flag(_alternateFlag))
                return _alternateDialogue;
            return _defaultDialogue;
        }

        /// <summary>
        /// The sequence id this NPC would speak right now, or null.
        ///
        /// Falls back to the book's own binding table when nothing is set on the component,
        /// which is the case for every NPC in the generated town — <see cref="Configure"/> is
        /// handed a position and a schedule and has never had anything to say. Looking the
        /// binding up by <see cref="_npcId"/> means the writer's file decides who says what and
        /// the level file decides who stands where, and neither has to know about the other.
        /// </summary>
        private string ResolveSequenceId()
        {
            var states = _dialogueStates != null && _dialogueStates.Count > 0 ? null : Binding?.States;
            if (states != null)
            {
                for (var i = 0; i < states.Length; i++)
                {
                    if (string.IsNullOrEmpty(states[i].SequenceId) || string.IsNullOrEmpty(states[i].Flag)) continue;
                    if (Flag(states[i].Flag)) return states[i].SequenceId;
                }
            }
            else
            {
                for (var i = 0; i < _dialogueStates.Count; i++)
                {
                    var state = _dialogueStates[i];
                    if (string.IsNullOrEmpty(state.SequenceId) || string.IsNullOrEmpty(state.Flag)) continue;
                    if (Flag(state.Flag)) return state.SequenceId;
                }
            }

            var fallback = string.IsNullOrEmpty(_defaultSequenceId)
                ? Binding?.DefaultSequenceId
                : _defaultSequenceId;
            return string.IsNullOrEmpty(fallback) ? null : fallback;
        }

        private DialogueBookBinding Binding => DialogueBook.Shared.BindingFor(_npcId);

        private bool HasDialogue
        {
            get
            {
                if (ResolveAsset() != null) return true;
                var id = ResolveSequenceId();
                return id != null && DialogueBook.Shared.Has(id);
            }
        }

        private static bool Flag(string key) =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete
            && concrete.GetFlagBool(key);

        public void Interact(GameObject instigator)
        {
            if (_dialogueRunner == null) return;

            // Built here and not in CanInteract, and remembered so it can be destroyed: a
            // sequence out of the book is a fresh ScriptableObject that belongs to whoever
            // asked for it, and dropping the reference leaks one object per conversation.
            var dialogue = ResolveAsset();
            if (dialogue == null)
            {
                var id = ResolveSequenceId();
                if (id == null) return;
                dialogue = DialogueBook.Shared.Build(id, Substitute);
                if (dialogue == null)
                {
                    Debug.LogWarning($"[Npc] '{_npcId}' asks for sequence '{id}', which is not in " +
                                     $"{DialogueBook.DefaultPath}. They stand there saying nothing, " +
                                     "which is what a silent NPC looks like from the player's side.", this);
                    return;
                }
                _builtDialogue = dialogue;
            }

            _talking = true;
            if (_agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }

            if (!_dialogueRunner.Play(dialogue, gameObject, OnDialogueFinished)) OnDialogueFinished(null);
        }

        private DialogueSequence _builtDialogue;

        private static string Substitute(string text) =>
            string.IsNullOrEmpty(text) || text.IndexOf(PlayerToken, StringComparison.Ordinal) < 0
                ? text
                : text.Replace(PlayerToken,
                    ServiceHub.TryGet<IPlayerProfile>(out var p) && !string.IsNullOrEmpty(p?.TrainerName)
                        ? p.TrainerName
                        : "Player");

        private const string PlayerToken = "{PLAYER}";

        private void OnDialogueFinished(string sequenceId)
        {
            _talking = false;
            if (_agent.isOnNavMesh) _agent.isStopped = false;

            if (_builtDialogue != null)
            {
                Destroy(_builtDialogue);
                _builtDialogue = null;
            }

            if (!string.IsNullOrEmpty(sequenceId)
                && ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
            {
                concrete.SetFlagBool("dialogue_seen_" + sequenceId, true);
            }
        }

        private void OnDestroy()
        {
            // The runner may still be holding it, but the NPC is going away with the scene and
            // so is the conversation; leaving it alive is a hidden object nothing can reach.
            if (_builtDialogue != null) Destroy(_builtDialogue);
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
