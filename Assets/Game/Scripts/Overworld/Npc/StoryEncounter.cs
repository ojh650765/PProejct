using System.Collections;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A character who starts the conversation themselves.
    ///
    /// Most people in a town wait to be spoken to, and that is right for them — a world
    /// where everyone accosts you is exhausting. Story characters are the exception, and
    /// the reason is structural rather than stylistic: the rival blocking the route and
    /// your friend catching you on the way out of town are how the plot advances, and a
    /// beat the player can walk past by not pressing the interact button is a beat that
    /// does not reliably happen. Pokémon has always handled these by taking control, and
    /// this is that.
    ///
    /// Deliberately not the same thing as <see cref="TrainerController"/>. A trainer spots
    /// you inside a sight cone, turns, walks over and fights; this fires on proximity
    /// regardless of facing, plays a conversation, and only then hands over to a battle if
    /// there is one. Sharing the code would mean every story beat inherited a sight cone
    /// the player could sidestep.
    /// </summary>
    /// <remarks>
    /// Carries no collider requirement, and must not gain one back. Proximity here is a
    /// distance test in <see cref="Update"/>, not a trigger callback — nothing in this file
    /// reads OnTriggerEnter. The requirement used to be here anyway, and its Awake forced
    /// <c>isTrigger</c> on whatever <c>GetComponent&lt;Collider&gt;</c> happened to return.
    /// Every story character in the generated town has exactly one collider, the capsule that
    /// is their body, so arming an encounter turned the rival into something the player walks
    /// straight through.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class StoryEncounter : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Flag set once this has fired. An encounter that repeats is not a story beat.")]
        [SerializeField] private string _completionFlag = "story.rival_route_01";

        [Tooltip("Flag that must already be set before this can fire. Empty means no gate. " +
                 "This is what keeps the rival from ambushing a player who has not yet been " +
                 "given a Pokémon to fight them with.")]
        [SerializeField] private string _requiresFlag = "";

        [Header("Trigger")]
        [Tooltip("Metres. Generous — the player should not be able to thread past a story beat.")]
        [SerializeField] private float _approachRadius = 5.5f;

        [Tooltip("Seconds the character walks toward the player before speaking. 0 speaks " +
                 "from where they stand.")]
        [SerializeField] private float _approachSeconds = 1.1f;

        [SerializeField] private float _approachSpeed = 3.6f;

        [Header("Content")]
        [Tooltip("What they say. A serialized asset rather than an id, matching DialogueTrigger " +
                 "— the id-to-sequence lookup over dialogue.json belongs to one place, and " +
                 "this is not it.")]
        [SerializeField] private DialogueSequence _sequence;

        [Tooltip("Trainer id to battle after the conversation. Empty means talk only — the " +
                 "friend who wishes you luck is the common case, not the exception.")]
        [SerializeField] private string _battleTrainerId = "";

        [Tooltip("Episode to play instead of a plain conversation, when this beat is a whole " +
                 "scene. Takes precedence over the dialogue id.")]
        [SerializeField] private string _episodeId = "";

        private Transform _player;
        private EpisodeRunner _runner;
        private bool _fired;
        private bool _running;

        /// <summary>False once a step has established that the beat did not actually play.</summary>
        private bool _spoke;

        private int _refusals;
        private const int MaxRefusals = 3;

        /// <summary>
        /// True while this character still owes the player a story beat.
        ///
        /// Read by <see cref="TrainerController"/> on the same GameObject, and only for that.
        /// Kes is both a story character and a trainer, and the two notice the player by
        /// different rules — this one on proximity, the other down a sight cone after a
        /// confirmation delay — so which of them wins is a race decided by the angle the player
        /// walks in at. Losing it means the rival battle happens through the trainer path, whose
        /// party is a placeholder species the episode was supposed to fill in: a fight against
        /// nothing, in place of the one the act is built around.
        /// </summary>
        public bool Pending => !_fired && !string.IsNullOrEmpty(_episodeId) && !AlreadySeen();

        /// <summary>
        /// Hangs a story beat on this character. Called by the level builder.
        ///
        /// Exists for the same reason <see cref="NpcController.Configure"/> does: the town is
        /// generated, so a beat wired by hand in the scene file is destroyed by the next
        /// rebuild. This is the only supported way to arm one.
        /// </summary>
        public void Configure(string episodeId, string requiresFlag, string completionFlag,
                              float approachRadius, float approachSeconds)
        {
            _episodeId = episodeId ?? "";
            _requiresFlag = requiresFlag ?? "";
            _completionFlag = completionFlag ?? "";
            if (approachRadius > 0f) _approachRadius = approachRadius;
            _approachSeconds = Mathf.Max(0f, approachSeconds);
        }

        private void Update()
        {
            if (_fired || _running) return;

            // Never on top of a scene that is already running. Two story characters standing
            // within a couple of metres of each other is normal staging — the professor and the
            // rival share the lab door — and without this the second one's Play() is refused,
            // Speak() falls through to a null sequence, and the beat marks itself complete
            // having shown the player nothing at all.
            if (_runner == null) _runner = FindFirstObjectByType<EpisodeRunner>();
            if (_runner != null && _runner.IsPlaying) return;

            // Nor on top of a hold that is not an episode. The gate walks the player back down
            // the ramp after Bram has refused them, and Kes' trigger sits in the square they are
            // being walked into — so without this the send-off fired at somebody who was not
            // driving, halfway through somebody else's scene.
            if (StoryInterlude.Active) return;

            if (_player == null)
            {
                var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
                if (found == null) return;
                _player = found.transform;
            }

            if (!GateOpen()) return;

            var flat = _player.position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > _approachRadius * _approachRadius) return;

            StartCoroutine(Run());
        }

        private static PlayerProfile ResolveProfile() =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) ? profile as PlayerProfile : null;

        private bool GateOpen()
        {
            if (string.IsNullOrEmpty(_requiresFlag)) return true;
            var profile = ResolveProfile();
            // No profile yet means the game is still booting, not that the gate is open.
            return profile != null && profile.GetFlagBool(_requiresFlag);
        }

        private bool AlreadySeen()
        {
            if (string.IsNullOrEmpty(_completionFlag)) return false;
            var profile = ResolveProfile();
            return profile != null && profile.GetFlagBool(_completionFlag);
        }

        private IEnumerator Run()
        {
            _running = true;

            if (AlreadySeen())
            {
                // Checked here rather than in Update so the lookup happens once on approach
                // instead of every frame for every story character in the scene.
                _fired = true;
                _running = false;
                yield break;
            }

            var input = FindFirstObjectByType<OverworldInputReader>();
            var rig = FindFirstObjectByType<OverworldCameraRig>();
            var hadControl = input != null && input.InputEnabled;

            try
            {
                if (input != null) input.InputEnabled = false;
                if (rig != null)
                {
                    rig.ControlEnabled = false;
                    // Turn the camera onto them. The player is about to lose control, and
                    // losing it while looking at nothing in particular reads as a freeze.
                    rig.LookToward(transform.position);
                }

                _spoke = true;
                yield return Approach();
                FaceThePlayer();
                yield return Speak();
                yield return Battle();

                // Only marked done if the beat actually happened. Marking it regardless is how
                // a refused episode becomes permanent: the flag says the scene has been seen,
                // so it can never be offered again, and the act it was meant to open is lost
                // for the rest of that save with nothing on screen to say so.
                if (_spoke && !string.IsNullOrEmpty(_completionFlag))
                    ResolveProfile()?.SetFlagBool(_completionFlag, true);
                _fired = _spoke;

                // A refusal re-arms the beat rather than losing it, but not forever. An episode
                // id that is simply not in the book is refused every frame the player stands
                // here, and a warning per frame buries whatever else the log was going to say.
                if (!_spoke && ++_refusals >= MaxRefusals)
                {
                    _fired = true;
                    Debug.LogError($"[Story] {name} gave up on episode '{_episodeId}' after " +
                                   $"{MaxRefusals} refusals. Whatever it was meant to open is now " +
                                   "unreachable on this save; check that the id is in episodes.json.",
                                   this);
                }
            }
            finally
            {
                // Returned even if a step threw or a system it needed was absent. A story
                // character who takes control and does not give it back has ended the game
                // more thoroughly than any crash.
                if (input != null) input.InputEnabled = hadControl;
                if (rig != null) rig.ControlEnabled = true;
                _running = false;
            }
        }

        /// <summary>Metres short of the player the approach stops at. Walking into the player's
        /// own capsule leaves two characters interpenetrating for the whole conversation.</summary>
        private const float ApproachStopDistance = 1.6f;

        /// <summary>
        /// Walks the character toward the player for the approach window.
        ///
        /// Through the NavMeshAgent when there is a usable one, and that is not a preference,
        /// it is the only version that works: a live agent keeps its own nextPosition and
        /// writes it back over the transform on the frame after any direct assignment — the
        /// exact failure EpisodeRunner.PlaceActor documents — so the old transform-only walk
        /// rubber-banded on every character that can path. The rival is a TrainerController,
        /// which <em>requires</em> an agent, so the one approach this component exists for
        /// (Kes closing on the player) moved a step and snapped back, every frame, for the
        /// whole window. The transform fallback stays for a character with no agent or one
        /// standing off the mesh, where a direct write is the only thing that moves them at
        /// all. No snap at the end on either path — the beat speaks from wherever the walk
        /// got to, which is why there is no agent.Warp here.
        /// </summary>
        private IEnumerator Approach()
        {
            if (_approachSeconds <= 0f || _player == null) yield break;

            var agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            var useAgent = agent != null && agent.enabled && agent.isOnNavMesh;

            // The agent's cruising settings belong to whoever owns it day-to-day — a trainer's
            // challenge walk, an NPC's schedule — so they are restored when the beat is done.
            var restoreSpeed = 0f;
            var restoreStop = 0f;
            if (useAgent)
            {
                restoreSpeed = agent.speed;
                restoreStop = agent.stoppingDistance;
                agent.speed = _approachSpeed;
                agent.stoppingDistance = ApproachStopDistance;
                agent.isStopped = false;
            }

            try
            {
                var elapsed = 0f;
                while (elapsed < _approachSeconds)
                {
                    var toPlayer = _player.position - transform.position;
                    toPlayer.y = 0f;

                    // Stop short — see ApproachStopDistance.
                    if (toPlayer.magnitude <= ApproachStopDistance) yield break;

                    if (useAgent)
                    {
                        // The destination chases the player, who may still be drifting to a
                        // stop; the agent turns itself, so nothing here touches the rotation.
                        agent.SetDestination(_player.position);
                    }
                    else
                    {
                        transform.position += toPlayer.normalized * (_approachSpeed * Time.deltaTime);
                        transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
                    }

                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }
            finally
            {
                if (useAgent && agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                    agent.speed = restoreSpeed;
                    agent.stoppingDistance = restoreStop;
                }
            }
        }

        private void FaceThePlayer()
        {
            if (_player == null) return;
            var toPlayer = _player.position - transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
        }

        private IEnumerator Speak()
        {
            if (!string.IsNullOrEmpty(_episodeId))
            {
                var runner = FindFirstObjectByType<EpisodeRunner>();
                if (runner != null && runner.Play(_episodeId))
                {
                    while (runner.IsPlaying) yield return null;

                    // Started is not the same as happened. The runner only sets an episode's
                    // completion flag when its beats genuinely ran to the end — an exception
                    // mid-scene leaves it unset — so a flag still unset here means the scene
                    // was cut short after Play() accepted it. Saying "did not happen" lets
                    // Run() re-offer the beat (bounded by the refusal cap) instead of retiring
                    // it on the strength of having tried, which is the same reasoning _spoke
                    // exists for on the refusal path. Only testable when this beat has a flag
                    // to test; the trigger table always gives it the episode's own.
                    if (!string.IsNullOrEmpty(_completionFlag) && !AlreadySeen()) _spoke = false;
                    yield break;
                }
                Debug.LogWarning($"[Story] Episode '{_episodeId}' could not be played for " +
                                 $"{name}; falling back to the dialogue.", this);
            }

            if (_sequence == null)
            {
                // Nothing was said. An encounter armed with only an episode id, whose episode
                // the runner refused, has no fallback — so say it did not happen rather than
                // letting Run() retire the beat on the strength of having tried.
                _spoke = false;
                yield break;
            }

            var dialogue = DialogueRunner.Instance;
            if (dialogue == null)
            {
                Debug.LogWarning($"[Story] {name} has nothing to speak through — there is no " +
                                 "DialogueRunner in the scene, so this beat passes in silence.",
                                 this);
                yield break;
            }

            var done = false;
            dialogue.Play(_sequence, gameObject, _ => done = true);

            // Bounded. A conversation that never reports finishing must not be the reason
            // the player can never move again.
            var elapsed = 0f;
            while (!done && elapsed < 120f)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            if (!done)
                Debug.LogWarning($"[Story] '{_sequence.SequenceId}' did not end; continuing.", this);
        }

        private IEnumerator Battle()
        {
            if (string.IsNullOrEmpty(_battleTrainerId)) yield break;

            var trainer = GetComponent<TrainerController>();
            if (trainer == null)
            {
                Debug.LogWarning($"[Story] {name} wants to battle as '{_battleTrainerId}' but " +
                                 "has no TrainerController to do it with.", this);
                yield break;
            }

            // The same entry point the player's own interact uses, so a story battle and a
            // challenged battle are one code path — a second one would drift.
            trainer.Interact(_player != null ? _player.gameObject : gameObject);
            yield return null;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, _approachRadius);
        }
    }
}
