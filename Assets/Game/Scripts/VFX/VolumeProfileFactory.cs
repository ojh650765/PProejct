using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Builds a URP VolumeProfile from a GradeDefinition at runtime.
    ///
    /// The authored .asset profiles under Assets/Game/VFX/Rendering are the artist's
    /// copy, and LightingDirector uses them when they are assigned. This factory is
    /// what makes the system work when they are not — which is the state the project
    /// is in during integration, and the state a fresh test scene is always in.
    /// GradeLibrary stays the single source of truth either way.
    ///
    /// Every parameter is explicitly overridden. A profile with unset overrides
    /// silently inherits from whatever else is in the stack, and tracking down "the
    /// battle grade has the wrong vignette because the default volume profile also
    /// sets one" is not a debugging session anyone should have to have.
    /// </summary>
    public static class VolumeProfileFactory
    {
        public static VolumeProfile Create(in GradeDefinition grade, Texture lut)
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "PL_Grade_" + grade.Key;
            profile.hideFlags = HideFlags.HideAndDontSave;
            Populate(profile, grade, lut);
            return profile;
        }

        /// <summary>
        /// Writes a grade into an existing profile, adding overrides that are not
        /// present. Called every frame by the director on its blended profile, so it
        /// must not allocate once warmed up — hence Add-if-missing rather than clear
        /// and rebuild.
        /// </summary>
        public static void Populate(VolumeProfile profile, in GradeDefinition grade, Texture lut)
        {
            // --- Tonemapping. Neutral rather than ACES: ACES desaturates the
            // saturated primaries this art direction depends on. ------------------
            var tonemapping = GetOrAdd<Tonemapping>(profile);
            tonemapping.active = true;
            tonemapping.mode.Override(TonemappingMode.Neutral);

            // --- Colour adjustments --------------------------------------------
            var colour = GetOrAdd<ColorAdjustments>(profile);
            colour.active = true;
            colour.postExposure.Override(grade.PostExposure);
            // URP takes contrast and saturation as percentages.
            colour.contrast.Override(Mathf.Clamp(grade.Contrast * 100f, -100f, 100f));
            colour.saturation.Override(Mathf.Clamp(grade.Saturation * 100f, -100f, 100f));
            colour.hueShift.Override(Mathf.Clamp(grade.HueShift, -180f, 180f));
            colour.colorFilter.Override(grade.Gain);

            // --- Split toning ---------------------------------------------------
            var split = GetOrAdd<SplitToning>(profile);
            split.active = true;
            split.shadows.Override(grade.SplitShadows);
            split.highlights.Override(grade.SplitHighlights);
            split.balance.Override(Mathf.Clamp(grade.SplitBalance * 100f, -100f, 100f));

            // --- The baked LUT --------------------------------------------------
            var lookup = GetOrAdd<ColorLookup>(profile);
            lookup.active = lut != null;
            lookup.texture.Override(lut);
            lookup.contribution.Override(lut != null ? Mathf.Clamp01(grade.LutContribution) : 0f);

            // --- Bloom ----------------------------------------------------------
            var bloom = GetOrAdd<Bloom>(profile);
            bloom.active = true;
            bloom.threshold.Override(Mathf.Max(grade.BloomThreshold, 0f));
            bloom.intensity.Override(Mathf.Max(grade.BloomIntensity, 0f));
            bloom.scatter.Override(Mathf.Clamp01(grade.BloomScatter));
            bloom.tint.Override(grade.BloomTint);
            // High quality filtering costs little at 1080p and is the difference
            // between a soft glow and a stair-stepped one.
            bloom.highQualityFiltering.Override(true);

            // --- Vignette -------------------------------------------------------
            var vignette = GetOrAdd<Vignette>(profile);
            vignette.active = true;
            vignette.color.Override(grade.VignetteColor);
            vignette.intensity.Override(Mathf.Clamp01(grade.VignetteIntensity));
            vignette.smoothness.Override(Mathf.Clamp(grade.VignetteSmoothness, 0.01f, 1f));
            vignette.rounded.Override(false);

            // --- Chromatic aberration -------------------------------------------
            var chromatic = GetOrAdd<ChromaticAberration>(profile);
            chromatic.active = grade.ChromaticAberration > 1e-4f;
            chromatic.intensity.Override(Mathf.Clamp01(grade.ChromaticAberration));

            // --- Film grain. Doubles as a dither at the dark end. ----------------
            var grain = GetOrAdd<FilmGrain>(profile);
            grain.active = grade.FilmGrain > 1e-4f;
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(Mathf.Clamp01(grade.FilmGrain));
            grain.response.Override(0.8f);

            // --- Depth of field --------------------------------------------------
            var dof = GetOrAdd<DepthOfField>(profile);
            dof.active = grade.DepthOfFieldEnabled;
            dof.mode.Override(grade.DepthOfFieldEnabled ? DepthOfFieldMode.Bokeh : DepthOfFieldMode.Off);
            dof.focusDistance.Override(Mathf.Max(grade.DofFocusDistance, 0.1f));
            dof.aperture.Override(Mathf.Clamp(grade.DofAperture, 1f, 32f));
            dof.focalLength.Override(60f);
            dof.bladeCount.Override(6);
            dof.bladeCurvature.Override(1f);

            // --- Motion blur ------------------------------------------------------
            var motion = GetOrAdd<MotionBlur>(profile);
            motion.active = grade.MotionBlurEnabled;
            motion.mode.Override(MotionBlurMode.CameraOnly);
            motion.quality.Override(MotionBlurQuality.Medium);
            motion.intensity.Override(Mathf.Clamp01(grade.MotionBlurIntensity));
        }

        /// <summary>
        /// Deep-copies an authored profile asset and injects the runtime-baked LUT.
        ///
        /// Populating the asset in place would work and would be simpler, and it
        /// would also write a reference to a HideAndDontSave texture into a file
        /// under source control and mark it dirty every time anyone enters Play
        /// Mode. Cloning costs one allocation per grade at boot and keeps the
        /// artist's file exactly as they left it.
        /// </summary>
        public static VolumeProfile CloneWithLut(VolumeProfile source, in GradeDefinition grade,
                                                 Texture lut)
        {
            var clone = ScriptableObject.CreateInstance<VolumeProfile>();
            clone.name = source.name + " (runtime)";
            clone.hideFlags = HideFlags.HideAndDontSave;

            for (int i = 0; i < source.components.Count; i++)
            {
                var component = source.components[i];
                if (component == null) continue;
                var copy = Object.Instantiate(component);
                copy.name = component.name;
                copy.hideFlags = HideFlags.HideAndDontSave;
                clone.components.Add(copy);
            }

            var lookup = GetOrAdd<ColorLookup>(clone);
            lookup.active = lut != null;
            lookup.texture.Override(lut);
            lookup.contribution.Override(lut != null ? Mathf.Clamp01(grade.LutContribution) : 0f);

            return clone;
        }

        /// <summary>Destroys a profile built here along with the components it owns.</summary>
        public static void DestroyRuntimeProfile(VolumeProfile profile)
        {
            if (profile == null) return;

            for (int i = 0; i < profile.components.Count; i++)
            {
                var component = profile.components[i];
                if (component == null) continue;
                if (Application.isPlaying) Object.Destroy(component);
                else Object.DestroyImmediate(component);
            }
            profile.components.Clear();

            if (Application.isPlaying) Object.Destroy(profile);
            else Object.DestroyImmediate(profile);
        }

        private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var existing)) return existing;
            return profile.Add<T>(true);
        }
    }
}
