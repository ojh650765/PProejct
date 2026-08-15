using System;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Every tunable duration in the battle performance, in one place.
    ///
    /// These are the values a director would give notes on, so they are named after what
    /// they do on screen rather than after the code that reads them. Two rules held
    /// throughout: an anticipation is always shorter than the action it precedes, and a
    /// recovery is always longer than the action it follows. Reversing either makes the
    /// performance feel weightless no matter what the absolute numbers are.
    ///
    /// A <see cref="PokeLab.Core.BattleEvent.SuggestedDuration"/> is a floor. Where a beat
    /// here is longer, the beat wins; where it is shorter, the presenter waits out the
    /// remainder before starting the next event.
    /// </summary>
    [Serializable]
    public sealed class BattleChoreography
    {
        [Header("Send-out")]
        [Tooltip("Trainer wind-up before the ball leaves their hand.")]
        public float ThrowWindUp = 0.30f;
        [Tooltip("Ball flight time from trainer to the creature's mark.")]
        public float BallFlight = 0.52f;
        [Tooltip("Ball spring-open.")]
        public float BallOpen = 0.16f;
        [Tooltip("Creature fall from the burst point to the ground.")]
        public float LandFall = 0.30f;
        [Tooltip("Compression and rebound after landing. This is where the weight lives.")]
        public float LandSettle = 0.40f;
        [Tooltip("Beat held after the cry, before the creature turns to its opponent.")]
        public float CryHold = 0.34f;
        [Tooltip("Turn to face the opponent.")]
        public float FaceOffTurn = 0.42f;
        [Tooltip("Multiplier applied to the whole send-out when it is a mid-battle replacement.")]
        [Range(0.3f, 1f)] public float ReplacementSpeedUp = 0.68f;

        [Header("Withdraw")]
        public float RecallBeam = 0.46f;
        public float RecallHold = 0.24f;

        [Header("Move")]
        [Tooltip("Held on the attacker between the move being declared and the attack starting.")]
        public float MoveAnticipation = 0.42f;
        [Tooltip("Anticipation for hits after the first in a multi-hit move.")]
        public float MultiHitAnticipation = 0.10f;
        [Tooltip("Physical lunge into contact.")]
        public float PhysicalLunge = 0.34f;
        [Tooltip("Fraction of the lunge at which contact occurs.")]
        [Range(0.1f, 0.9f)] public float PhysicalContactAt = 0.40f;
        [Tooltip("Special move plant, charge and release.")]
        public float SpecialPlant = 0.62f;
        [Tooltip("Fraction of the plant at which the move is released.")]
        [Range(0.1f, 0.9f)] public float SpecialReleaseAt = 0.58f;
        [Tooltip("Status move gesture.")]
        public float StatusGesture = 0.55f;
        [Tooltip("Projectile flight time from muzzle to target body.")]
        public float ProjectileFlight = 0.40f;
        [Tooltip("Recovery back to the battle idle after an attack.")]
        public float MoveRecovery = 0.30f;

        [Header("Impact")]
        [Tooltip("Recoil length at full-health-bar damage. Scaled down for smaller hits.")]
        public float RecoilMax = 0.62f;
        [Tooltip("Recoil length at negligible damage.")]
        public float RecoilMin = 0.28f;
        [Tooltip("Hold on the impact push-in after a normal hit.")]
        public float PunchInHold = 0.22f;
        [Tooltip("Hold on the impact push-in after a critical hit.")]
        public float PunchInHoldCritical = 0.46f;
        [Tooltip("Extra beat held on a super-effective hit before the next event.")]
        public float SuperEffectiveBeat = 0.30f;

        [Header("Miss")]
        public float DodgePrepare = 0.12f;
        public float DodgeMove = 0.44f;
        [Tooltip("Held after the attack has passed through, so the miss registers.")]
        public float MissBeat = 0.26f;

        [Header("Faint")]
        [Tooltip("Stagger, fall and dead bounce.")]
        public float FaintCollapse = 1.15f;
        [Tooltip("Silence held on the fallen creature. Do not shorten this — it is the beat.")]
        public float FaintHold = 0.85f;
        public float FaintRecall = 0.55f;

        [Header("Capture")]
        public float CaptureThrowWindUp = 0.38f;
        public float CaptureFlight = 0.62f;
        public float CaptureAbsorb = 0.66f;
        public float CaptureFall = 0.72f;
        [Tooltip("Held after the ball settles, before the first shake.")]
        public float CaptureSettleHold = 0.45f;
        [Tooltip("Held before the click on a successful capture.")]
        public float CaptureClickHold = 0.62f;
        [Tooltip("Celebration after a successful capture.")]
        public float CaptureCelebrate = 1.5f;

        [Header("Status, stats and abilities")]
        public float StatusOnset = 0.55f;
        public float StatStageBeat = 0.50f;
        public float AbilityFlare = 0.70f;
        public float WeatherTurn = 1.0f;

        [Header("Outro")]
        public float VictoryPose = 1.4f;
        public float ExperienceBeat = 1.1f;
        public float LevelUpBeat = 0.9f;
        public float BattleEndHold = 1.2f;

        /// <summary>
        /// Recoil length for a given damage fraction. Interpolated rather than switched, so
        /// a 40% hit genuinely reads as bigger than a 30% one instead of falling into the
        /// same bucket.
        /// </summary>
        public float RecoilFor(float damageFraction)
            => Mathf.Lerp(RecoilMin, RecoilMax, Mathf.Clamp01(damageFraction));
    }
}
