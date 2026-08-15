using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// The seven authored looks, as data.
    ///
    /// These values are the art direction. They are deliberately not photographic:
    /// shadows are blue and lifted rather than black, saturation is pushed, the sun
    /// is warmer than daylight actually is, and night is a deep desaturated blue
    /// with a visible moon key rather than an absence of light. That is the palette
    /// commercial creature-collection games use, and matching it is the brief.
    ///
    /// Every colour is authored the way a colourist would think about it, then the
    /// LUT baker turns the grading block into an actual lookup table.
    /// </summary>
    public static class GradeLibrary
    {
        public const int Count = 7;

        public static GradeDefinition[] CreateDefaults()
        {
            var grades = new GradeDefinition[Count];
            grades[(int)GradeKey.Dawn] = Dawn();
            grades[(int)GradeKey.Day] = Day();
            grades[(int)GradeKey.Dusk] = Dusk();
            grades[(int)GradeKey.Night] = Night();
            grades[(int)GradeKey.CaveInterior] = CaveInterior();
            grades[(int)GradeKey.Rain] = Rain();
            grades[(int)GradeKey.Battle] = Battle();
            return grades;
        }

        // A low, rose-gold key with a lot of atmosphere. Long shadows come from the
        // sun elevation, which the director derives from the day progress.
        private static GradeDefinition Dawn() => new GradeDefinition
        {
            Key = GradeKey.Dawn,
            SunColor = new Color(1.00f, 0.72f, 0.48f),
            SunIntensity = 1.5f,
            ShadowStrength = 0.62f,

            AmbientSky = new Color(0.42f, 0.48f, 0.72f),
            AmbientEquator = new Color(0.58f, 0.50f, 0.52f),
            AmbientGround = new Color(0.26f, 0.22f, 0.24f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(1.00f, 0.78f, 0.55f),
            RimBoost = 0.35f,

            FogColor = new Color(0.78f, 0.66f, 0.68f),
            FogDensity = 0.016f,

            SkyZenith = new Color(0.20f, 0.32f, 0.62f),
            SkyMid = new Color(0.62f, 0.55f, 0.72f),
            SkyHorizon = new Color(1.00f, 0.72f, 0.55f),
            SkyGround = new Color(0.24f, 0.22f, 0.26f),
            SunDiscColor = new Color(3.2f, 2.1f, 1.2f),
            SunGlowColor = new Color(1.60f, 0.80f, 0.45f),
            SunGlowStrength = 1.6f,
            CloudColor = new Color(1.00f, 0.82f, 0.72f),
            CloudShadeColor = new Color(0.48f, 0.42f, 0.56f),
            CloudCoverage = 0.40f,
            StarStrength = 0.15f,

            Lift = new Color(0.04f, 0.02f, 0.06f),
            Gamma = new Color(1.02f, 1.00f, 0.98f),
            Gain = new Color(1.06f, 0.99f, 0.92f),
            Contrast = 0.06f,
            Saturation = 0.10f,
            HueShift = -3f,
            SplitShadows = new Color(0.35f, 0.45f, 0.75f),
            SplitHighlights = new Color(1.00f, 0.78f, 0.52f),
            SplitBalance = -0.1f,
            LutContribution = 0.75f,

            PostExposure = 0.10f,
            BloomThreshold = 0.85f,
            BloomIntensity = 0.60f,
            BloomScatter = 0.72f,
            BloomTint = new Color(1.00f, 0.86f, 0.72f),
            VignetteIntensity = 0.22f,
            VignetteSmoothness = 0.45f,
            VignetteColor = new Color(0.06f, 0.04f, 0.10f),
            ChromaticAberration = 0.05f,
            FilmGrain = 0.10f,
            DepthOfFieldEnabled = true,
            DofFocusDistance = 9.0f,
            DofAperture = 3.2f,
            MotionBlurEnabled = false,
            MotionBlurIntensity = 0f,

            Wetness = 0.05f,
            WindStrength = 0.15f,
            NightFactor = 0.25f,
        };

        // The default look. Bright, clean, high saturation, shadows tinted towards
        // sky blue rather than towards black.
        private static GradeDefinition Day() => new GradeDefinition
        {
            Key = GradeKey.Day,
            SunColor = new Color(1.00f, 0.96f, 0.86f),
            SunIntensity = 2.4f,
            ShadowStrength = 0.78f,

            AmbientSky = new Color(0.52f, 0.68f, 0.92f),
            AmbientEquator = new Color(0.62f, 0.66f, 0.70f),
            AmbientGround = new Color(0.30f, 0.30f, 0.26f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(1.00f, 0.97f, 0.88f),
            RimBoost = 0.15f,

            FogColor = new Color(0.74f, 0.84f, 0.94f),
            FogDensity = 0.008f,

            SkyZenith = new Color(0.14f, 0.36f, 0.76f),
            SkyMid = new Color(0.40f, 0.66f, 0.94f),
            SkyHorizon = new Color(0.82f, 0.90f, 0.97f),
            SkyGround = new Color(0.28f, 0.30f, 0.30f),
            SunDiscColor = new Color(4.0f, 3.6f, 2.8f),
            SunGlowColor = new Color(1.20f, 1.05f, 0.80f),
            SunGlowStrength = 0.9f,
            CloudColor = new Color(1.00f, 0.99f, 0.97f),
            CloudShadeColor = new Color(0.60f, 0.66f, 0.80f),
            CloudCoverage = 0.34f,
            StarStrength = 0f,

            Lift = new Color(0.02f, 0.03f, 0.05f),
            Gamma = new Color(1.00f, 1.00f, 1.00f),
            Gain = new Color(1.02f, 1.01f, 0.99f),
            Contrast = 0.10f,
            Saturation = 0.16f,
            HueShift = 0f,
            SplitShadows = new Color(0.42f, 0.55f, 0.85f),
            SplitHighlights = new Color(1.00f, 0.96f, 0.84f),
            SplitBalance = 0f,
            LutContribution = 0.70f,

            PostExposure = 0f,
            BloomThreshold = 1.05f,
            BloomIntensity = 0.45f,
            BloomScatter = 0.62f,
            BloomTint = Color.white,
            VignetteIntensity = 0.16f,
            VignetteSmoothness = 0.50f,
            VignetteColor = new Color(0.04f, 0.05f, 0.08f),
            ChromaticAberration = 0.03f,
            FilmGrain = 0.05f,
            DepthOfFieldEnabled = true,
            DofFocusDistance = 11.0f,
            DofAperture = 3.6f,
            MotionBlurEnabled = false,
            MotionBlurIntensity = 0f,

            Wetness = 0f,
            WindStrength = 0.25f,
            NightFactor = 0f,
        };

        // The hero look. Deep orange key, magenta sky, strong rim on everything.
        private static GradeDefinition Dusk() => new GradeDefinition
        {
            Key = GradeKey.Dusk,
            SunColor = new Color(1.00f, 0.55f, 0.28f),
            SunIntensity = 1.7f,
            ShadowStrength = 0.58f,

            AmbientSky = new Color(0.34f, 0.34f, 0.66f),
            AmbientEquator = new Color(0.70f, 0.46f, 0.44f),
            AmbientGround = new Color(0.24f, 0.18f, 0.22f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(1.00f, 0.62f, 0.35f),
            RimBoost = 0.55f,

            FogColor = new Color(0.80f, 0.55f, 0.52f),
            FogDensity = 0.020f,

            SkyZenith = new Color(0.14f, 0.18f, 0.48f),
            SkyMid = new Color(0.58f, 0.36f, 0.62f),
            SkyHorizon = new Color(1.00f, 0.52f, 0.30f),
            SkyGround = new Color(0.18f, 0.16f, 0.22f),
            SunDiscColor = new Color(3.4f, 1.6f, 0.7f),
            SunGlowColor = new Color(1.80f, 0.62f, 0.30f),
            SunGlowStrength = 2.0f,
            CloudColor = new Color(1.00f, 0.68f, 0.55f),
            CloudShadeColor = new Color(0.42f, 0.32f, 0.52f),
            CloudCoverage = 0.46f,
            StarStrength = 0.30f,

            Lift = new Color(0.05f, 0.02f, 0.07f),
            Gamma = new Color(1.03f, 0.99f, 0.96f),
            Gain = new Color(1.10f, 0.96f, 0.86f),
            Contrast = 0.12f,
            Saturation = 0.20f,
            HueShift = -6f,
            SplitShadows = new Color(0.30f, 0.36f, 0.80f),
            SplitHighlights = new Color(1.00f, 0.66f, 0.36f),
            SplitBalance = -0.15f,
            LutContribution = 0.82f,

            PostExposure = 0.05f,
            BloomThreshold = 0.75f,
            BloomIntensity = 0.85f,
            BloomScatter = 0.78f,
            BloomTint = new Color(1.00f, 0.76f, 0.58f),
            VignetteIntensity = 0.28f,
            VignetteSmoothness = 0.42f,
            VignetteColor = new Color(0.08f, 0.03f, 0.10f),
            ChromaticAberration = 0.08f,
            FilmGrain = 0.12f,
            DepthOfFieldEnabled = true,
            DofFocusDistance = 9.0f,
            DofAperture = 3.0f,
            MotionBlurEnabled = false,
            MotionBlurIntensity = 0f,

            Wetness = 0.05f,
            WindStrength = 0.20f,
            NightFactor = 0.35f,
        };

        // Deep blue, low key, but never black: the moon is a real light and the
        // ambient floor stays high enough that shapes read. Dither is pushed hard
        // here because this grade is where 8-bit banding shows first.
        private static GradeDefinition Night() => new GradeDefinition
        {
            Key = GradeKey.Night,
            SunColor = new Color(0.52f, 0.62f, 1.00f),
            SunIntensity = 0.55f,
            ShadowStrength = 0.40f,

            AmbientSky = new Color(0.14f, 0.19f, 0.38f),
            AmbientEquator = new Color(0.12f, 0.15f, 0.28f),
            AmbientGround = new Color(0.06f, 0.07f, 0.13f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(0.62f, 0.76f, 1.00f),
            RimBoost = 0.70f,

            FogColor = new Color(0.10f, 0.14f, 0.28f),
            FogDensity = 0.024f,

            SkyZenith = new Color(0.02f, 0.03f, 0.11f),
            SkyMid = new Color(0.05f, 0.08f, 0.20f),
            SkyHorizon = new Color(0.12f, 0.16f, 0.32f),
            SkyGround = new Color(0.03f, 0.04f, 0.08f),
            SunDiscColor = new Color(1.6f, 1.7f, 2.0f),
            SunGlowColor = new Color(0.30f, 0.40f, 0.70f),
            SunGlowStrength = 0.7f,
            CloudColor = new Color(0.30f, 0.36f, 0.55f),
            CloudShadeColor = new Color(0.08f, 0.10f, 0.20f),
            CloudCoverage = 0.30f,
            StarStrength = 1.6f,

            Lift = new Color(0.01f, 0.02f, 0.05f),
            Gamma = new Color(0.98f, 1.00f, 1.05f),
            Gain = new Color(0.86f, 0.92f, 1.10f),
            Contrast = 0.05f,
            Saturation = -0.18f,
            HueShift = 6f,
            SplitShadows = new Color(0.22f, 0.32f, 0.78f),
            SplitHighlights = new Color(0.70f, 0.82f, 1.00f),
            SplitBalance = -0.3f,
            LutContribution = 0.80f,

            PostExposure = -0.15f,
            BloomThreshold = 0.60f,
            BloomIntensity = 0.95f,
            BloomScatter = 0.80f,
            BloomTint = new Color(0.78f, 0.86f, 1.00f),
            VignetteIntensity = 0.34f,
            VignetteSmoothness = 0.40f,
            VignetteColor = new Color(0.01f, 0.02f, 0.06f),
            ChromaticAberration = 0.06f,
            // Grain doubles as extra dither in the darkest grade, on top of the
            // per-shader dither. Between them the night sky should not band.
            FilmGrain = 0.28f,
            DepthOfFieldEnabled = true,
            DofFocusDistance = 8.0f,
            DofAperture = 2.8f,
            MotionBlurEnabled = false,
            MotionBlurIntensity = 0f,

            Wetness = 0.08f,
            WindStrength = 0.10f,
            NightFactor = 1.0f,
        };

        // No sun. Everything comes from a cool ambient floor and whatever point
        // lights the level has, so the cave reads as lit from inside.
        private static GradeDefinition CaveInterior() => new GradeDefinition
        {
            Key = GradeKey.CaveInterior,
            SunColor = new Color(0.30f, 0.38f, 0.55f),
            SunIntensity = 0.15f,
            ShadowStrength = 0.30f,

            AmbientSky = new Color(0.12f, 0.16f, 0.22f),
            AmbientEquator = new Color(0.10f, 0.12f, 0.17f),
            AmbientGround = new Color(0.05f, 0.06f, 0.08f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(0.55f, 0.78f, 0.95f),
            RimBoost = 0.85f,

            FogColor = new Color(0.06f, 0.09f, 0.13f),
            FogDensity = 0.055f,

            SkyZenith = new Color(0.02f, 0.03f, 0.04f),
            SkyMid = new Color(0.03f, 0.04f, 0.05f),
            SkyHorizon = new Color(0.04f, 0.05f, 0.07f),
            SkyGround = new Color(0.01f, 0.02f, 0.02f),
            SunDiscColor = Color.black,
            SunGlowColor = new Color(0.10f, 0.16f, 0.22f),
            SunGlowStrength = 0.1f,
            CloudColor = Color.black,
            CloudShadeColor = Color.black,
            CloudCoverage = 0f,
            StarStrength = 0f,

            Lift = new Color(0.02f, 0.03f, 0.04f),
            Gamma = new Color(0.97f, 1.00f, 1.03f),
            Gain = new Color(0.88f, 0.96f, 1.06f),
            Contrast = 0.16f,
            Saturation = -0.10f,
            HueShift = 4f,
            SplitShadows = new Color(0.20f, 0.34f, 0.62f),
            SplitHighlights = new Color(0.72f, 0.88f, 1.00f),
            SplitBalance = -0.2f,
            LutContribution = 0.85f,

            PostExposure = -0.05f,
            BloomThreshold = 0.55f,
            BloomIntensity = 1.20f,
            BloomScatter = 0.82f,
            BloomTint = new Color(0.70f, 0.88f, 1.00f),
            VignetteIntensity = 0.42f,
            VignetteSmoothness = 0.35f,
            VignetteColor = Color.black,
            ChromaticAberration = 0.10f,
            FilmGrain = 0.22f,
            DepthOfFieldEnabled = true,
            DofFocusDistance = 7.0f,
            DofAperture = 2.6f,
            MotionBlurEnabled = false,
            MotionBlurIntensity = 0f,

            Wetness = 0.35f,
            WindStrength = 0.02f,
            NightFactor = 0.8f,
        };

        // Overlaid on whatever time of day it is: flattens the key, greys the
        // ambient, thickens the fog, and turns the global wetness up.
        private static GradeDefinition Rain() => new GradeDefinition
        {
            Key = GradeKey.Rain,
            SunColor = new Color(0.72f, 0.78f, 0.86f),
            SunIntensity = 0.90f,
            ShadowStrength = 0.35f,

            AmbientSky = new Color(0.40f, 0.45f, 0.52f),
            AmbientEquator = new Color(0.38f, 0.42f, 0.47f),
            AmbientGround = new Color(0.20f, 0.22f, 0.25f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(0.82f, 0.90f, 1.00f),
            RimBoost = 0.40f,

            FogColor = new Color(0.55f, 0.60f, 0.66f),
            FogDensity = 0.045f,

            SkyZenith = new Color(0.26f, 0.30f, 0.36f),
            SkyMid = new Color(0.42f, 0.46f, 0.52f),
            SkyHorizon = new Color(0.60f, 0.64f, 0.68f),
            SkyGround = new Color(0.20f, 0.22f, 0.24f),
            SunDiscColor = new Color(0.9f, 0.95f, 1.0f),
            SunGlowColor = new Color(0.55f, 0.60f, 0.68f),
            SunGlowStrength = 0.35f,
            CloudColor = new Color(0.62f, 0.66f, 0.72f),
            CloudShadeColor = new Color(0.30f, 0.34f, 0.40f),
            CloudCoverage = 0.88f,
            StarStrength = 0f,

            Lift = new Color(0.03f, 0.04f, 0.05f),
            Gamma = new Color(1.00f, 1.01f, 1.02f),
            Gain = new Color(0.94f, 0.97f, 1.02f),
            Contrast = 0.04f,
            Saturation = -0.22f,
            HueShift = 3f,
            SplitShadows = new Color(0.35f, 0.45f, 0.62f),
            SplitHighlights = new Color(0.85f, 0.90f, 1.00f),
            SplitBalance = -0.1f,
            LutContribution = 0.75f,

            PostExposure = -0.08f,
            BloomThreshold = 0.90f,
            BloomIntensity = 0.55f,
            BloomScatter = 0.75f,
            BloomTint = new Color(0.90f, 0.94f, 1.00f),
            VignetteIntensity = 0.30f,
            VignetteSmoothness = 0.45f,
            VignetteColor = new Color(0.05f, 0.06f, 0.08f),
            ChromaticAberration = 0.05f,
            FilmGrain = 0.16f,
            DepthOfFieldEnabled = true,
            DofFocusDistance = 10.0f,
            DofAperture = 3.4f,
            MotionBlurEnabled = false,
            MotionBlurIntensity = 0f,

            Wetness = 1.0f,
            WindStrength = 0.85f,
            NightFactor = 0.3f,
        };

        // Deliberately the most aggressive grade in the game. The push into it is
        // part of the encounter cinematic, so it has to be visibly different within
        // the first half second: higher contrast, crushed and cooled shadows, hot
        // saturated highlights, a heavy vignette and depth of field to isolate the
        // combatants from the field.
        private static GradeDefinition Battle() => new GradeDefinition
        {
            Key = GradeKey.Battle,
            SunColor = new Color(1.00f, 0.92f, 0.80f),
            SunIntensity = 2.9f,
            ShadowStrength = 0.90f,

            AmbientSky = new Color(0.42f, 0.54f, 0.86f),
            AmbientEquator = new Color(0.48f, 0.48f, 0.58f),
            AmbientGround = new Color(0.16f, 0.16f, 0.20f),
            AmbientIntensity = 1.0f,

            RimTint = new Color(1.00f, 0.94f, 0.82f),
            RimBoost = 0.90f,

            FogColor = new Color(0.42f, 0.48f, 0.62f),
            FogDensity = 0.018f,

            SkyZenith = new Color(0.08f, 0.20f, 0.56f),
            SkyMid = new Color(0.30f, 0.52f, 0.84f),
            SkyHorizon = new Color(0.78f, 0.84f, 0.94f),
            SkyGround = new Color(0.16f, 0.18f, 0.22f),
            SunDiscColor = new Color(5.0f, 4.4f, 3.2f),
            SunGlowColor = new Color(1.60f, 1.30f, 0.90f),
            SunGlowStrength = 1.4f,
            CloudColor = new Color(1.00f, 0.98f, 0.94f),
            CloudShadeColor = new Color(0.42f, 0.48f, 0.66f),
            CloudCoverage = 0.42f,
            StarStrength = 0f,

            Lift = new Color(-0.02f, -0.01f, 0.03f),
            Gamma = new Color(0.98f, 0.99f, 1.01f),
            Gain = new Color(1.10f, 1.06f, 1.00f),
            Contrast = 0.34f,
            Saturation = 0.28f,
            HueShift = -2f,
            SplitShadows = new Color(0.22f, 0.34f, 0.90f),
            SplitHighlights = new Color(1.00f, 0.90f, 0.68f),
            SplitBalance = 0.15f,
            LutContribution = 0.90f,

            PostExposure = 0.18f,
            BloomThreshold = 0.70f,
            BloomIntensity = 1.10f,
            BloomScatter = 0.68f,
            BloomTint = new Color(1.00f, 0.94f, 0.86f),
            VignetteIntensity = 0.40f,
            VignetteSmoothness = 0.34f,
            VignetteColor = new Color(0.02f, 0.02f, 0.06f),
            ChromaticAberration = 0.14f,
            FilmGrain = 0.08f,
            // Depth of field is what turns a battle into a staged shot rather than
            // a wide view of a field with two creatures on it.
            DepthOfFieldEnabled = true,
            DofFocusDistance = 9f,
            DofAperture = 3.6f,
            MotionBlurEnabled = true,
            MotionBlurIntensity = 0.35f,

            Wetness = 0f,
            WindStrength = 0.35f,
            NightFactor = 0f,
        };
    }
}
