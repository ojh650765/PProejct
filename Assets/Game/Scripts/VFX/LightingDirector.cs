using System;
using PokeLab.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Owns the look of the world over time.
    ///
    /// Subscribes to GameEvents.TimeOfDayProgress, TimeOfDayChanged and
    /// WeatherChanged, and blends every visual consequence of them together: sun
    /// angle, sun colour and intensity, the ambient gradient, fog, the sky, the
    /// post-processing grade, global wetness and wind.
    ///
    /// Two rules drive the design:
    ///
    /// 1. Nothing ever cuts. Every value is eased towards its target on a time
    ///    constant, and the two active time-of-day volumes cross-fade continuously.
    ///    The user asked for this explicitly, and it is also why a single
    ///    TimeOfDayChanged event does not need to do anything dramatic: the
    ///    continuous progress signal has already been moving the look for a while
    ///    by the time the discrete event fires.
    ///
    /// 2. One weight vector drives everything. The sun, the ambient, the fog, the
    ///    sky material and the volume weights are all computed from the same
    ///    per-grade weights, so they cannot drift out of step.
    ///
    /// Attach to a GameObject in the boot or overworld scene, assign the directional
    /// light, and it builds the rest of its rig itself.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("PokeLab/Lighting Director")]
    public sealed class LightingDirector : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("The scene's directional light. Found automatically if left empty.")]
        [SerializeField] private Light sunLight;

        [Tooltip("Skybox material using PokeLab/Sky. An instance is created so the " +
                 "shared asset is never written to.")]
        [SerializeField] private Material skyboxMaterial;

        [Tooltip("Optional authored profiles, indexed by GradeKey. Any left empty " +
                 "are built from GradeLibrary at runtime.")]
        [SerializeField] private VolumeProfile[] authoredProfiles = new VolumeProfile[GradeLibrary.Count];

        [Header("Day cycle")]
        [Tooltip("Normalised day progress used before any TimeOfDayProgress event arrives.")]
        [Range(0f, 1f)]
        [SerializeField] private float initialProgress = 0.35f;

        [Tooltip("Seconds for a full day when Advance Time Automatically is on. " +
                 "Only used for standalone testing; the Overworld worker owns real time.")]
        [SerializeField] private float debugDayLengthSeconds = 180f;

        [SerializeField] private bool advanceTimeAutomatically;

        [Tooltip("Compass direction the sun travels along, in degrees.")]
        [Range(0f, 360f)]
        [SerializeField] private float sunAzimuth = 35f;

        [Tooltip("How far the sun's arc tilts from straight overhead. 0 puts the sun " +
                 "through the zenith, which produces very short shadows at noon.")]
        [Range(0f, 60f)]
        [SerializeField] private float sunTilt = 22f;

        [Header("Blending")]
        [Tooltip("Seconds for the look to catch up to a step change such as entering " +
                 "a cave or a battle. Larger is smoother and laggier.")]
        [SerializeField] private float blendTime = 0.9f;

        [Tooltip("Seconds for a weather change to take hold. Weather should feel " +
                 "slower to arrive than a room change.")]
        [SerializeField] private float weatherBlendTime = 3.5f;

        [Header("Wind")]
        [SerializeField] private Vector2 windDirection = new Vector2(1f, 0.35f);
        [SerializeField] private float gustPeriodSeconds = 9f;

        [Header("Debug")]
        [SerializeField] private bool logStateChanges;

        // --- Runtime state ---------------------------------------------------
        private GradeDefinition[] grades;
        private VolumeProfile[] profiles;
        private Texture2D[] luts;

        // Two cross-fading time-of-day volumes plus three overlays. Two is enough
        // because the day cycle only ever blends between adjacent keys, and using
        // exactly two makes the blend an exact lerp rather than an approximation of
        // one built out of four overlapping weights.
        private Volume timeVolumeFrom;
        private Volume timeVolumeTo;
        private Volume rainVolume;
        private Volume caveVolume;
        private Volume battleVolume;

        private Material skyboxInstance;

        private float progress;
        private float targetRainWeight, currentRainWeight;
        private float targetCaveWeight, currentCaveWeight;
        private float targetBattleWeight, currentBattleWeight;

        private bool subscribed;

        // The day-cycle keys, in progress order. Dawn wraps around from Night.
        private static readonly GradeKey[] TimeKeys =
        {
            GradeKey.Dawn, GradeKey.Day, GradeKey.Dusk, GradeKey.Night,
        };

        private static readonly float[] TimeKeyPositions = { 0.00f, 0.30f, 0.62f, 0.80f };

        /// <summary>The look currently on screen, after blending. Read-only for others.</summary>
        public GradeDefinition CurrentGrade { get; private set; }

        /// <summary>Normalised day progress, 0-1.</summary>
        public float Progress => progress;

        /// <summary>The weather the director is currently grading for.</summary>
        public Weather CurrentWeather { get; private set; } = Weather.Clear;

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------
        private void Awake()
        {
            grades = GradeLibrary.CreateDefaults();
            progress = initialProgress;

            BuildLuts();
            BuildProfiles();
            BuildVolumeRig();
            BuildSkybox();

            // Ambient is authored here rather than in the Lighting window, so a scene
            // that has never been baked still looks right.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;

            ApplyImmediate();
        }

        private void OnEnable()
        {
            if (subscribed) return;
            GameEvents.TimeOfDayProgress += OnTimeOfDayProgress;
            GameEvents.TimeOfDayChanged += OnTimeOfDayChanged;
            GameEvents.WeatherChanged += OnWeatherChanged;
            GameEvents.BiomeEntered += OnBiomeEntered;
            GameEvents.ModeChanged += OnModeChanged;
            subscribed = true;
        }

        private void OnDisable()
        {
            if (!subscribed) return;
            GameEvents.TimeOfDayProgress -= OnTimeOfDayProgress;
            GameEvents.TimeOfDayChanged -= OnTimeOfDayChanged;
            GameEvents.WeatherChanged -= OnWeatherChanged;
            GameEvents.BiomeEntered -= OnBiomeEntered;
            GameEvents.ModeChanged -= OnModeChanged;
            subscribed = false;
        }

        private void OnDestroy()
        {
            VfxShaderGlobals.ResetToNeutral();
            DestroyGenerated();
        }

        private void Update()
        {
            if (advanceTimeAutomatically && debugDayLengthSeconds > 0.01f)
                progress = Mathf.Repeat(progress + Time.deltaTime / debugDayLengthSeconds, 1f);

            float dt = Time.deltaTime;
            currentRainWeight = Ease(currentRainWeight, targetRainWeight, weatherBlendTime, dt);
            currentCaveWeight = Ease(currentCaveWeight, targetCaveWeight, blendTime, dt);
            currentBattleWeight = Ease(currentBattleWeight, targetBattleWeight, blendTime, dt);

            Apply();
        }

        /// <summary>
        /// Exponential ease towards a target, framerate independent. Not a lerp with
        /// a fixed factor: that one is framerate dependent and produces a different
        /// transition speed on a 144 Hz monitor, which is exactly the kind of bug
        /// that only shows up on someone else's machine.
        /// </summary>
        private static float Ease(float current, float target, float timeConstant, float dt)
        {
            if (timeConstant <= 1e-4f) return target;
            float k = 1f - Mathf.Exp(-dt / timeConstant);
            return Mathf.Lerp(current, target, k);
        }

        // ---------------------------------------------------------------------
        // Public API. The cinematics worker calls these directly; everything else
        // reaches them through game events.
        // ---------------------------------------------------------------------

        /// <summary>Push into or out of the dramatic battle grade. 0-1.</summary>
        public void SetBattleIntensity(float weight) => targetBattleWeight = Mathf.Clamp01(weight);

        /// <summary>Push into or out of the cave interior look. 0-1.</summary>
        public void SetCaveWeight(float weight) => targetCaveWeight = Mathf.Clamp01(weight);

        /// <summary>Override the day progress directly, for cutscenes.</summary>
        public void SetProgress(float normalised) => progress = Mathf.Repeat(normalised, 1f);

        /// <summary>
        /// Snap the blend to its target rather than easing. Use when teleporting or
        /// loading, never mid-scene: the whole point of this component is that it
        /// does not cut.
        /// </summary>
        public void ApplyImmediate()
        {
            currentRainWeight = targetRainWeight;
            currentCaveWeight = targetCaveWeight;
            currentBattleWeight = targetBattleWeight;
            Apply();
        }

        // ---------------------------------------------------------------------
        // Event handlers
        // ---------------------------------------------------------------------
        private void OnTimeOfDayProgress(float normalised) => progress = Mathf.Repeat(normalised, 1f);

        private void OnTimeOfDayChanged(TimeOfDay from, TimeOfDay to)
        {
            // The continuous progress signal is authoritative. This handler exists to
            // recover when only the discrete event is being raised: it snaps progress
            // to the incoming key only if progress is nowhere near it, which means a
            // caller driving both signals never fights itself.
            float target = KeyPositionFor(to);
            if (Mathf.Abs(Mathf.DeltaAngle(progress * 360f, target * 360f)) > 60f)
                progress = target;

            if (logStateChanges)
                Debug.Log($"[LightingDirector] Time of day {from} -> {to} (progress {progress:F2})");
        }

        private void OnWeatherChanged(Weather from, Weather to)
        {
            CurrentWeather = to;
            targetRainWeight = to == Weather.Rain ? 1f : (to == Weather.Fog ? 0.55f : 0f);
            if (logStateChanges) Debug.Log($"[LightingDirector] Weather {from} -> {to}");
        }

        private void OnBiomeEntered(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return;
            bool cave = biomeId.IndexOf("cave", StringComparison.OrdinalIgnoreCase) >= 0
                     || biomeId.IndexOf("interior", StringComparison.OrdinalIgnoreCase) >= 0;
            targetCaveWeight = cave ? 1f : 0f;
        }

        private void OnModeChanged(GameMode from, GameMode to)
        {
            // The encounter intro is where the grade shift reads best, so it starts
            // there rather than waiting for the battle proper.
            bool dramatic = to == GameMode.Battle || to == GameMode.EncounterIntro;
            targetBattleWeight = dramatic ? 1f : 0f;
        }

        private static float KeyPositionFor(TimeOfDay tod)
        {
            switch (tod)
            {
                case TimeOfDay.Dawn: return TimeKeyPositions[0];
                case TimeOfDay.Day: return TimeKeyPositions[1];
                case TimeOfDay.Dusk: return TimeKeyPositions[2];
                default: return TimeKeyPositions[3];
            }
        }

        // ---------------------------------------------------------------------
        // The blend
        // ---------------------------------------------------------------------
        private void Apply()
        {
            ResolveTimeBlend(progress, out int fromIndex, out int toIndex, out float t);

            GradeKey fromKey = TimeKeys[fromIndex];
            GradeKey toKey = TimeKeys[toIndex];

            // --- Blend the definition ---------------------------------------
            var blended = new GradeDefinition();
            float total = 0f;

            GradeDefinition.Accumulate(ref blended, grades[(int)fromKey], 1f - t); total += 1f - t;
            GradeDefinition.Accumulate(ref blended, grades[(int)toKey], t); total += t;

            // Overlays sit on top of the time-of-day blend rather than replacing it,
            // so rain at dusk still reads as dusk and a cave keeps a hint of it.
            if (currentRainWeight > 1e-4f)
            {
                GradeDefinition.Accumulate(ref blended, grades[(int)GradeKey.Rain], currentRainWeight * 1.6f);
                total += currentRainWeight * 1.6f;
            }
            if (currentCaveWeight > 1e-4f)
            {
                // The cave dominates: no daylight reaches it.
                GradeDefinition.Accumulate(ref blended, grades[(int)GradeKey.CaveInterior], currentCaveWeight * 5f);
                total += currentCaveWeight * 5f;
            }
            if (currentBattleWeight > 1e-4f)
            {
                GradeDefinition.Accumulate(ref blended, grades[(int)GradeKey.Battle], currentBattleWeight * 1.4f);
                total += currentBattleWeight * 1.4f;
            }

            GradeDefinition.Normalise(ref blended, total);
            CurrentGrade = blended;

            ApplySun(blended);
            ApplyAmbientAndFog(blended);
            ApplyGlobals(blended);
            ApplySkybox(blended);
            ApplyVolumeWeights(fromKey, toKey, t);
        }

        /// <summary>
        /// Finds the two day-cycle keys the current progress sits between, wrapping
        /// from Night back around to Dawn, and returns a smoothstepped blend factor
        /// so the transitions ease in and out rather than running at a constant rate.
        /// </summary>
        private static void ResolveTimeBlend(float p, out int fromIndex, out int toIndex, out float t)
        {
            int n = TimeKeyPositions.Length;

            fromIndex = n - 1;
            for (int i = n - 1; i >= 0; i--)
            {
                if (p >= TimeKeyPositions[i]) { fromIndex = i; break; }
            }
            // Before the first key means we are still on the wrap from the last one.
            if (p < TimeKeyPositions[0]) fromIndex = n - 1;

            toIndex = (fromIndex + 1) % n;

            float start = TimeKeyPositions[fromIndex];
            float end = TimeKeyPositions[toIndex];
            float span = end - start;
            if (span <= 0f) span += 1f;   // the Night -> Dawn wrap

            float delta = p - start;
            if (delta < 0f) delta += 1f;

            float raw = Mathf.Clamp01(delta / Mathf.Max(span, 1e-4f));
            t = raw * raw * (3f - 2f * raw);
        }

        private void ApplySun(in GradeDefinition g)
        {
            if (sunLight == null) sunLight = FindSunLight();
            if (sunLight == null) return;

            // The sun travels a full circle per day. Progress 0.25 is noon, so the
            // elevation peaks a quarter of the way through, matching the Day key.
            float dayAngle = (progress - 0.25f) * 360f;
            var rotation = Quaternion.Euler(0f, sunAzimuth, 0f) *
                           Quaternion.Euler(dayAngle, 0f, 0f) *
                           Quaternion.Euler(0f, 0f, sunTilt);

            sunLight.transform.rotation = rotation;
            sunLight.color = g.SunColor;
            sunLight.intensity = Mathf.Max(g.SunIntensity, 0f);
            sunLight.shadowStrength = Mathf.Clamp01(g.ShadowStrength);

            // In a cave the directional light would leak through the ceiling, so the
            // cave grade drives its intensity to near nothing and we cull it entirely
            // once it can no longer contribute.
            bool contributes = sunLight.intensity > 0.02f;
            if (sunLight.enabled != contributes) sunLight.enabled = contributes;
        }

        private void ApplyAmbientAndFog(in GradeDefinition g)
        {
            // Written for the benefit of any material that is not one of ours: URP's
            // own Lit, particles, UI. Our shaders read the _PL_ globals instead, but
            // both come from the same numbers so the frame stays coherent.
            RenderSettings.ambientSkyColor = g.AmbientSky * g.AmbientIntensity;
            RenderSettings.ambientEquatorColor = g.AmbientEquator * g.AmbientIntensity;
            RenderSettings.ambientGroundColor = g.AmbientGround * g.AmbientIntensity;

            RenderSettings.fogColor = g.FogColor;
            RenderSettings.fogDensity = Mathf.Max(g.FogDensity, 0f);
        }

        private void ApplyGlobals(in GradeDefinition g)
        {
            VfxShaderGlobals.SetAmbient(
                g.AmbientSky * g.AmbientIntensity,
                g.AmbientEquator * g.AmbientIntensity,
                g.AmbientGround * g.AmbientIntensity,
                blend: 1f,
                boost: 0f);

            VfxShaderGlobals.SetRim(g.RimTint, g.RimBoost);
            VfxShaderGlobals.SetSun(g.SunColor * Mathf.Clamp01(g.SunIntensity * 0.4f));
            VfxShaderGlobals.SetWetness(g.Wetness);
            VfxShaderGlobals.SetNightFactor(g.NightFactor);

            // Gusts are a slow noise rather than a sine, so foliage does not pulse
            // on a recognisable beat.
            float gustPhase = gustPeriodSeconds > 0.01f ? Time.time / gustPeriodSeconds : 0f;
            float gust = Mathf.PerlinNoise(gustPhase, 3.7f);
            gust = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((gust - 0.35f) / 0.5f));

            VfxShaderGlobals.SetWind(windDirection, g.WindStrength, 1f / Mathf.Max(gustPeriodSeconds, 0.01f),
                                     gust * (0.4f + g.WindStrength));
        }

        private void ApplySkybox(in GradeDefinition g)
        {
            if (skyboxInstance == null) return;

            skyboxInstance.SetColor(SkyIds.Zenith, g.SkyZenith);
            skyboxInstance.SetColor(SkyIds.Mid, g.SkyMid);
            skyboxInstance.SetColor(SkyIds.Horizon, g.SkyHorizon);
            skyboxInstance.SetColor(SkyIds.Ground, g.SkyGround);
            skyboxInstance.SetColor(SkyIds.SunColor, g.SunDiscColor);
            skyboxInstance.SetColor(SkyIds.SunGlowColor, g.SunGlowColor);
            skyboxInstance.SetFloat(SkyIds.SunGlowStrength, g.SunGlowStrength);
            skyboxInstance.SetColor(SkyIds.CloudColor, g.CloudColor);
            skyboxInstance.SetColor(SkyIds.CloudShadeColor, g.CloudShadeColor);
            skyboxInstance.SetFloat(SkyIds.CloudCoverage, g.CloudCoverage);
            skyboxInstance.SetFloat(SkyIds.StarStrength, g.StarStrength);
            // Dither harder as the sky darkens: that is where the steps are closest
            // together and where the QA brief says banding shows.
            skyboxInstance.SetFloat(SkyIds.DitherStrength, Mathf.Lerp(1.5f, 4.0f, g.NightFactor));
        }

        private void ApplyVolumeWeights(GradeKey fromKey, GradeKey toKey, float t)
        {
            if (timeVolumeFrom == null) return;

            // Swapping the profile reference rather than re-authoring means the two
            // volumes always hold the two grades we are between, and the cross-fade
            // is an exact lerp between them.
            var fromProfile = ProfileFor(fromKey);
            var toProfile = ProfileFor(toKey);

            if (timeVolumeFrom.sharedProfile != fromProfile) timeVolumeFrom.sharedProfile = fromProfile;
            if (timeVolumeTo.sharedProfile != toProfile) timeVolumeTo.sharedProfile = toProfile;

            timeVolumeFrom.weight = 1f;
            timeVolumeTo.weight = t;

            rainVolume.weight = currentRainWeight;
            caveVolume.weight = currentCaveWeight;
            battleVolume.weight = currentBattleWeight;
        }

        // ---------------------------------------------------------------------
        // Rig construction
        // ---------------------------------------------------------------------
        private void BuildLuts()
        {
            luts = new Texture2D[GradeLibrary.Count];
            for (int i = 0; i < grades.Length; i++)
                luts[i] = ColorGradingLutBaker.Bake(grades[i]);
        }

        private void BuildProfiles()
        {
            profiles = new VolumeProfile[GradeLibrary.Count];
            for (int i = 0; i < grades.Length; i++)
            {
                bool authored = authoredProfiles != null
                             && i < authoredProfiles.Length
                             && authoredProfiles[i] != null;

                // Either way the profile the volumes reference is a runtime object we
                // own, never the asset on disk. An authored file is cloned and given
                // the baked LUT it cannot reference itself; an unassigned slot is
                // built from GradeLibrary.
                profiles[i] = authored
                    ? VolumeProfileFactory.CloneWithLut(authoredProfiles[i], grades[i], luts[i])
                    : VolumeProfileFactory.Create(grades[i], luts[i]);
            }
        }

        private VolumeProfile ProfileFor(GradeKey key) => profiles[(int)key];

        private void BuildVolumeRig()
        {
            // Priorities are spread so anything the integrator adds by hand can slot
            // between them without a fight.
            timeVolumeFrom = CreateVolume("PL_Volume_TimeFrom", 0f, ProfileFor(GradeKey.Dawn), 1f);
            timeVolumeTo = CreateVolume("PL_Volume_TimeTo", 10f, ProfileFor(GradeKey.Day), 0f);
            rainVolume = CreateVolume("PL_Volume_Rain", 20f, ProfileFor(GradeKey.Rain), 0f);
            caveVolume = CreateVolume("PL_Volume_Cave", 30f, ProfileFor(GradeKey.CaveInterior), 0f);
            battleVolume = CreateVolume("PL_Volume_Battle", 40f, ProfileFor(GradeKey.Battle), 0f);
        }

        private Volume CreateVolume(string name, float priority, VolumeProfile profile, float weight)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSave;

            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = priority;
            volume.sharedProfile = profile;
            volume.weight = weight;
            return volume;
        }

        private void BuildSkybox()
        {
            Material source = skyboxMaterial;

            // Resources before Shader.Find: a shader reached only through Shader.Find
            // is a shader the build stripper is entitled to remove, and a missing sky
            // in a player build that worked in the editor is a miserable thing to
            // diagnose. The Resources material keeps it alive.
            if (source == null) source = Resources.Load<Material>("PokeLab/Materials/Sky");

            if (source == null)
            {
                var shader = Shader.Find("PokeLab/Sky");
                if (shader == null)
                {
                    // Not fatal: the ambient, fog and grade still work, the sky just
                    // stays whatever the scene had.
                    Debug.LogWarning("[LightingDirector] PokeLab/Sky not found. Assign a skybox " +
                                     "material or make sure the shader is included in the build.");
                    return;
                }
                source = new Material(shader);
            }

            skyboxInstance = new Material(source) { hideFlags = HideFlags.DontSave };
            RenderSettings.skybox = skyboxInstance;
        }

        private static Light FindSunLight()
        {
            // RenderSettings.sun is set by the Lighting window and is the right answer
            // when it exists; otherwise take the brightest directional light.
            if (RenderSettings.sun != null) return RenderSettings.sun;

            Light best = null;
            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type != LightType.Directional) continue;
                if (best == null || lights[i].intensity > best.intensity) best = lights[i];
            }
            return best;
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        private void DestroyGenerated()
        {
            if (luts != null)
            {
                for (int i = 0; i < luts.Length; i++) SafeDestroy(luts[i]);
                luts = null;
            }

            if (profiles != null)
            {
                // Every entry is a runtime profile we own, including the clones of
                // authored assets, so all of them go. The assets themselves were
                // never handed to a Volume and are untouched.
                for (int i = 0; i < profiles.Length; i++)
                    VolumeProfileFactory.DestroyRuntimeProfile(profiles[i]);
                profiles = null;
            }

            if (skyboxInstance != null)
            {
                if (RenderSettings.skybox == skyboxInstance) RenderSettings.skybox = null;
                SafeDestroy(skyboxInstance);
                skyboxInstance = null;
            }
        }

        /// <summary>Shader property ids for PokeLab/Sky, resolved once.</summary>
        private static class SkyIds
        {
            public static readonly int Zenith = Shader.PropertyToID("_ZenithColor");
            public static readonly int Mid = Shader.PropertyToID("_MidColor");
            public static readonly int Horizon = Shader.PropertyToID("_HorizonColor");
            public static readonly int Ground = Shader.PropertyToID("_GroundColor");
            public static readonly int SunColor = Shader.PropertyToID("_SunColor");
            public static readonly int SunGlowColor = Shader.PropertyToID("_SunGlowColor");
            public static readonly int SunGlowStrength = Shader.PropertyToID("_SunGlowStrength");
            public static readonly int CloudColor = Shader.PropertyToID("_CloudColor");
            public static readonly int CloudShadeColor = Shader.PropertyToID("_CloudShadeColor");
            public static readonly int CloudCoverage = Shader.PropertyToID("_CloudCoverage");
            public static readonly int StarStrength = Shader.PropertyToID("_StarStrength");
            public static readonly int DitherStrength = Shader.PropertyToID("_DitherStrength");
        }
    }
}
