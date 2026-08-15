using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// The single writer for every _PL_ global that Assets/Game/Shaders reads.
    ///
    /// These globals are what make the look cohesive: the creature shader, the
    /// terrain, the foliage, the water and the smoke all read the same ambient
    /// gradient, the same wind and the same wetness, so when the director moves the
    /// time of day every surface in the frame moves with it. Nothing else in the
    /// project should call Shader.SetGlobal on a _PL_ name.
    ///
    /// Every value defaults to the neutral element (zero, mostly), and the shaders
    /// are written so that zero means "behave as if I were not here". A scene
    /// without a LightingDirector therefore renders with plain Unity ambient and no
    /// wind rather than rendering black, which matters a great deal while eight
    /// workers are integrating in parallel.
    /// </summary>
    public static class VfxShaderGlobals
    {
        public static readonly int SkyColorId = Shader.PropertyToID("_PL_SkyColor");
        public static readonly int EquatorColorId = Shader.PropertyToID("_PL_EquatorColor");
        public static readonly int GroundColorId = Shader.PropertyToID("_PL_GroundColor");
        public static readonly int AmbientBlendId = Shader.PropertyToID("_PL_AmbientBlend");
        public static readonly int AmbientBoostId = Shader.PropertyToID("_PL_AmbientBoost");
        public static readonly int RimTintId = Shader.PropertyToID("_PL_RimTint");
        public static readonly int RimBoostId = Shader.PropertyToID("_PL_RimBoost");
        public static readonly int WindParamsId = Shader.PropertyToID("_PL_WindParams");
        public static readonly int WindGustId = Shader.PropertyToID("_PL_WindGust");
        public static readonly int WetnessId = Shader.PropertyToID("_PL_Wetness");
        public static readonly int SunColorId = Shader.PropertyToID("_PL_SunColor");
        public static readonly int NightFactorId = Shader.PropertyToID("_PL_NightFactor");

        public static void SetAmbient(Color sky, Color equator, Color ground, float blend, float boost)
        {
            Shader.SetGlobalColor(SkyColorId, sky);
            Shader.SetGlobalColor(EquatorColorId, equator);
            Shader.SetGlobalColor(GroundColorId, ground);
            Shader.SetGlobalFloat(AmbientBlendId, blend);
            Shader.SetGlobalFloat(AmbientBoostId, boost);
        }

        public static void SetRim(Color tint, float boost)
        {
            Shader.SetGlobalColor(RimTintId, tint);
            Shader.SetGlobalFloat(RimBoostId, boost);
        }

        /// <summary>
        /// Wind direction is supplied in world XZ and normalised here, because a
        /// zero-length direction in the shader falls back to a fixed diagonal and
        /// that fallback should only ever trigger when nobody has set the wind.
        /// </summary>
        public static void SetWind(Vector2 directionXZ, float strength, float gustPhaseSpeed, float gust)
        {
            Vector2 dir = directionXZ.sqrMagnitude > 1e-6f ? directionXZ.normalized : Vector2.zero;
            Shader.SetGlobalVector(WindParamsId, new Vector4(dir.x, dir.y, strength, gustPhaseSpeed));
            Shader.SetGlobalFloat(WindGustId, gust);
        }

        public static void SetWetness(float wetness) =>
            Shader.SetGlobalFloat(WetnessId, Mathf.Clamp01(wetness));

        public static void SetSun(Color colour) => Shader.SetGlobalColor(SunColorId, colour);

        /// <summary>Drives adaptive dither strength: dark grades quantise hardest.</summary>
        public static void SetNightFactor(float night) =>
            Shader.SetGlobalFloat(NightFactorId, Mathf.Clamp01(night));

        /// <summary>
        /// Puts every global back to the neutral value the shaders treat as absent.
        /// Called on director teardown so leaving Play Mode does not leave a stale
        /// midnight ambient bleeding into the next session's scene view.
        /// </summary>
        public static void ResetToNeutral()
        {
            SetAmbient(Color.black, Color.black, Color.black, 0f, 0f);
            SetRim(Color.black, 0f);
            SetWind(Vector2.zero, 0f, 0f, 0f);
            SetWetness(0f);
            SetSun(Color.black);
            SetNightFactor(0f);
        }
    }
}
