using System;
using UnityEngine;
using Unity.Cinemachine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The battle rig's shot vocabulary, after the HD-2D pivot.
    ///
    /// This is the <b>traditional Pokémon presentation</b>: one fixed three-quarter framing of
    /// the whole field, held for almost the entire battle, with a small number of authored
    /// variations that change how much of the frame the field fills and where inside it the
    /// camera is looking. Nothing here orbits, nothing crosses the axis, and nothing looks at
    /// a creature from behind or from underneath.
    ///
    /// <b>Why the orbiting set was deleted.</b> Creatures are camera-facing billboards now. A
    /// billboard has exactly two authored views — front and back — and no underside, so the
    /// shots that made the old rig worth building are not merely worse, they are impossible:
    ///
    /// <list type="bullet">
    /// <item><c>PlayerOverShoulder</c> / <c>OpponentOverShoulder</c> put a creature between the
    /// lens and the target, seen from behind. A camera-facing quad shows its front while its
    /// back is turned.</item>
    /// <item><c>ImpactPunchIn</c> filled 90% of a 1080p frame with one creature — 972 px of flat
    /// cutout, shaken hard. It is the single shot most likely to tell the player the art is
    /// cardboard.</item>
    /// <item><c>Faint</c> sat below the creature's mid-height looking up, and <c>Victory</c> rose
    /// and looked down. Neither view exists in the art.</item>
    /// <item><c>AttackerCloseUp</c> / <c>TargetReaction</c> at 0.62-0.68 of screen height were
    /// part of a 12-16× apparent-size range across the library. Pixel art tolerates roughly
    /// 6×; Octopath's battle camera varies it about 1.3×.</item>
    /// </list>
    ///
    /// What replaces them is deliberately narrow. Drama now comes from VFX, timing, screen
    /// shake and the grade, which is how the traditional games do it.
    /// </summary>
    public enum BattleShot
    {
        /// <summary>No shot forced; the rig falls back to its resting shot.</summary>
        None = 0,

        /// <summary>
        /// The primary framing and the resting shot: fixed three-quarter on the whole field,
        /// player's creature near-left, opponent far-right. Held for most of the battle.
        /// </summary>
        Field = 1,

        /// <summary>
        /// The same framing, pushed in a little and aimed at whoever is receiving. This is the
        /// entire replacement for the impact punch-in: a 1.33× push, no yaw, no Dutch.
        /// </summary>
        FieldPush = 2,

        /// <summary>The field with the aim biased toward the player's creature. A pan, not a move.</summary>
        PlayerFocus = 3,

        /// <summary>The field with the aim biased toward the opponent's creature.</summary>
        OpponentFocus = 4,

        /// <summary>Slightly wider, for the ball arc and the landing. The widest shot in the rig.</summary>
        SendOut = 5,

        /// <summary>A slow held framing on the creature going down. Nothing moves during it.</summary>
        Faint = 6,

        /// <summary>Modest framing on the ball, which is a real 3D object and takes a camera move fine.</summary>
        Capture = 7,

        /// <summary>A slow drift on the winner. Level with it — never rising and looking down.</summary>
        Victory = 8,
    }

    /// <summary>
    /// Which combatant a shot <i>aims</i> at. This only moves the look target; it never moves
    /// the camera, which is the whole point — an aim bias is a pan of a few degrees, and a
    /// change of anchor was an orbit.
    /// </summary>
    public enum ShotFocus
    {
        /// <summary>Aim at the midpoint of the field.</summary>
        Stage = 0,
        /// <summary>Aim toward whichever side is currently acting.</summary>
        Actor = 1,
        /// <summary>Aim toward whichever side is currently being acted upon.</summary>
        Receiver = 2,
        /// <summary>Always the player's creature.</summary>
        Player = 3,
        /// <summary>Always the opponent's creature.</summary>
        Opponent = 4,
    }

    /// <summary>
    /// Per-shot framing rules.
    ///
    /// The structure changed with the pivot and the change is the point. The old profile let
    /// every shot pick its own anchor creature, its own back/up/side offsets and its own
    /// subject, which is what produced a 12-16× apparent-size range and a library where three
    /// shots could not be drawn at all. Here, <b>every shot shares one camera placement</b>
    /// solved once by <see cref="BattleCameraRig"/> from the stage axis, and a shot may vary
    /// only four things:
    ///
    /// <list type="number">
    /// <item><see cref="StageScreenFraction"/> — how much of the frame the field fills. This is
    /// the <i>only</i> control over apparent subject size, and it is hard-clamped to
    /// <see cref="MinStageFraction"/>..<see cref="MaxStageFraction"/>, so the ratio across the
    /// whole library is bounded by construction and can be asserted
    /// (<see cref="ApparentSizeRatio"/>).</item>
    /// <item><see cref="Focus"/> and <see cref="FocusStrength"/> — where inside the field the
    /// camera looks. A pan of a few degrees.</item>
    /// <item><see cref="Dolly"/> and <see cref="Rise"/> — a small perspective change with the
    /// lens re-solved so apparent size does not move with it.</item>
    /// <item>Feel: damping, handheld noise, shake gain.</item>
    /// </list>
    ///
    /// There is deliberately no Dutch field. A Dutch roll rotates the pixel grid off screen
    /// axes, which is exactly the sampling case point-filtered sprite art handles worst.
    /// </summary>
    [Serializable]
    public struct ShotProfile
    {
        /// <summary>
        /// Floor on how much of the frame the field may fill. Together with
        /// <see cref="MaxStageFraction"/> this bounds the apparent-size range at 1.92×,
        /// against a ~6× tolerance for pixel art and the ~1.3× an HD-2D battle camera uses.
        /// </summary>
        public const float MinStageFraction = 0.48f;

        /// <summary>Ceiling on how much of the frame the field may fill. See <see cref="MinStageFraction"/>.</summary>
        public const float MaxStageFraction = 0.92f;

        /// <summary>
        /// The contractual ceiling on apparent-size variation across the whole library. The
        /// shipped values sit far below it; this exists so an inspector-tuned rig that drifts
        /// upward fails a check instead of quietly reintroducing the old problem.
        /// </summary>
        public const float MaxApparentSizeRatio = 3f;

        [Tooltip("Which shot these values configure.")]
        public BattleShot Shot;

        [Header("Aim (a pan — never moves the camera)")]
        [Tooltip("Who the camera looks toward.")]
        public ShotFocus Focus;
        [Tooltip("How far toward that side the aim travels. 0 stays on the field midpoint, 1 aims fully at the creature.")]
        [Range(0f, 1f)] public float FocusStrength;
        [Tooltip("0 aims at the creature's feet, 1 at its crown. 0.4-0.5 is chest height.")]
        [Range(0f, 1f)] public float AimHeightBias;

        [Header("Framing — the only apparent-size control")]
        [Tooltip("Fraction of screen height the field occupies. Clamped; the library's spread is the rig's whole zoom range.")]
        [Range(MinStageFraction, MaxStageFraction)] public float StageScreenFraction;
        [Tooltip("Clamp on the solved field of view, in degrees.")]
        public Vector2 FovRange;

        [Header("Perspective (size-neutral: the lens re-solves)")]
        [Tooltip("Fraction of the standoff to dolly in by. Positive is closer. Kept tiny — this is a perspective change, not a zoom.")]
        [Range(-0.25f, 0.25f)] public float Dolly;
        [Tooltip("Fraction of the camera height to rise by. Positive is higher. Never enough to look down on a billboard.")]
        [Range(-0.25f, 0.25f)] public float Rise;

        [Header("Feel")]
        [Tooltip("Position damping in seconds. Low values track hard, high values drift.")]
        public float PositionDamping;
        [Tooltip("Aim damping in seconds, x then y.")]
        public Vector2 AimDamping;
        [Tooltip("Handheld noise amplitude. 0 disables noise on this shot.")]
        public float NoiseAmplitude;
        [Tooltip("Handheld noise frequency.")]
        public float NoiseFrequency;
        [Tooltip("Multiplier on impulse shake reaching this shot. Shake now carries the drama the punch-in used to.")]
        public float ShakeGain;

        [Header("Occlusion")]
        [Tooltip("Let the deoccluder slide this camera when scenery blocks it. Off for every creature shot: " +
                 "a runtime yaw change is an unauthored camera angle, which is the one thing a fixed sprite layout cannot absorb.")]
        public bool AvoidObstacles;

        /// <summary>Evaluates a (constant, per-metre) pair against a scale in metres.</summary>
        public static float Solve(Vector2 pair, float scale) => pair.x + pair.y * scale;

        /// <summary>
        /// The library of shots the rig ships with.
        ///
        /// Read the <see cref="StageScreenFraction"/> column on its own: 0.52 to 0.80, a total
        /// range of 1.54×. That number is the whole camera decision. The old library's
        /// equivalent range was 0.28 to 0.90 measured against different subjects, which worked
        /// out at 12-16× of on-screen creature height.
        /// </summary>
        public static ShotProfile[] DefaultLibrary() => new[]
        {
            new ShotProfile
            {
                // The shot the battle lives in. Long damping so it settles rather than follows,
                // and the lowest noise in the library — a fixed shot that visibly breathes
                // reads as a mistake, where a moving one reads as handheld.
                Shot = BattleShot.Field, Focus = ShotFocus.Stage,
                FocusStrength = 0f, AimHeightBias = 0.42f,
                StageScreenFraction = 0.60f, FovRange = new Vector2(26f, 54f),
                Dolly = 0f, Rise = 0f,
                PositionDamping = 1.00f, AimDamping = new Vector2(0.80f, 0.80f),
                NoiseAmplitude = 0.10f, NoiseFrequency = 0.28f, ShakeGain = 0.85f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                // The impact beat. 0.80 against the Field's 0.60 is a 1.33× push — visible as
                // emphasis, nowhere near enough to expose the sprite's resolution. The shake
                // gain is where the violence went.
                Shot = BattleShot.FieldPush, Focus = ShotFocus.Receiver,
                FocusStrength = 0.45f, AimHeightBias = 0.46f,
                StageScreenFraction = 0.80f, FovRange = new Vector2(24f, 50f),
                Dolly = 0.10f, Rise = -0.02f,
                PositionDamping = 0.35f, AimDamping = new Vector2(0.28f, 0.28f),
                NoiseAmplitude = 0.18f, NoiseFrequency = 0.55f, ShakeGain = 1.45f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                Shot = BattleShot.PlayerFocus, Focus = ShotFocus.Player,
                FocusStrength = 0.55f, AimHeightBias = 0.45f,
                StageScreenFraction = 0.70f, FovRange = new Vector2(24f, 52f),
                Dolly = 0.05f, Rise = 0f,
                PositionDamping = 0.55f, AimDamping = new Vector2(0.45f, 0.45f),
                NoiseAmplitude = 0.13f, NoiseFrequency = 0.38f, ShakeGain = 1.00f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                // Deliberately identical to PlayerFocus except for the side it pans toward.
                // Mirroring the numbers is what keeps a swap between them from reading as a
                // reframe; only the aim differs, so the cut is a pan of a few degrees.
                Shot = BattleShot.OpponentFocus, Focus = ShotFocus.Opponent,
                FocusStrength = 0.55f, AimHeightBias = 0.45f,
                StageScreenFraction = 0.70f, FovRange = new Vector2(24f, 52f),
                Dolly = 0.05f, Rise = 0f,
                PositionDamping = 0.55f, AimDamping = new Vector2(0.45f, 0.45f),
                NoiseAmplitude = 0.13f, NoiseFrequency = 0.38f, ShakeGain = 1.00f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                // The one shot allowed to go wider than the Field. The ball arcs above the
                // creature's head and a tighter framing crops the top of the throw.
                Shot = BattleShot.SendOut, Focus = ShotFocus.Actor,
                FocusStrength = 0.35f, AimHeightBias = 0.55f,
                StageScreenFraction = 0.52f, FovRange = new Vector2(26f, 56f),
                Dolly = -0.06f, Rise = 0.10f,
                PositionDamping = 0.70f, AimDamping = new Vector2(0.50f, 0.50f),
                NoiseAmplitude = 0.12f, NoiseFrequency = 0.32f, ShakeGain = 0.80f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                // Aimed low because the creature ends up on the ground, and level rather than
                // beneath it. The old profile put the lens under the creature's mid-height
                // looking up, which a billboard has no image for.
                Shot = BattleShot.Faint, Focus = ShotFocus.Receiver,
                FocusStrength = 0.60f, AimHeightBias = 0.30f,
                StageScreenFraction = 0.72f, FovRange = new Vector2(24f, 52f),
                Dolly = 0.04f, Rise = -0.04f,
                PositionDamping = 0.85f, AimDamping = new Vector2(0.70f, 0.70f),
                NoiseAmplitude = 0.08f, NoiseFrequency = 0.22f, ShakeGain = 1.10f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                // Aimed low: for most of the sequence the subject is the ball on the ground,
                // and the ball is real geometry, so this is the one shot that could take a
                // bigger move. It does not, because it has to blend with everything else.
                Shot = BattleShot.Capture, Focus = ShotFocus.Opponent,
                FocusStrength = 0.55f, AimHeightBias = 0.25f,
                StageScreenFraction = 0.74f, FovRange = new Vector2(24f, 52f),
                Dolly = 0.06f, Rise = -0.05f,
                PositionDamping = 0.50f, AimDamping = new Vector2(0.40f, 0.40f),
                NoiseAmplitude = 0.14f, NoiseFrequency = 0.42f, ShakeGain = 1.00f,
                AvoidObstacles = false,
            },
            new ShotProfile
            {
                // A slow drift, level with the winner. Rise is +0.05 of the camera height,
                // which is centimetres — enough to feel like a lift, not enough to start
                // looking down at a quad that has no top.
                Shot = BattleShot.Victory, Focus = ShotFocus.Player,
                FocusStrength = 0.50f, AimHeightBias = 0.45f,
                StageScreenFraction = 0.66f, FovRange = new Vector2(26f, 54f),
                Dolly = 0.02f, Rise = 0.05f,
                PositionDamping = 0.95f, AimDamping = new Vector2(0.75f, 0.75f),
                NoiseAmplitude = 0.10f, NoiseFrequency = 0.26f, ShakeGain = 0.70f,
                AvoidObstacles = false,
            },
        };

        /// <summary>
        /// The largest apparent-size change any shot change in <paramref name="library"/> can
        /// produce, as a multiple. Because every shot shares one camera placement and the lens
        /// is solved against a fixed reference distance, this is exactly the ratio of the
        /// largest <see cref="StageScreenFraction"/> to the smallest — which is why the pivot
        /// moved the framing control here in the first place: it is now a number you can
        /// assert rather than a property you have to measure in a screenshot.
        /// </summary>
        public static float ApparentSizeRatio(ShotProfile[] library)
        {
            if (library == null || library.Length == 0) return 1f;
            float min = float.MaxValue;
            float max = 0f;
            for (int i = 0; i < library.Length; i++)
            {
                if (library[i].Shot == BattleShot.None) continue;
                float f = Mathf.Clamp(library[i].StageScreenFraction, MinStageFraction, MaxStageFraction);
                if (f < min) min = f;
                if (f > max) max = f;
            }
            return min >= float.MaxValue || min <= 0.0001f ? 1f : max / min;
        }

        /// <summary>
        /// Forces a profile back inside the framing band and strips the fields that cannot be
        /// drawn on a billboard. Applied to every profile at startup, authored or shipped, so
        /// an inspector edit cannot reintroduce a shot the art has no image for.
        /// </summary>
        public static ShotProfile Sanitise(ShotProfile p)
        {
            p.StageScreenFraction = Mathf.Clamp(p.StageScreenFraction <= 0.0001f ? 0.60f : p.StageScreenFraction,
                MinStageFraction, MaxStageFraction);
            p.FocusStrength = Mathf.Clamp01(p.FocusStrength);
            p.AimHeightBias = Mathf.Clamp01(p.AimHeightBias);
            p.Dolly = Mathf.Clamp(p.Dolly, -0.25f, 0.25f);
            p.Rise = Mathf.Clamp(p.Rise, -0.25f, 0.25f);
            p.PositionDamping = Mathf.Max(0.01f, p.PositionDamping);
            p.AimDamping = new Vector2(Mathf.Max(0f, p.AimDamping.x), Mathf.Max(0f, p.AimDamping.y));
            p.NoiseAmplitude = Mathf.Clamp(p.NoiseAmplitude, 0f, 0.6f);
            p.NoiseFrequency = Mathf.Clamp(p.NoiseFrequency, 0.01f, 2f);
            p.ShakeGain = Mathf.Clamp(p.ShakeGain, 0f, 3f);
            if (p.FovRange == Vector2.zero) p.FovRange = new Vector2(24f, 54f);
            // Non-negotiable: nothing in this rig may resolve occlusion by moving the camera.
            p.AvoidObstacles = false;
            return p;
        }
    }

    /// <summary>
    /// An authored blend for one ordered pair of shots.
    ///
    /// The pacing argument is unchanged from the orbiting rig — going into an impact beat must
    /// be faster than coming out of one — but the numbers moved. Every blend in this table is
    /// now a lens change plus a pan of a few degrees between two cameras standing in almost
    /// the same place, so it can afford to be short without reading as a cut. The one rule the
    /// old table broke is gone with the shots that broke it: nothing here sweeps yaw.
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
            // Impact: in like a slap, out like a breath. Still the sharpest pair in the table,
            // but 0.14 s rather than 0.09 s, because what it is snapping to is a 1.33× push
            // and not a 90%-of-frame punch-in — the same violence would now read as a glitch.
            R(BattleShot.None, BattleShot.FieldPush, CinemachineBlendDefinition.Styles.HardIn, 0.14f),
            R(BattleShot.FieldPush, BattleShot.None, CinemachineBlendDefinition.Styles.EaseOut, 0.80f),

            // A focus pan is the most common change in the battle and must be almost
            // invisible: same placement, same lens, a few degrees of aim.
            R(BattleShot.PlayerFocus, BattleShot.OpponentFocus, CinemachineBlendDefinition.Styles.EaseInOut, 0.45f),
            R(BattleShot.OpponentFocus, BattleShot.PlayerFocus, CinemachineBlendDefinition.Styles.EaseInOut, 0.45f),
            R(BattleShot.PlayerFocus, BattleShot.FieldPush, CinemachineBlendDefinition.Styles.HardIn, 0.16f),
            R(BattleShot.OpponentFocus, BattleShot.FieldPush, CinemachineBlendDefinition.Styles.HardIn, 0.16f),

            // Set pieces get long, obviously authored moves.
            R(BattleShot.None, BattleShot.Faint, CinemachineBlendDefinition.Styles.EaseInOut, 0.80f),
            R(BattleShot.Faint, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 1.10f),
            R(BattleShot.None, BattleShot.Capture, CinemachineBlendDefinition.Styles.EaseInOut, 0.90f),
            R(BattleShot.Capture, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 1.00f),
            R(BattleShot.None, BattleShot.Victory, CinemachineBlendDefinition.Styles.EaseInOut, 1.20f),
            R(BattleShot.None, BattleShot.SendOut, CinemachineBlendDefinition.Styles.EaseInOut, 0.70f),
            R(BattleShot.SendOut, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 0.80f),

            // Returning to the field is always the slowest move in the rig; it is the shot the
            // player rests on, so arriving at it should feel like exhaling.
            R(BattleShot.None, BattleShot.Field, CinemachineBlendDefinition.Styles.EaseOut, 0.95f),

            // Tail default for every pair not named above.
            R(BattleShot.None, BattleShot.None, CinemachineBlendDefinition.Styles.EaseInOut, 0.55f),
        };

        private static ShotBlendRule R(BattleShot from, BattleShot to, CinemachineBlendDefinition.Styles style, float seconds)
            => new ShotBlendRule { From = from, To = to, Style = style, Seconds = seconds };
    }
}
