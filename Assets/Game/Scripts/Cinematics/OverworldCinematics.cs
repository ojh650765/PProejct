using System.Collections;
using UnityEngine;
using Unity.Cinemachine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The camera work that happens outside battle: a trainer noticing you and walking over,
    /// conversation framing, the scanner being raised, and the beat where the player spots a
    /// creature in the grass.
    ///
    /// All four are short, and all four are the moments where an unauthored camera is most
    /// obvious — the player is in direct control right up until the instant they are not, so
    /// any snap at the hand-off is unmissable. Each sequence therefore takes the camera from
    /// the follow rig by priority and gives it back the same way, never by teleporting.
    ///
    /// The overworld worker owns movement and interaction; this class owns only the camera
    /// and the timing, and takes the transforms it needs as arguments.
    /// </summary>
    [DefaultExecutionOrder(-340)]
    public sealed class OverworldCinematics : MonoBehaviour
    {
        [Header("Scene wiring")]
        [Tooltip("The overworld follow rig. Its priority is restored at the end of every sequence.")]
        [SerializeField] private CinemachineCamera followCamera;
        [Tooltip("The player transform, for framing two-shots and reaction beats.")]
        [SerializeField] private Transform player;

        [Header("Conversation framing")]
        [Tooltip("Height above the ground the conversation camera looks at, in metres.")]
        [SerializeField] private float eyeHeight = 1.55f;
        [Tooltip("Distance from the midpoint of the two speakers.")]
        [SerializeField] private float conversationDistance = 2.6f;
        [Tooltip("Angle off the speakers' axis, in degrees. Zero is a flat, dull two-shot.")]
        [SerializeField] private float conversationAngle = 38f;
        [Tooltip("Field of view for conversation framing. Tighter than gameplay.")]
        [SerializeField] private float conversationFov = 40f;

        [Header("Scanner")]
        [Tooltip("Metres the camera moves in over the player's shoulder when the scanner is raised.")]
        [SerializeField] private float scannerPushIn = 0.85f;
        [Tooltip("Field of view while the scanner is up. Narrower reads as looking through a device.")]
        [SerializeField] private float scannerFov = 34f;
        [Tooltip("Seconds to raise and to lower. Lowering is slower — it is a release, not an action.")]
        [SerializeField] private Vector2 scannerRaiseLower = new Vector2(0.4f, 0.55f);

        [Header("Spotted")]
        [Tooltip("Seconds held on a creature the player has just spotted.")]
        [SerializeField] private float spottedHold = 0.8f;

        private CinemachineCamera _cinematicCamera;
        private Coroutine _running;
        private int _followPriorityBeforeTakeover = 100;

        /// <summary>
        /// True while an overworld cinematic owns the camera. Gate input on this.
        /// Derived from the camera's own state rather than from a flag, because a
        /// conversation is held open by the UI worker and has no routine running.
        /// </summary>
        public bool IsPlaying => _cinematicCamera != null
                                 && _cinematicCamera.gameObject.activeSelf
                                 && _cinematicCamera.Priority.Value > 0;

        /// <summary>Binds the follow rig this class hands the camera back to.</summary>
        public void Bind(CinemachineCamera follow, Transform playerTransform)
        {
            followCamera = follow;
            player = playerTransform;
        }

        private void Awake() => EnsureCamera();

        private void EnsureCamera()
        {
            if (_cinematicCamera != null) return;

            var go = new GameObject("CM_Overworld_Cinematic");
            go.transform.SetParent(transform, false);
            _cinematicCamera = go.AddComponent<CinemachineCamera>();
            _cinematicCamera.Lens = LensSettings.Default;

            // Deoccluded like the battle rig: an overworld two-shot is the easiest place in
            // the game to put a camera inside a tree.
            var deoccluder = go.AddComponent<CinemachineDeoccluder>();
            var avoid = deoccluder.AvoidObstacles;
            avoid.Enabled = true;
            avoid.CameraRadius = 0.3f;
            avoid.Strategy = CinemachineDeoccluder.ObstacleAvoidance.ResolutionStrategy.PreserveCameraHeight;
            avoid.Damping = 0.4f;
            avoid.SmoothingTime = 0.2f;
            deoccluder.AvoidObstacles = avoid;

            go.SetActive(false);
        }

        // --- Sequences ------------------------------------------------------------------------

        /// <summary>
        /// A trainer spots the player and approaches.
        ///
        /// The camera swings to catch the trainer at the moment they notice, then holds while
        /// they close the distance. It deliberately does not follow them the whole way: a
        /// camera that tracks an approaching character makes them appear stationary.
        /// </summary>
        /// <param name="trainer">The approaching trainer.</param>
        /// <param name="stopDistance">Metres from the player the trainer stops at.</param>
        /// <param name="walkSpeed">Metres per second. Drives the beat length, not the movement.</param>
        public IEnumerator TrainerApproach(Transform trainer, float stopDistance = 2.4f, float walkSpeed = 2.2f)
        {
            if (trainer == null || player == null) yield break;
            _running = CinematicRunner.Run(TrainerApproachRoutine(trainer, stopDistance, walkSpeed));
            yield return _running;
        }

        private IEnumerator TrainerApproachRoutine(Transform trainer, float stopDistance, float walkSpeed)
        {
            TakeOver();

            // The notice beat: a fast swing onto the trainer, tight.
            Vector3 toPlayer = player.position - trainer.position;
            toPlayer.y = 0f;
            Vector3 axis = toPlayer.sqrMagnitude < 1e-4f ? Vector3.forward : toPlayer.normalized;
            Vector3 lateral = Vector3.Cross(Vector3.up, axis);

            Vector3 noticePose = trainer.position + Vector3.up * eyeHeight - axis * 2.2f + lateral * 1.5f;
            yield return MoveCameraTo(noticePose, trainer.position + Vector3.up * eyeHeight, 0.5f, 42f, CinematicEase.OutCubic);

            CinematicHooks.HudBeat("trainer_spotted", 1f);
            yield return CinematicRunner.Wait(0.5f);

            // Hold while they walk. Duration derived from the real distance so the camera and
            // the trainer arrive together whatever the geography.
            float distance = Mathf.Max(0f, toPlayer.magnitude - stopDistance);
            float walkTime = Mathf.Clamp(distance / Mathf.Max(0.5f, walkSpeed), 0.6f, 4f);

            Vector3 midpoint = Vector3.Lerp(player.position, trainer.position, 0.5f) + Vector3.up * eyeHeight;
            Vector3 twoShot = ConversationPose(player.position, trainer.position);
            yield return MoveCameraTo(twoShot, midpoint, walkTime, conversationFov, CinematicEase.InOutCubic);

            Release();
        }

        /// <summary>
        /// Frames a conversation as a three-quarter two-shot. Holds until
        /// <see cref="EndConversation"/> is called, because dialogue length is the UI
        /// worker's business, not the camera's.
        /// </summary>
        public IEnumerator BeginConversation(Transform other, float duration = 0.6f)
        {
            if (other == null || player == null) yield break;
            TakeOver();

            Vector3 midpoint = Vector3.Lerp(player.position, other.position, 0.5f) + Vector3.up * eyeHeight;
            Vector3 pose = ConversationPose(player.position, other.position);
            yield return MoveCameraTo(pose, midpoint, duration, conversationFov, CinematicEase.InOutCubic);
        }

        /// <summary>Hands the camera back to the follow rig after a conversation.</summary>
        public IEnumerator EndConversation(float duration = 0.6f)
        {
            yield return CinematicRunner.Wait(duration * 0.25f);
            Release();
        }

        /// <summary>
        /// The pose for a two-shot: off the axis between the speakers, at eye height,
        /// far enough back that neither head is cropped.
        ///
        /// Offsetting by <see cref="conversationAngle"/> rather than sitting on the axis is
        /// the whole difference between a conversation and a police interview.
        /// </summary>
        private Vector3 ConversationPose(Vector3 a, Vector3 b)
        {
            Vector3 axis = b - a;
            axis.y = 0f;
            if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward;
            axis.Normalize();

            Vector3 midpoint = Vector3.Lerp(a, b, 0.5f);
            Vector3 offset = Quaternion.AngleAxis(90f - conversationAngle, Vector3.up) * axis;
            // Widen with the gap so a long conversation distance does not crop both heads.
            float distance = conversationDistance + Vector3.Distance(a, b) * 0.35f;
            return midpoint + offset * distance + Vector3.up * (eyeHeight + 0.25f);
        }

        /// <summary>
        /// The scanner-raise adjustment: a short push over the shoulder and a lens tighten,
        /// as if the player brought a device up to their eye. Reversed by
        /// <see cref="LowerScanner"/>, and deliberately slower on the way down.
        /// </summary>
        public IEnumerator RaiseScanner(Transform lookTarget = null)
        {
            if (player == null) yield break;
            TakeOver();

            Vector3 forward = player.forward;
            Vector3 lateral = player.right;
            Vector3 shoulder = player.position + Vector3.up * (eyeHeight + 0.1f)
                             - forward * (2.0f - scannerPushIn) + lateral * 0.55f;
            Vector3 aim = lookTarget != null
                ? lookTarget.position
                : player.position + Vector3.up * eyeHeight + forward * 6f;

            yield return MoveCameraTo(shoulder, aim, scannerRaiseLower.x, scannerFov, CinematicEase.OutCubic);
            CinematicHooks.HudBeat("scanner_raised", 1f);
        }

        /// <summary>Reverses <see cref="RaiseScanner"/> and returns the camera to the follow rig.</summary>
        public IEnumerator LowerScanner()
        {
            CinematicHooks.HudBeat("scanner_lowered", 1f);
            yield return CinematicRunner.Wait(scannerRaiseLower.y * 0.3f);
            Release();
        }

        /// <summary>
        /// The reaction beat when a creature is spotted in the world: a quick tilt onto it,
        /// a hold, and back. Framed on the creature's head anchor when it has a view, and on
        /// its transform plus an estimated height when it does not.
        /// </summary>
        public IEnumerator CreatureSpotted(Transform creature, float displayHeight = 0.8f)
        {
            if (creature == null || player == null) yield break;
            _running = CinematicRunner.Run(CreatureSpottedRoutine(creature, displayHeight));
            yield return _running;
        }

        private IEnumerator CreatureSpottedRoutine(Transform creature, float displayHeight)
        {
            TakeOver();

            Vector3 aim = creature.position + Vector3.up * Mathf.Max(0.25f, displayHeight * 0.8f);
            Vector3 toCreature = creature.position - player.position;
            toCreature.y = 0f;
            Vector3 axis = toCreature.sqrMagnitude < 1e-4f ? player.forward : toCreature.normalized;

            // Distance scales with the creature so a small one is not lost in the frame —
            // the same problem the battle rig solves, in miniature.
            float distance = 1.6f + displayHeight * 2.2f;
            Vector3 pose = creature.position - axis * distance
                         + Vector3.up * (displayHeight * 1.1f + 0.5f)
                         + Vector3.Cross(Vector3.up, axis) * (0.5f + displayHeight * 0.4f);

            float fov = BattleCameraRig.SolveFov(displayHeight, distance, 0.45f, new Vector2(30f, 55f));

            CinematicHooks.HudBeat("creature_spotted", 1f);
            yield return MoveCameraTo(pose, aim, 0.45f, fov, CinematicEase.OutCubic);
            yield return CinematicRunner.Wait(spottedHold);
            Release();
        }

        // --- Camera plumbing ---------------------------------------------------------------------

        /// <summary>
        /// Raises the cinematic camera above the follow rig, seeding it with the follow rig's
        /// current pose.
        ///
        /// Seeding is the important part. Raising a camera that is sitting at the origin makes
        /// the brain blend from wherever it was last left, which produces a long swoop across
        /// the map on the first frame of every overworld cinematic.
        /// </summary>
        private void TakeOver()
        {
            EnsureCamera();
            if (_cinematicCamera.gameObject.activeSelf) return;

            Camera output = Camera.main;
            if (output != null)
            {
                _cinematicCamera.transform.SetPositionAndRotation(output.transform.position, output.transform.rotation);
                _cinematicCamera.Lens.FieldOfView = output.fieldOfView;
            }

            if (followCamera != null) _followPriorityBeforeTakeover = followCamera.Priority.Value;
            _cinematicCamera.gameObject.SetActive(true);
            _cinematicCamera.Priority = Mathf.Max(_followPriorityBeforeTakeover + 20, 120);
        }

        /// <summary>
        /// Hands the camera back. The cinematic camera is left active for the length of the
        /// blend rather than being disabled immediately, because disabling it mid-blend
        /// removes the shot the brain is blending out of and produces a cut.
        /// </summary>
        private void Release()
        {
            if (_cinematicCamera == null) return;
            _cinematicCamera.Priority = 0;
            if (followCamera != null) followCamera.Priority = _followPriorityBeforeTakeover;
            _running = null;
            CinematicRunner.ForkDelayed(1.5f, DeactivateCinematicCamera());
        }

        private IEnumerator DeactivateCinematicCamera()
        {
            if (_cinematicCamera != null && _cinematicCamera.Priority.Value <= 0)
                _cinematicCamera.gameObject.SetActive(false);
            yield break;
        }

        /// <summary>
        /// Moves the cinematic camera from where it currently is to a target pose and lens.
        /// Position, rotation and field of view are eased together — animating the lens
        /// separately from the move is the most common source of a "zoom that arrives before
        /// the camera does" read.
        /// </summary>
        private IEnumerator MoveCameraTo(Vector3 position, Vector3 lookAt, float duration, float fov,
            CinematicEase.Function ease)
        {
            EnsureCamera();
            Transform t = _cinematicCamera.transform;
            Vector3 fromPos = t.position;
            Quaternion fromRot = t.rotation;
            float fromFov = _cinematicCamera.Lens.FieldOfView;

            yield return CinematicRunner.Tween(duration, ease, p =>
            {
                Vector3 pos = Vector3.Lerp(fromPos, position, p);
                t.position = pos;

                Vector3 direction = lookAt - pos;
                Quaternion target = direction.sqrMagnitude > 1e-5f
                    ? Quaternion.LookRotation(direction.normalized, Vector3.up)
                    : fromRot;
                t.rotation = Quaternion.Slerp(fromRot, target, p);

                _cinematicCamera.Lens.FieldOfView = Mathf.Lerp(fromFov, fov, p);
            });
        }
    }
}
