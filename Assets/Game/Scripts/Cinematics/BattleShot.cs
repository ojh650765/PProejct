using System;
using UnityEngine;
using Unity.Cinemachine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The battle rig's shot vocabulary. One Cinemachine camera exists per value and the
    /// presenter selects between them by raising priority — it never repositions a live
    /// camera, so every change of shot goes through an authored blend.
    /// </summary>
    public enum BattleShot
    {
        /// <summary>No shot forced; the rig falls back to its resting shot.</summary>
        None = 0,
        /// <summary>Both combatants in frame across the stage axis. The resting shot.</summary>
        WideEstablishing = 1,
        /// <summary>Behind the player's creature, looking at the opponent.</summary>
        PlayerOverShoulder = 2,
        /// <summary>Behind the opponent's creature, looking at the player's. Used when the opponent acts.</summary>
        OpponentOverShoulder = 3,
        /// <summary>Tight on whoever is attacking, during anticipation and release.</summary>
        AttackerCloseUp = 4,
        /// <summary>Tight on whoever is being hit, held through the recoil.</summary>
        TargetReaction = 5,
        /// <summary>Very tight, very fast. Snaps in on the frame of contact and is held briefly.</summary>
        ImpactPunchIn = 6,
        /// <summary>Low and wide, tracking the ball arc and the creature landing.</summary>
        SendOut = 7,
        /// <summary>Low angle on the collapsing creature, held for the beat.</summary>
        Faint = 8,
        /// <summary>Locked on the ball for the absorb, the fall and the shakes.</summary>
        Capture = 9,
        /// <summary>Rising three-quarter on the winner.</summary>
        Victory = 10,
    }

    /// <summary>Which combatant a shot is framed around, resolved at retarget time.</summary>
    public enum ShotSubject
    {
        /// <summary>Framed on the stage as a whole.</summary>
        Stage = 0,
        /// <summary>Framed on whichever side is currently acting.</summary>
        Actor = 1,
        /// <summary>Framed on whichever side is currently being acted upon.</summary>
        Receiver = 2,
        /// <summary>Always the player's creature.</summary>
        Player = 3,
        /// <summary>Always the opponent's creature.</summary>
        Opponent = 4,
    }

    /// <summary>
    /// Per-shot framing rules, expressed in units of the subject's display height rather
    /// than in metres.
    ///
    /// This is the whole answer to "a 0.3 m Pidgey and a 1.3 m Gastly must both frame well
    /// through the same rig": absolute offsets can only be tuned for one creature size, so
    /// every distance here is <c>Constant + PerHeight * subjectHeight</c>, and the lens is
    /// then solved so the subject occupies a fixed fraction of the screen regardless of how
    /// that arithmetic came out.
    /// </summary>
    [Serializable]
    public struct ShotProfile
    {
        [Tooltip("Which shot these values configure.")]
        public BattleShot Shot;

        [Tooltip("Who the camera is placed relative to.")]
        public ShotSubject Anchor;

        [Tooltip("Who the camera looks at.")]
        public ShotSubject Target;

        [Header("Placement (metres = Constant + PerHeight * subjectHeight)")]
        [Tooltip("Distance back along the stage axis, away from the target.")]
        public Vector2 Back;
        [Tooltip("Height above the anchor's feet.")]
        public Vector2 Up;
        [Tooltip("Lateral offset across the stage axis. Positive is the camera's right.")]
        public Vector2 Side;

        [Header("Aim")]
        [Tooltip("0 aims at Anchor_Body, 1 aims at Anchor_Head.")]
        [Range(0f, 1f)] public float AimBias;
        [Tooltip("Fraction of screen height the target should occupy. Drives the solved FOV.")]
        [Range(0.05f, 1.5f)] public float TargetScreenFraction;
        [Tooltip("Clamp on the solved field of view, in degrees.")]
        public Vector2 FovRange;
        [Tooltip("Dutch roll in degrees. Small non-zero values keep a shot from feeling surveyed.")]
        public float Dutch;

        [Header("Feel")]
        [Tooltip("Position damping in seconds. Low values track hard, high values drift.")]
        public float PositionDamping;
        [Tooltip("Aim damping in seconds, x then y.")]
        public Vector2 AimDamping;
        [Tooltip("Handheld noise amplitude. 0 disables noise on this shot.")]
        public float NoiseAmplitude;
        [Tooltip("Handheld noise frequency.")]
        public float NoiseFrequency;
        [Tooltip("Multiplier on impulse shake reaching this shot. Punch-ins take the most.")]
        public float ShakeGain;

        /// <summary>Evaluates a (constant, per-height) pair against a subject height.</summary>
        public static float Solve(Vector2 pair, float subjectHeight) => pair.x + pair.y * subjectHeight;

        /// <summary>
        /// The library of shots the rig ships with. These are tuned values, not placeholders:
        /// the punch-in is deliberately closer and harsher than anything else, the faint sits
        /// low so the collapse reads against the sky, and the wide is the only shot with a
        /// long damping tail so it feels like it is settling rather than following.
        /// </summary>
        public static ShotProfile[] DefaultLibrary() => new[]
        {
            new ShotProfile
            {
                // The only shot whose screen fraction is measured against the field rather
                // than a creature (see BattleCameraRig.SolveStageExtent), so its numbers read
                // large next to the others.
                Shot = BattleShot.WideEstablishing, Anchor = ShotSubject.Stage, Target = ShotSubject.Stage,
                Back = new Vector2(0.25f, 2.00f), Up = new Vector2(0.10f, 0.70f), Side = new Vector2(0.15f, 0.60f),
                AimBias = 0.35f, TargetScreenFraction = 0.42f, FovRange = new Vector2(24f, 64f), Dutch = 0f,
                PositionDamping = 1.1f, AimDamping = new Vector2(0.9f, 0.9f),
                NoiseAmplitude = 0.22f, NoiseFrequency = 0.35f, ShakeGain = 0.5f,
            },
            new ShotProfile
            {
                // Over-the-shoulder frames the far creature across the whole stage, so its
                // screen fraction is necessarily the smallest in the library — a shoulder
                // shot that fills the frame with the opponent is not a shoulder shot.
                Shot = BattleShot.PlayerOverShoulder, Anchor = ShotSubject.Player, Target = ShotSubject.Opponent,
                Back = new Vector2(0.15f, 0.75f), Up = new Vector2(0.12f, 0.55f), Side = new Vector2(0.10f, 0.35f),
                AimBias = 0.55f, TargetScreenFraction = 0.28f, FovRange = new Vector2(16f, 58f), Dutch = -1.5f,
                PositionDamping = 0.55f, AimDamping = new Vector2(0.45f, 0.45f),
                NoiseAmplitude = 0.32f, NoiseFrequency = 0.5f, ShakeGain = 0.8f,
            },
            new ShotProfile
            {
                Shot = BattleShot.OpponentOverShoulder, Anchor = ShotSubject.Opponent, Target = ShotSubject.Player,
                Back = new Vector2(0.15f, 0.75f), Up = new Vector2(0.12f, 0.55f), Side = new Vector2(-0.10f, -0.35f),
                AimBias = 0.55f, TargetScreenFraction = 0.28f, FovRange = new Vector2(16f, 58f), Dutch = 1.5f,
                PositionDamping = 0.55f, AimDamping = new Vector2(0.45f, 0.45f),
                NoiseAmplitude = 0.32f, NoiseFrequency = 0.5f, ShakeGain = 0.8f,
            },
            new ShotProfile
            {
                Shot = BattleShot.AttackerCloseUp, Anchor = ShotSubject.Actor, Target = ShotSubject.Actor,
                Back = new Vector2(0.20f, 1.50f), Up = new Vector2(0.10f, 0.55f), Side = new Vector2(0.20f, 0.90f),
                AimBias = 0.75f, TargetScreenFraction = 0.68f, FovRange = new Vector2(20f, 55f), Dutch = -3f,
                PositionDamping = 0.30f, AimDamping = new Vector2(0.25f, 0.25f),
                NoiseAmplitude = 0.45f, NoiseFrequency = 0.7f, ShakeGain = 1.0f,
            },
            new ShotProfile
            {
                // Mirrored across the stage axis from the attacker close-up, so cutting
                // between them crosses the line deliberately rather than by accident.
                Shot = BattleShot.TargetReaction, Anchor = ShotSubject.Receiver, Target = ShotSubject.Receiver,
                Back = new Vector2(0.20f, 1.65f), Up = new Vector2(0.10f, 0.58f), Side = new Vector2(-0.20f, -0.95f),
                AimBias = 0.70f, TargetScreenFraction = 0.62f, FovRange = new Vector2(20f, 55f), Dutch = 3f,
                PositionDamping = 0.28f, AimDamping = new Vector2(0.22f, 0.22f),
                NoiseAmplitude = 0.5f, NoiseFrequency = 0.75f, ShakeGain = 1.2f,
            },
            new ShotProfile
            {
                // The tightest shot in the rig and the only one allowed to crop its subject.
                Shot = BattleShot.ImpactPunchIn, Anchor = ShotSubject.Receiver, Target = ShotSubject.Receiver,
                Back = new Vector2(0.15f, 1.00f), Up = new Vector2(0.08f, 0.45f), Side = new Vector2(-0.15f, -0.55f),
                AimBias = 0.65f, TargetScreenFraction = 0.90f, FovRange = new Vector2(20f, 55f), Dutch = -6f,
                PositionDamping = 0.08f, AimDamping = new Vector2(0.06f, 0.06f),
                NoiseAmplitude = 0.7f, NoiseFrequency = 1.1f, ShakeGain = 1.6f,
            },
            new ShotProfile
            {
                // Backed off further than the close-up: the ball arc and the fall need
                // headroom above the creature, which a tight shot has none of.
                Shot = BattleShot.SendOut, Anchor = ShotSubject.Actor, Target = ShotSubject.Actor,
                Back = new Vector2(0.30f, 2.40f), Up = new Vector2(0.15f, 0.60f), Side = new Vector2(0.25f, 1.10f),
                AimBias = 0.50f, TargetScreenFraction = 0.45f, FovRange = new Vector2(20f, 58f), Dutch = -2f,
                PositionDamping = 0.6f, AimDamping = new Vector2(0.4f, 0.4f),
                NoiseAmplitude = 0.35f, NoiseFrequency = 0.55f, ShakeGain = 0.7f,
            },
            new ShotProfile
            {
                // Deliberately low: Up is well below the creature's mid-height so the
                // collapse falls toward the lens rather than away from it.
                Shot = BattleShot.Faint, Anchor = ShotSubject.Receiver, Target = ShotSubject.Receiver,
                Back = new Vector2(0.20f, 1.90f), Up = new Vector2(0.06f, 0.20f), Side = new Vector2(0.20f, 0.80f),
                AimBias = 0.45f, TargetScreenFraction = 0.55f, FovRange = new Vector2(20f, 55f), Dutch = 4.5f,
                PositionDamping = 0.7f, AimDamping = new Vector2(0.6f, 0.6f),
                NoiseAmplitude = 0.28f, NoiseFrequency = 0.4f, ShakeGain = 1.0f,
            },
            new ShotProfile
            {
                // Aimed low (small AimBias) because the ball, not the creature, is the
                // subject for most of the sequence.
                Shot = BattleShot.Capture, Anchor = ShotSubject.Receiver, Target = ShotSubject.Receiver,
                Back = new Vector2(0.20f, 1.90f), Up = new Vector2(0.10f, 0.60f), Side = new Vector2(0.20f, 0.85f),
                AimBias = 0.30f, TargetScreenFraction = 0.50f, FovRange = new Vector2(20f, 55f), Dutch = -2.5f,
                PositionDamping = 0.45f, AimDamping = new Vector2(0.35f, 0.35f),
                NoiseAmplitude = 0.4f, NoiseFrequency = 0.5f, ShakeGain = 0.9f,
            },
            new ShotProfile
            {
                Shot = BattleShot.Victory, Anchor = ShotSubject.Actor, Target = ShotSubject.Actor,
                Back = new Vector2(0.30f, 2.30f), Up = new Vector2(0.20f, 0.90f), Side = new Vector2(0.25f, 1.20f),
                AimBias = 0.60f, TargetScreenFraction = 0.50f, FovRange = new Vector2(20f, 58f), Dutch = -1f,
                PositionDamping = 0.9f, AimDamping = new Vector2(0.7f, 0.7f),
                NoiseAmplitude = 0.3f, NoiseFrequency = 0.4f, ShakeGain = 0.6f,
            },
        };
    }

    /// <summary>
    /// An authored blend for one ordered pair of shots.
    ///
    /// The point of naming pairs rather than using a single default blend is pacing: going
    /// wide-to-punch-in must be violent (0.08 s, hard in) and punch-in-back-to-wide must be
    /// a long settle (0.9 s, ease out). One global blend time makes one of those two wrong.
    /// </summary>
    [Serializable]
    public struct ShotBlendRule
    {
        [Tooltip("Outgoing shot. None matches any outgoing shot.")]
        public BattleShot From;
        [Tooltip("Incoming shot. None matches any incoming shot.")]
        public BattleShot To;
        public CinemachineBlendDefinition.Styles Style;
        [Tooltip("Blend duration in seconds. Never set this to zero — use a very short HardIn instead.")]
        public float Seconds;

        /// <summary>
        /// The shipped blend table. Ordered most-specific first; the rig takes the first
        /// match, and wildcard rules act as the tail defaults.
        /// </summary>
        public static ShotBlendRule[] DefaultTable() => new[]
        {
            // Impact: in like a slap, out like a breath.
            R(BattleShot.None, BattleShot.ImpactPunchIn, CinemachineBlendDefinition.Styles.HardIn, 0.09f),
            R(BattleShot.ImpactPunchIn, BattleShot.None, CinemachineBlendDefinition.Styles.EaseOut, 0.85f),

            // Anticipation reads better as a deliberate push than as a cut.
            R(BattleShot.PlayerOverShoulder, BattleShot.AttackerCloseUp, CinemachineBlendDefinition.Styles.EaseIn, 0.42f),
            R(BattleShot.OpponentOverShoulder, BattleShot.AttackerCloseUp, CinemachineBlendDefinition.Styles.EaseIn, 0.42f),
            R(BattleShot.AttackerCloseUp, BattleShot.TargetReaction, CinemachineBlendDefinition.Styles.HardIn, 0.16f),
            R(BattleShot.TargetReaction, BattleShot.ImpactPunchIn, CinemachineBlendDefinition.Styles.HardIn, 0.07f),

            // Set pieces get long, obviously authored moves.
            R(BattleShot.None, BattleShot.Faint, CinemachineBlendDefinition.Styles.EaseInOut, 0.75f),
            R(BattleShot.Faint, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 1.1f),
            R(BattleShot.None, BattleShot.Capture, CinemachineBlendDefinition.Styles.EaseInOut, 0.9f),
            R(BattleShot.Capture, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 1.0f),
            R(BattleShot.None, BattleShot.Victory, CinemachineBlendDefinition.Styles.EaseInOut, 1.2f),
            R(BattleShot.None, BattleShot.SendOut, CinemachineBlendDefinition.Styles.EaseInOut, 0.7f),
            R(BattleShot.SendOut, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 0.8f),

            // Returning to the wide is always the slowest move in the rig; it is the shot
            // the player rests on, so arriving at it should feel like exhaling.
            R(BattleShot.None, BattleShot.WideEstablishing, CinemachineBlendDefinition.Styles.EaseOut, 0.95f),

            // Shoulder-to-shoulder is a real reframe, not a cut across the axis.
            R(BattleShot.PlayerOverShoulder, BattleShot.OpponentOverShoulder, CinemachineBlendDefinition.Styles.EaseInOut, 0.6f),
            R(BattleShot.OpponentOverShoulder, BattleShot.PlayerOverShoulder, CinemachineBlendDefinition.Styles.EaseInOut, 0.6f),

            // Tail default for every pair not named above.
            R(BattleShot.None, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 0.55f),
        };

        private static ShotBlendRule R(BattleShot from, BattleShot to, CinemachineBlendDefinition.Styles style, float seconds)
            => new ShotBlendRule { From = from, To = to, Style = style, Seconds = seconds };
    }
}
