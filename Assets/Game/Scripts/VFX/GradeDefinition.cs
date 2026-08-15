using System;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>The seven authored looks. Order matters: it indexes the weight array.</summary>
    public enum GradeKey
    {
        Dawn = 0,
        Day = 1,
        Dusk = 2,
        Night = 3,
        CaveInterior = 4,
        Rain = 5,
        Battle = 6,
    }

    /// <summary>
    /// One complete look: sun, ambient, fog, colour grade and post.
    ///
    /// Everything about a look lives in one struct so a grade can be blended as a
    /// single unit. That is the whole reason the transitions read as continuous:
    /// the sun colour, the ambient gradient, the fog and the grade all move on the
    /// same weight, so there is never a frame where the sky has changed and the
    /// shadows have not.
    ///
    /// Serializable so the integrator can override any of it from the inspector on
    /// LightingDirector without touching code.
    /// </summary>
    [Serializable]
    public struct GradeDefinition
    {
        public GradeKey Key;

        [Header("Sun")]
        public Color SunColor;
        public float SunIntensity;
        /// <summary>Shadow strength 0-1. Lower keeps stylised shadows readable.</summary>
        [Range(0f, 1f)] public float ShadowStrength;

        [Header("Ambient gradient")]
        public Color AmbientSky;
        public Color AmbientEquator;
        public Color AmbientGround;
        /// <summary>Multiplier applied on top of the three colours above.</summary>
        public float AmbientIntensity;

        [Header("Rim")]
        public Color RimTint;
        public float RimBoost;

        [Header("Fog")]
        public Color FogColor;
        public float FogDensity;

        [Header("Sky")]
        public Color SkyZenith;
        public Color SkyMid;
        public Color SkyHorizon;
        public Color SkyGround;
        public Color SunDiscColor;
        public Color SunGlowColor;
        public float SunGlowStrength;
        public Color CloudColor;
        public Color CloudShadeColor;
        [Range(0f, 1f)] public float CloudCoverage;
        [Range(0f, 4f)] public float StarStrength;

        [Header("Colour grade (baked into the LUT)")]
        /// <summary>Multiplicative tint on the shadows.</summary>
        public Color Lift;
        /// <summary>Midtone tint, applied as a per-channel gamma.</summary>
        public Color Gamma;
        /// <summary>Highlight tint, applied as a multiply.</summary>
        public Color Gain;
        [Range(-1f, 1f)] public float Contrast;
        [Range(-1f, 1f)] public float Saturation;
        [Range(-180f, 180f)] public float HueShift;
        public Color SplitShadows;
        public Color SplitHighlights;
        [Range(-1f, 1f)] public float SplitBalance;
        /// <summary>How much of the baked LUT is applied, 0-1.</summary>
        [Range(0f, 1f)] public float LutContribution;

        [Header("Post processing")]
        public float PostExposure;
        public float BloomThreshold;
        public float BloomIntensity;
        [Range(0f, 1f)] public float BloomScatter;
        public Color BloomTint;
        [Range(0f, 1f)] public float VignetteIntensity;
        [Range(0.01f, 1f)] public float VignetteSmoothness;
        public Color VignetteColor;
        [Range(0f, 1f)] public float ChromaticAberration;
        [Range(0f, 1f)] public float FilmGrain;
        public bool DepthOfFieldEnabled;
        public float DofFocusDistance;
        [Range(1f, 32f)] public float DofAperture;
        public bool MotionBlurEnabled;
        [Range(0f, 1f)] public float MotionBlurIntensity;

        [Header("Environment")]
        [Range(0f, 1f)] public float Wetness;
        public float WindStrength;
        /// <summary>0 by day, 1 at deep night. Drives adaptive dither in the shaders.</summary>
        [Range(0f, 1f)] public float NightFactor;

        /// <summary>
        /// Weighted accumulation. Called once per grade per frame with that grade's
        /// weight, then divided by the total weight.
        ///
        /// The grading block (Lift/Gamma/Gain, split toning, LUT contribution) and
        /// the depth-of-field and motion-blur switches are deliberately absent: those
        /// are baked into per-grade volume profiles and blended by URP's volume
        /// stack, which is the only place that can blend them correctly. Blending
        /// them here as well would apply each grade twice.
        /// </summary>
        public static void Accumulate(ref GradeDefinition acc, in GradeDefinition g, float w)
        {
            acc.SunColor += g.SunColor * w;
            acc.SunIntensity += g.SunIntensity * w;
            acc.ShadowStrength += g.ShadowStrength * w;

            acc.AmbientSky += g.AmbientSky * w;
            acc.AmbientEquator += g.AmbientEquator * w;
            acc.AmbientGround += g.AmbientGround * w;
            acc.AmbientIntensity += g.AmbientIntensity * w;

            acc.RimTint += g.RimTint * w;
            acc.RimBoost += g.RimBoost * w;

            acc.FogColor += g.FogColor * w;
            acc.FogDensity += g.FogDensity * w;

            acc.SkyZenith += g.SkyZenith * w;
            acc.SkyMid += g.SkyMid * w;
            acc.SkyHorizon += g.SkyHorizon * w;
            acc.SkyGround += g.SkyGround * w;
            acc.SunDiscColor += g.SunDiscColor * w;
            acc.SunGlowColor += g.SunGlowColor * w;
            acc.SunGlowStrength += g.SunGlowStrength * w;
            acc.CloudColor += g.CloudColor * w;
            acc.CloudShadeColor += g.CloudShadeColor * w;
            acc.CloudCoverage += g.CloudCoverage * w;
            acc.StarStrength += g.StarStrength * w;

            acc.PostExposure += g.PostExposure * w;
            acc.BloomThreshold += g.BloomThreshold * w;
            acc.BloomIntensity += g.BloomIntensity * w;
            acc.BloomScatter += g.BloomScatter * w;
            acc.BloomTint += g.BloomTint * w;
            acc.VignetteIntensity += g.VignetteIntensity * w;
            acc.VignetteSmoothness += g.VignetteSmoothness * w;
            acc.VignetteColor += g.VignetteColor * w;
            acc.ChromaticAberration += g.ChromaticAberration * w;
            acc.FilmGrain += g.FilmGrain * w;

            acc.Wetness += g.Wetness * w;
            acc.WindStrength += g.WindStrength * w;
            acc.NightFactor += g.NightFactor * w;
        }

        public static void Normalise(ref GradeDefinition acc, float totalWeight)
        {
            if (totalWeight <= 1e-5f) return;
            float k = 1f / totalWeight;

            acc.SunColor *= k;
            acc.SunIntensity *= k;
            acc.ShadowStrength *= k;

            acc.AmbientSky *= k;
            acc.AmbientEquator *= k;
            acc.AmbientGround *= k;
            acc.AmbientIntensity *= k;

            acc.RimTint *= k;
            acc.RimBoost *= k;

            acc.FogColor *= k;
            acc.FogDensity *= k;

            acc.SkyZenith *= k;
            acc.SkyMid *= k;
            acc.SkyHorizon *= k;
            acc.SkyGround *= k;
            acc.SunDiscColor *= k;
            acc.SunGlowColor *= k;
            acc.SunGlowStrength *= k;
            acc.CloudColor *= k;
            acc.CloudShadeColor *= k;
            acc.CloudCoverage *= k;
            acc.StarStrength *= k;

            acc.PostExposure *= k;
            acc.BloomThreshold *= k;
            acc.BloomIntensity *= k;
            acc.BloomScatter *= k;
            acc.BloomTint *= k;
            acc.VignetteIntensity *= k;
            acc.VignetteSmoothness *= k;
            acc.VignetteColor *= k;
            acc.ChromaticAberration *= k;
            acc.FilmGrain *= k;

            acc.Wetness *= k;
            acc.WindStrength *= k;
            acc.NightFactor *= k;
        }
    }
}
