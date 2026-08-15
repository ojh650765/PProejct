using System;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The easing vocabulary the whole cinematics layer speaks.
    ///
    /// Every camera move, creature lunge and screen wipe in this assembly runs through one
    /// of these curves rather than a raw <c>Lerp</c>. That is the single cheapest defence
    /// against the "everything snaps" failure the QA pass looks for: a linear ramp reads as
    /// mechanical even when its duration is correct, so linear is only ever used for things
    /// that genuinely travel at constant speed (a projectile in flight).
    ///
    /// All functions take and return normalised time in [0,1] and are clamped, so callers
    /// never have to guard the endpoints.
    /// </summary>
    public static class CinematicEase
    {
        /// <summary>Signature every easing function in this class matches.</summary>
        public delegate float Function(float t);

        // --- Symmetric ---------------------------------------------------------------

        /// <summary>Constant speed. Use only for genuinely constant motion.</summary>
        public static float Linear(float t) => Mathf.Clamp01(t);

        /// <summary>The default settle. Gentle out of the old pose, gentle into the new one.</summary>
        public static float InOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
        }

        /// <summary>Sharper than <see cref="InOutCubic"/>; good for a deliberate camera reframe.</summary>
        public static float InOutQuint(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 16f * t * t * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 5f) * 0.5f;
        }

        /// <summary>Hermite smoothstep with zero second derivative at both ends.</summary>
        public static float Smoother(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        // --- Accelerating ------------------------------------------------------------

        /// <summary>Anticipation: slow wind-up before a strike.</summary>
        public static float InQuad(float t) { t = Mathf.Clamp01(t); return t * t; }

        /// <summary>Hard wind-up. The coil before a physical move commits.</summary>
        public static float InCubic(float t) { t = Mathf.Clamp01(t); return t * t * t; }

        /// <summary>Near-instant late acceleration. Punch-ins use this.</summary>
        public static float InExpo(float t)
        {
            t = Mathf.Clamp01(t);
            return t <= 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
        }

        // --- Decelerating ------------------------------------------------------------

        /// <summary>The workhorse arrival curve: fast departure, long soft landing.</summary>
        public static float OutQuad(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t); }

        /// <summary>A heavier arrival than <see cref="OutQuad"/>.</summary>
        public static float OutCubic(float t) { t = Mathf.Clamp01(t); return 1f - Mathf.Pow(1f - t, 3f); }

        /// <summary>Violent departure, very long tail. Impact recoil and shake decay.</summary>
        public static float OutExpo(float t)
        {
            t = Mathf.Clamp01(t);
            return t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
        }

        // --- Overshooting ------------------------------------------------------------

        /// <summary>
        /// Overshoots past 1 then settles. Used for anything that should feel like it has
        /// mass arriving: a creature landing, the HUD flying in, a ball hitting the ground.
        /// </summary>
        public static float OutBack(float t, float overshoot = 1.70158f)
        {
            t = Mathf.Clamp01(t);
            float c3 = overshoot + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + overshoot * u * u;
        }

        /// <summary>Pulls back before moving forward. Anticipation with visible wind-up.</summary>
        public static float InBack(float t, float overshoot = 1.70158f)
        {
            t = Mathf.Clamp01(t);
            float c3 = overshoot + 1f;
            return c3 * t * t * t - overshoot * t * t;
        }

        /// <summary>Springy settle. Reserved for celebration beats; it is loud.</summary>
        public static float OutElastic(float t, float period = 0.3f)
        {
            t = Mathf.Clamp01(t);
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float c4 = 2f * Mathf.PI / period;
            return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
        }

        /// <summary>Successively smaller bounces. A dropped capture ball uses this.</summary>
        public static float OutBounce(float t)
        {
            t = Mathf.Clamp01(t);
            const float n1 = 7.5625f;
            const float d1 = 2.75f;
            if (t < 1f / d1) return n1 * t * t;
            if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
            if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
            t -= 2.625f / d1;
            return n1 * t * t + 0.984375f;
        }

        // --- Shaped pulses -----------------------------------------------------------

        /// <summary>
        /// A there-and-back pulse: 0 at t=0, 1 at the given peak, 0 at t=1, with an
        /// accelerating strike and a decelerating recovery. This is the shape of a lunge,
        /// a recoil and a camera punch-in — anything that returns to where it started.
        /// </summary>
        public static float Pulse(float t, float peak = 0.35f)
        {
            t = Mathf.Clamp01(t);
            peak = Mathf.Clamp(peak, 0.01f, 0.99f);
            return t < peak
                ? InCubic(t / peak)
                : OutCubic(1f - (t - peak) / (1f - peak));
        }

        /// <summary>
        /// A damped oscillation that starts at 1 and decays to 0. Used for the wobble that
        /// follows an impact, so a hit reaction ends by settling instead of stopping dead.
        /// </summary>
        public static float DampedOscillation(float t, float frequency = 6f, float damping = 5f)
        {
            t = Mathf.Clamp01(t);
            return Mathf.Cos(t * frequency * Mathf.PI * 2f) * Mathf.Exp(-damping * t);
        }

        // --- Curve construction ------------------------------------------------------

        /// <summary>
        /// Bakes an easing function into a normalised <see cref="AnimationCurve"/> so it can
        /// be handed to systems that only accept curves — notably Cinemachine's custom blend
        /// style, which requires a curve over time [0,1] and value [0,1].
        /// </summary>
        public static AnimationCurve ToCurve(Function f, int samples = 24)
        {
            samples = Mathf.Max(2, samples);
            var keys = new Keyframe[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)(samples - 1);
                keys[i] = new Keyframe(t, f(t));
            }
            var curve = new AnimationCurve(keys);
            for (int i = 0; i < samples; i++) curve.SmoothTangents(i, 0f);
            return curve;
        }

        /// <summary>Resolves an <see cref="EaseId"/> to its function, for serialized fields.</summary>
        public static Function Resolve(EaseId id)
        {
            switch (id)
            {
                case EaseId.Linear: return Linear;
                case EaseId.InQuad: return InQuad;
                case EaseId.InCubic: return InCubic;
                case EaseId.InExpo: return InExpo;
                case EaseId.OutQuad: return OutQuad;
                case EaseId.OutCubic: return OutCubic;
                case EaseId.OutExpo: return OutExpo;
                case EaseId.InOutQuint: return InOutQuint;
                case EaseId.Smoother: return Smoother;
                case EaseId.OutBack: return t => OutBack(t);
                case EaseId.InBack: return t => InBack(t);
                case EaseId.OutElastic: return t => OutElastic(t);
                case EaseId.OutBounce: return OutBounce;
                default: return InOutCubic;
            }
        }
    }

    /// <summary>Serializable selector for <see cref="CinematicEase"/> functions.</summary>
    public enum EaseId
    {
        Linear = 0,
        InOutCubic = 1,
        InOutQuint = 2,
        Smoother = 3,
        InQuad = 4,
        InCubic = 5,
        InExpo = 6,
        OutQuad = 7,
        OutCubic = 8,
        OutExpo = 9,
        OutBack = 10,
        InBack = 11,
        OutElastic = 12,
        OutBounce = 13,
    }
}
