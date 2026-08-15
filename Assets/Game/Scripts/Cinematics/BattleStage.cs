using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The physical layout of a battle: where each side's creature stands, where the
    /// trainers stand, and which way the field faces.
    ///
    /// Every position in the choreography is derived from this rather than hardcoded, so a
    /// cave arena and a lakeside arena can differ in scale and orientation without touching
    /// a line of presenter code. The marks are also what the transition director flies the
    /// camera between, so they must exist before the encounter intro starts.
    /// </summary>
    [DefaultExecutionOrder(-360)]
    public sealed class BattleStage : MonoBehaviour
    {
        [Header("Marks (created relative to this transform when empty)")]
        [Tooltip("Where the player's creature stands. Near side of frame.")]
        [SerializeField] private Transform playerCreatureMark;
        [Tooltip("Where the opponent's creature stands. Far side of frame.")]
        [SerializeField] private Transform opponentCreatureMark;
        [Tooltip("Where the player trainer stands to throw from.")]
        [SerializeField] private Transform playerTrainerMark;
        [Tooltip("Where the opposing trainer stands. Unused in wild encounters.")]
        [SerializeField] private Transform opponentTrainerMark;
        [Tooltip("Centre of the arena. Midpoint of the creature marks when empty.")]
        [SerializeField] private Transform stageCenter;

        [Header("Default layout (metres, used only to create missing marks)")]
        [Tooltip("Separation between the two creature marks along the stage axis.")]
        [SerializeField] private float creatureSeparation = 5.2f;
        [Tooltip("Lateral stagger, so the two creatures are not exactly in line with the camera.")]
        [SerializeField] private float lateralStagger = 1.15f;
        [Tooltip("How far behind its creature each trainer stands.")]
        [SerializeField] private float trainerSetback = 3.1f;

        [Tooltip("Rescale the arena to the creatures standing in it. Turn off for a hand-authored " +
                 "arena whose marks must stay exactly where they were placed.")]
        [SerializeField] private bool adaptLayoutToCreatureSizes = true;

        [Header("Views")]
        [Tooltip("Optional pre-placed creature views. Created on the marks when empty.")]
        [SerializeField] private CreatureView playerView;
        [SerializeField] private CreatureView opponentView;

        private bool _built;

        /// <summary>Horizontal direction from the player's mark to the opponent's.</summary>
        public Vector3 Axis
        {
            get
            {
                EnsureBuilt();
                Vector3 a = opponentCreatureMark.position - playerCreatureMark.position;
                a.y = 0f;
                return a.sqrMagnitude < 1e-4f ? transform.forward : a.normalized;
            }
        }

        /// <summary>Centre of the arena, used by the wide establishing shot.</summary>
        public Transform Center { get { EnsureBuilt(); return stageCenter; } }

        private void Awake() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            // The player mark is deliberately offset to the camera's left and the opponent to
            // its right. Placing them on a single line makes the far creature sit exactly
            // behind the near one on every over-the-shoulder shot.
            float half = creatureSeparation * 0.5f;
            playerCreatureMark = EnsureMark(playerCreatureMark, "Mark_PlayerCreature",
                new Vector3(-lateralStagger, 0f, -half));
            opponentCreatureMark = EnsureMark(opponentCreatureMark, "Mark_OpponentCreature",
                new Vector3(lateralStagger, 0f, half));
            playerTrainerMark = EnsureMark(playerTrainerMark, "Mark_PlayerTrainer",
                new Vector3(-lateralStagger * 1.25f, 0f, -half - trainerSetback));
            opponentTrainerMark = EnsureMark(opponentTrainerMark, "Mark_OpponentTrainer",
                new Vector3(lateralStagger * 1.25f, 0f, half + trainerSetback));

            if (stageCenter == null)
            {
                var go = new GameObject("Mark_StageCenter");
                stageCenter = go.transform;
                stageCenter.SetParent(transform, false);
                stageCenter.position = (playerCreatureMark.position + opponentCreatureMark.position) * 0.5f;
            }

            // Marks face each other, so anything parented to one inherits a sane forward.
            playerCreatureMark.rotation = LookAlong(opponentCreatureMark.position - playerCreatureMark.position);
            opponentCreatureMark.rotation = LookAlong(playerCreatureMark.position - opponentCreatureMark.position);
            playerTrainerMark.rotation = playerCreatureMark.rotation;
            opponentTrainerMark.rotation = opponentCreatureMark.rotation;

            playerView = EnsureView(playerView, playerCreatureMark, "CreatureView_Player");
            opponentView = EnsureView(opponentView, opponentCreatureMark, "CreatureView_Opponent");
        }

        private Transform EnsureMark(Transform existing, string markName, Vector3 localPosition)
        {
            if (existing != null) return existing;
            var go = new GameObject(markName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static CreatureView EnsureView(CreatureView existing, Transform mark, string viewName)
        {
            if (existing != null) return existing;
            var go = new GameObject(viewName);
            go.transform.SetParent(mark, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            return go.AddComponent<CreatureView>();
        }

        private static Quaternion LookAlong(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 1e-5f
                ? Quaternion.identity
                : Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        // --- Accessors ----------------------------------------------------------------------

        /// <summary>The creature view for a side. Never null once the stage has built.</summary>
        public CreatureView ViewOf(BattleSide side)
        {
            EnsureBuilt();
            return side == BattleSide.Player ? playerView : opponentView;
        }

        /// <summary>The mark a side's creature stands on.</summary>
        public Transform MarkOf(BattleSide side)
        {
            EnsureBuilt();
            return side == BattleSide.Player ? playerCreatureMark : opponentCreatureMark;
        }

        /// <summary>The mark a side's trainer stands on — the throw origin for balls.</summary>
        public Transform TrainerMarkOf(BattleSide side)
        {
            EnsureBuilt();
            return side == BattleSide.Player ? playerTrainerMark : opponentTrainerMark;
        }

        /// <summary>The side opposite the given one.</summary>
        public static BattleSide Opposite(BattleSide side)
            => side == BattleSide.Player ? BattleSide.Opponent : BattleSide.Player;

        /// <summary>
        /// Puts a side's view back on its mark and re-binds it to a creature. Used on
        /// send-out and on every switch, because a creature that lunged and was then swapped
        /// out would otherwise leave the replacement standing at the lunge position.
        /// </summary>
        public CreatureView Occupy(BattleSide side, CreatureInstance creature)
        {
            EnsureBuilt();
            var view = ViewOf(side);
            var mark = MarkOf(side);

            view.transform.SetParent(mark, false);
            view.transform.localPosition = Vector3.zero;
            view.transform.localRotation = Quaternion.identity;
            view.Motion.ResetLayer();
            view.Bind(creature);
            return view;
        }

        /// <summary>Turns both creatures to face each other over <paramref name="duration"/> seconds.</summary>
        public void FaceOff(float duration = 0.4f)
        {
            EnsureBuilt();
            playerView.FaceTowards(opponentCreatureMark.position, duration);
            opponentView.FaceTowards(playerCreatureMark.position, duration);
        }

        /// <summary>
        /// Moves the creature marks to suit the two creatures standing on them.
        ///
        /// A fixed arena separation cannot serve both ends of the size contract. Two 0.3 m
        /// creatures five metres apart are two specks at opposite corners of the frame, and
        /// no lens fixes that — an over-the-shoulder shot would need a 10-degree lens, which
        /// flattens the foreground creature into a cutout. Two 6 m creatures at the same
        /// separation are interpenetrating. So the field scales with its occupants, and the
        /// camera rig's framing solve then has something it can actually solve.
        ///
        /// Call after both sides are bound, then <see cref="BattleCameraRig.Retarget"/>.
        /// </summary>
        public void AdaptToCreatureSizes(float playerHeight, float opponentHeight)
        {
            EnsureBuilt();
            if (!adaptLayoutToCreatureSizes) return;

            float separation = SolveSeparation(playerHeight, opponentHeight);
            float half = separation * 0.5f;
            float stagger = Mathf.Clamp(separation * 0.22f, 0.35f, 2.4f);
            float setback = Mathf.Clamp(separation * 0.6f, 1.4f, 5f);

            playerCreatureMark.localPosition = new Vector3(-stagger, 0f, -half);
            opponentCreatureMark.localPosition = new Vector3(stagger, 0f, half);
            playerTrainerMark.localPosition = new Vector3(-stagger * 1.25f, 0f, -half - setback);
            opponentTrainerMark.localPosition = new Vector3(stagger * 1.25f, 0f, half + setback);
            stageCenter.position = (playerCreatureMark.position + opponentCreatureMark.position) * 0.5f;

            playerCreatureMark.rotation = LookAlong(opponentCreatureMark.position - playerCreatureMark.position);
            opponentCreatureMark.rotation = LookAlong(playerCreatureMark.position - opponentCreatureMark.position);
            playerTrainerMark.rotation = playerCreatureMark.rotation;
            opponentTrainerMark.rotation = opponentCreatureMark.rotation;
        }

        /// <summary>
        /// Distance between the creature marks for a given pair of sizes, in metres.
        /// Clamped at both ends so an absurd registry height cannot put the two creatures in
        /// the same place or on opposite sides of the map.
        /// </summary>
        public static float SolveSeparation(float playerHeight, float opponentHeight)
        {
            float a = Mathf.Clamp(playerHeight <= 0.01f ? 0.8f : playerHeight, 0.2f, 6f);
            float b = Mathf.Clamp(opponentHeight <= 0.01f ? 0.8f : opponentHeight, 0.2f, 6f);
            // Almost purely proportional. A large constant term is what breaks framing at
            // the small end: it dominates the distance for a 0.3 m creature, so the camera
            // ends up needing a telephoto lens to fill the frame and the shot flattens.
            float proportional = 2.6f * (a + b);

            // Second cap, driven by the *smaller* creature. Without it a 6 m creature facing
            // a 0.5 m one pushes them 17 m apart, and no lens can frame the small one from
            // behind the large one's shoulder. The pair closes up instead.
            float smallestCanReach = 3f + 9f * Mathf.Min(a, b);

            return Mathf.Clamp(Mathf.Min(proportional, smallestCanReach), 2.0f, 16f);
        }

        /// <summary>
        /// A point in the air above a mark, for ball arcs and burst effects.
        /// Scaled by the occupying creature's height so a large creature's ball does not
        /// appear to burst inside its chest.
        /// </summary>
        public Vector3 BurstPointOf(BattleSide side)
        {
            EnsureBuilt();
            var view = ViewOf(side);
            float h = view != null ? view.DisplayHeight : 0.8f;
            return MarkOf(side).position + Vector3.up * Mathf.Max(0.7f, h * 1.15f);
        }
    }
}
