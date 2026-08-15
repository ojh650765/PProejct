using System.Collections;
using UnityEngine;
using Unity.Cinemachine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Everything that makes the battle camera feel like it is being held by someone who
    /// is reacting to what they are filming: impact shake, the critical-hit hitch, and the
    /// low-amplitude handheld drift that runs continuously underneath.
    ///
    /// Kept separate from <see cref="BattleCameraRig"/> because shake is orthogonal to shot
    /// selection — a push-in and the field shot can both be shaking, and the rig should not
    /// have to know about it to blend correctly.
    ///
    /// <b>The pivot made this the loudest instrument in the orchestra.</b> With the impact
    /// punch-in cut and the whole camera confined to a 1.5× zoom range, shake is most of what
    /// is left to say "that hurt", so the amplitudes below went up rather than down. The one
    /// thing that had to be added is the ceiling in <see cref="SubjectFalloff"/>: shaking a
    /// flat sprite that already fills the frame does not read as force, it reads as a
    /// cardboard cutout being waggled, and that failure is much worse than an under-sold hit.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public sealed class CameraShakeDirector : MonoBehaviour
    {
        [Header("Impact shake")]
        [Tooltip("Shake amplitude for a hit that removes the target's entire health bar.")]
        [SerializeField] private float maxImpactAmplitude = 1.90f;
        [Tooltip("Amplitude floor, so even a 1 HP chip registers physically.")]
        [SerializeField] private float minImpactAmplitude = 0.26f;
        [Tooltip("Impulse duration in seconds at full strength.")]
        [SerializeField] private float impactDuration = 0.36f;
        [Tooltip("Extra multiplier applied on a critical hit.")]
        [SerializeField] private float criticalMultiplier = 2.1f;
        [Tooltip("Extra multiplier applied when the hit was super effective.")]
        [SerializeField] private float superEffectiveMultiplier = 1.45f;

        [Header("Sprite ceiling")]
        [Tooltip("Fraction of screen height the subject may fill before shake starts backing off. " +
                 "Above this a flat sprite being shaken reads as a wobbling cutout rather than as impact.")]
        [Range(0.5f, 1f)]
        [SerializeField] private float shakeFalloffStart = 0.72f;
        [Tooltip("Fraction of screen height at which shake is reduced to shakeFloor.")]
        [Range(0.6f, 1.2f)]
        [SerializeField] private float shakeFalloffEnd = 1.00f;
        [Tooltip("Multiplier shake is reduced to at and beyond shakeFalloffEnd. Never zero — a hit that " +
                 "produces no camera response at all reads as a dropped frame.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float shakeFloor = 0.35f;

        [Header("Critical hitch")]
        [Tooltip("Freeze-frame on a critical hit. Set false if another system owns time scale.")]
        [SerializeField] private bool enableTimeHitch = true;
        [Tooltip("Time scale held during the hitch.")]
        [Range(0.01f, 1f)]
        [SerializeField] private float hitchTimeScale = 0.08f;
        [Tooltip("Unscaled seconds the hitch is held before time ramps back up.")]
        [SerializeField] private float hitchHold = 0.07f;
        [Tooltip("Unscaled seconds spent ramping time back to normal. Never snap this back.")]
        [SerializeField] private float hitchRecover = 0.22f;

        [Header("Handheld")]
        [Tooltip("Global multiplier on the per-shot handheld noise amplitude.")]
        [Range(0f, 2f)]
        [SerializeField] private float handheldScale = 1f;

        private CinemachineImpulseSource _source;
        private NoiseSettings _handheldProfile;
        private Coroutine _hitch;
        private float _timeScaleBeforeHitch = 1f;
        private float _subjectScreenFraction = 0.6f;
        private float _shotGain = 1f;

        /// <summary>
        /// The shared handheld noise profile, built in code so the rig does not depend on a
        /// committed <c>.asset</c>. Amplitude is scaled per shot by
        /// <see cref="ShotProfile.NoiseAmplitude"/>, so one profile serves every camera.
        /// </summary>
        public NoiseSettings HandheldProfile
        {
            get
            {
                if (_handheldProfile == null) _handheldProfile = BuildHandheldNoise();
                return _handheldProfile;
            }
        }

        /// <summary>Global handheld amplitude multiplier, 0 to lock the camera off entirely.</summary>
        public float HandheldScale
        {
            get => handheldScale;
            set => handheldScale = Mathf.Clamp(value, 0f, 2f);
        }

        private void Awake()
        {
            EnsureSource();
        }

        private void OnDisable()
        {
            // A hitch that is interrupted must not leave the game in slow motion.
            RestoreTimeScale();
        }

        private void OnDestroy()
        {
            RestoreTimeScale();
            if (_handheldProfile != null) Destroy(_handheldProfile);
        }

        private void EnsureSource()
        {
            if (_source != null) return;

            var go = new GameObject("~ImpulseSource") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            _source = go.AddComponent<CinemachineImpulseSource>();

            var def = _source.ImpulseDefinition;
            def.ImpulseChannel = 1;
            def.ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Recoil;
            def.ImpulseDuration = impactDuration;
            // Uniform: every camera in the rig receives the same signal regardless of how far
            // it is from the hit. Distance-attenuated shake reads as inconsistent when the
            // shot changes mid-beat, which it constantly does here.
            // In Uniform mode the envelope is derived from ImpulseDuration and the spatial
            // range is ignored, so ImpulseDuration and ImpulseShape are the only controls
            // that matter here — TimeEnvelope is legacy-mode only and is left alone.
            def.ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Uniform;
            def.AmplitudeGain = 1f;
            def.FrequencyGain = 1f;
            def.Randomize = true;
            _source.ImpulseDefinition = def;
            _source.DefaultVelocity = Vector3.down;
        }

        // --- Framing context ---------------------------------------------------------------

        /// <summary>
        /// Tells the director how much of the frame the subject currently fills, so it can back
        /// off before it starts shaking a near-full-screen sprite. Set by
        /// <see cref="BattleCameraRig"/> whenever the live shot changes.
        /// </summary>
        public void SetSubjectScreenFraction(float fraction)
            => _subjectScreenFraction = Mathf.Clamp(fraction, 0.05f, 1.5f);

        /// <summary>Per-shot shake multiplier, from <see cref="ShotProfile.ShakeGain"/>.</summary>
        public void SetShotGain(float gain) => _shotGain = Mathf.Clamp(gain, 0f, 3f);

        /// <summary>
        /// The ceiling that keeps a flat subject from being waggled.
        ///
        /// The failure this prevents is specific and it is the one the whole art pivot is
        /// sensitive to: translate a camera in front of a billboard that already fills the
        /// frame and every pixel of the creature moves together, rigidly, with no parallax and
        /// no internal motion — which is precisely what a cardboard cutout being shaken looks
        /// like. Below the threshold there is enough background in frame for the shake to read
        /// as the camera moving instead.
        /// </summary>
        private float SubjectFalloff()
        {
            float start = Mathf.Min(shakeFalloffStart, shakeFalloffEnd - 0.01f);
            if (_subjectScreenFraction <= start) return 1f;
            float t = Mathf.Clamp01((_subjectScreenFraction - start) / Mathf.Max(0.01f, shakeFalloffEnd - start));
            return Mathf.Lerp(1f, Mathf.Clamp01(shakeFloor), t);
        }

        // --- Impact ----------------------------------------------------------------------

        /// <summary>
        /// Shakes for a damage event. Amplitude scales with the fraction of the target's
        /// health bar removed, not with the raw number, so a big hit on a big creature and a
        /// big hit on a small one feel equally significant.
        /// </summary>
        /// <param name="worldPosition">Where the hit landed, used as the impulse origin.</param>
        /// <param name="direction">Direction the impact travelled, so the kick has a bearing.</param>
        /// <param name="damageFraction">Damage as a fraction of the target's max HP, 0-1.</param>
        /// <param name="critical">Applies the critical multiplier.</param>
        /// <param name="effectiveness">Applies the super-effective multiplier.</param>
        public void Impact(Vector3 worldPosition, Vector3 direction, float damageFraction,
            bool critical = false, Effectiveness effectiveness = Effectiveness.Neutral)
        {
            EnsureSource();

            float f = Mathf.Clamp01(damageFraction);
            // Square-rooted so small hits are not invisible: linear scaling makes a 6% chip
            // produce a shake nobody can see, and those are the majority of hits in a battle.
            float amplitude = Mathf.Lerp(minImpactAmplitude, maxImpactAmplitude, Mathf.Sqrt(f));
            if (critical) amplitude *= criticalMultiplier;
            if (effectiveness == Effectiveness.SuperEffective) amplitude *= superEffectiveMultiplier;
            if (effectiveness == Effectiveness.NotVeryEffective) amplitude *= 0.6f;
            amplitude *= SubjectFalloff();

            var def = _source.ImpulseDefinition;
            def.ImpulseDuration = Mathf.Lerp(impactDuration * 0.6f, impactDuration * 1.4f, f);
            _source.ImpulseDefinition = def;

            Vector3 dir = direction.sqrMagnitude > 1e-5f ? direction.normalized : Vector3.down;
            // Bias downward: a purely horizontal kick reads as a camera glitch, a downward
            // component reads as weight transferring into the ground.
            Vector3 velocity = (dir * 0.65f + Vector3.down * 0.85f).normalized * amplitude;

            _source.GenerateImpulseAtPositionWithVelocity(worldPosition, velocity);
        }

        /// <summary>A shake with no damage behind it — a landing, a ball hitting the ground, a stat drop.</summary>
        public void Bump(Vector3 worldPosition, float strength = 0.3f)
        {
            EnsureSource();
            float amplitude = Mathf.Max(0.02f, strength) * SubjectFalloff();
            _source.GenerateImpulseAtPositionWithVelocity(worldPosition, Vector3.down * amplitude);
        }

        /// <summary>
        /// Amplitude the director would produce for a hit right now, after the per-shot gain
        /// and the sprite ceiling. Diagnostic — used by the framing audit rather than by any
        /// beat, so a review can see the ceiling engaging instead of inferring it.
        /// </summary>
        public float PreviewAmplitude(float damageFraction, bool critical = false)
        {
            float a = Mathf.Lerp(minImpactAmplitude, maxImpactAmplitude, Mathf.Sqrt(Mathf.Clamp01(damageFraction)));
            if (critical) a *= criticalMultiplier;
            return a * SubjectFalloff() * _shotGain;
        }

        // --- Critical hitch --------------------------------------------------------------

        /// <summary>
        /// The critical-hit hitch: time all but stops for a beat, then ramps back.
        ///
        /// The ramp is the important half. Snapping time scale back to 1 produces a visible
        /// jolt in every physics-driven and animated object at once, which reads as a bug
        /// rather than as emphasis.
        /// </summary>
        public IEnumerator Hitch(float intensity = 1f)
        {
            if (!enableTimeHitch) yield break;

            if (_hitch != null) CinematicRunner.Halt(_hitch);
            _hitch = CinematicRunner.Run(HitchRoutine(Mathf.Clamp01(intensity)));
            yield return _hitch;
        }

        private IEnumerator HitchRoutine(float intensity)
        {
            _timeScaleBeforeHitch = Time.timeScale > 0.001f ? Time.timeScale : 1f;
            float floor = Mathf.Lerp(_timeScaleBeforeHitch, hitchTimeScale, intensity);

            Time.timeScale = floor;
            yield return CinematicRunner.Wait(hitchHold * Mathf.Lerp(0.5f, 1f, intensity));

            yield return CinematicRunner.Tween(hitchRecover, CinematicEase.OutCubic,
                p => Time.timeScale = Mathf.Lerp(floor, _timeScaleBeforeHitch, p));

            Time.timeScale = _timeScaleBeforeHitch;
            _hitch = null;
        }

        private void RestoreTimeScale()
        {
            if (_hitch == null) return;
            CinematicRunner.Halt(_hitch);
            _hitch = null;
            Time.timeScale = _timeScaleBeforeHitch;
        }

        // --- Noise -----------------------------------------------------------------------

        /// <summary>
        /// Builds a mild six-degree-of-freedom handheld profile.
        ///
        /// Deliberately asymmetric and non-harmonic frequencies. Matching frequencies across
        /// axes produce a visibly circular drift that the eye locks onto within a second or
        /// two; mismatched ones never visibly repeat.
        /// </summary>
        private static NoiseSettings BuildHandheldNoise()
        {
            var n = ScriptableObject.CreateInstance<NoiseSettings>();
            n.name = "~HandheldDrift";
            n.hideFlags = HideFlags.DontSave;

            n.PositionNoise = new[]
            {
                new NoiseSettings.TransformNoiseParams
                {
                    X = new NoiseSettings.NoiseParams { Frequency = 0.31f, Amplitude = 0.055f },
                    Y = new NoiseSettings.NoiseParams { Frequency = 0.43f, Amplitude = 0.040f },
                    Z = new NoiseSettings.NoiseParams { Frequency = 0.23f, Amplitude = 0.030f },
                },
            };

            n.OrientationNoise = new[]
            {
                new NoiseSettings.TransformNoiseParams
                {
                    X = new NoiseSettings.NoiseParams { Frequency = 0.27f, Amplitude = 0.42f },
                    Y = new NoiseSettings.NoiseParams { Frequency = 0.37f, Amplitude = 0.55f },
                    Z = new NoiseSettings.NoiseParams { Frequency = 0.19f, Amplitude = 0.30f },
                },
                // A second, slower orientation layer. One layer alone reads as a wobble;
                // two at different rates read as a person breathing.
                new NoiseSettings.TransformNoiseParams
                {
                    X = new NoiseSettings.NoiseParams { Frequency = 0.07f, Amplitude = 0.30f },
                    Y = new NoiseSettings.NoiseParams { Frequency = 0.05f, Amplitude = 0.36f },
                    Z = new NoiseSettings.NoiseParams { Frequency = 0.09f, Amplitude = 0.16f },
                },
            };

            return n;
        }
    }
}
