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
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
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
        private bool _fired;
        private bool _running;

        private void Reset()
        {
            var trigger = GetComponent<Collider>();
            if (trigger != null) trigger.isTrigger = true;
        }

        private void Awake()
        {
            // A trigger, not a solid: the character still needs their own collider so the
            // player cannot walk through them, and that is a separate one on the body.
            var trigger = GetComponent<Collider>();
            if (trigger != null) trigger.isTrigger = true;
        }

        private void Update()
        {
            if (_fired || _running) return;

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

                yield return Approach();
                FaceThePlayer();
                yield return Speak();
                yield return Battle();

                if (!string.IsNullOrEmpty(_completionFlag))
                    ResolveProfile()?.SetFlagBool(_completionFlag, true);
                _fired = true;
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

        private IEnumerator Approach()
        {
            if (_approachSeconds <= 0f || _player == null) yield break;

            var elapsed = 0f;
            while (elapsed < _approachSeconds)
            {
                var toPlayer = _player.position - transform.position;
                toPlayer.y = 0f;

                // Stop short. Walking into the player's own capsule leaves two characters
                // interpenetrating for the whole conversation.
                if (toPlayer.magnitude <= 1.6f) yield break;

                transform.position += toPlayer.normalized * (_approachSpeed * Time.deltaTime);
                transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
                elapsed += Time.deltaTime;
                yield return null;
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
                    yield break;
                }
                Debug.LogWarning($"[Story] Episode '{_episodeId}' could not be played for " +
                                 $"{name}; falling back to the dialogue.", this);
            }

            if (_sequence == null) yield break;

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
