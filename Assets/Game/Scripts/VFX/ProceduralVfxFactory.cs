using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Turns a VfxRecipe into a live GameObject with configured ParticleSystems.
    ///
    /// Effects are built in code rather than authored as prefabs on purpose. The
    /// integration contract reserves every .prefab for the integrator, and a
    /// ParticleSystem's serialised form is enormous, version-specific and
    /// impossible to review — one wrong field in a hand-written prefab is a silent
    /// visual bug nobody can find. Everything here is type-checked by the compiler
    /// instead, and a recipe reads as a paragraph of intent rather than as 400 lines
    /// of YAML.
    /// </summary>
    public static class ProceduralVfxFactory
    {
        public static GameObject Build(VfxRecipe recipe, Transform parent = null)
        {
            if (recipe == null) return null;

            var root = new GameObject("PL_Vfx_" + recipe.Id);
            root.hideFlags = HideFlags.DontSave;
            if (parent != null) root.transform.SetParent(parent, false);

            if (recipe.Emitters != null)
            {
                for (int i = 0; i < recipe.Emitters.Length; i++)
                {
                    var emitter = recipe.Emitters[i];
                    if (emitter == null) continue;
                    BuildEmitter(emitter, root.transform);
                }
            }

            if (recipe.LightRange > 0.01f)
                BuildLight(recipe, root.transform);

            return root;
        }

        private static void BuildEmitter(VfxEmitter e, Transform parent)
        {
            var go = new GameObject(e.Name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = e.Offset;

            var ps = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();

            // ParticleSystem modules are structs returned by value in the Unity API,
            // so each has to be written back through its own local. Assigning through
            // a property chain silently does nothing.
            var main = ps.main;
            main.duration = Mathf.Max(e.Duration, 0.05f);
            main.loop = e.Looping;
            main.startDelay = e.Delay;
            main.startLifetime = new ParticleSystem.MinMaxCurve(e.LifetimeMin, e.LifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(e.SpeedMin, e.SpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(e.SizeMin, e.SizeMax);
            main.startRotation = e.StartRotationRandom > 0.01f
                ? new ParticleSystem.MinMaxCurve(-e.StartRotationRandom * Mathf.Deg2Rad,
                                                 e.StartRotationRandom * Mathf.Deg2Rad)
                : new ParticleSystem.MinMaxCurve(0f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                Boost(e.StartColor, e.EmissionBoost),
                Boost(e.StartColorSecondary, e.EmissionBoost));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(e.Gravity);
            main.simulationSpace = e.SimulationSpace;
            main.playOnAwake = false;
            main.maxParticles = EstimateMaxParticles(e);
            main.cullingMode = ParticleSystemCullingMode.Automatic;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(e.RateOverTime);
            emission.rateOverDistance = new ParticleSystem.MinMaxCurve(e.RateOverDistance);
            if (e.Burst > 0)
            {
                emission.SetBursts(new[]
                {
                    new ParticleSystem.Burst(0f, (short)Mathf.Clamp(e.Burst, 1, 2000)),
                });
            }
            else
            {
                emission.SetBursts(System.Array.Empty<ParticleSystem.Burst>());
            }

            ConfigureShape(ps, e);

            if (e.Drag > 1e-3f || e.ConstantForce.sqrMagnitude > 1e-6f)
            {
                var limit = ps.limitVelocityOverLifetime;
                limit.enabled = e.Drag > 1e-3f;
                limit.dampen = 0f;
                limit.drag = new ParticleSystem.MinMaxCurve(e.Drag);
                limit.multiplyDragByParticleSize = true;
                limit.multiplyDragByParticleVelocity = true;

                var force = ps.forceOverLifetime;
                force.enabled = e.ConstantForce.sqrMagnitude > 1e-6f;
                force.space = ParticleSystemSimulationSpace.World;
                force.x = new ParticleSystem.MinMaxCurve(e.ConstantForce.x);
                force.y = new ParticleSystem.MinMaxCurve(e.ConstantForce.y);
                force.z = new ParticleSystem.MinMaxCurve(e.ConstantForce.z);
            }

            if (e.RotationSpeed > 1e-3f)
            {
                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-e.RotationSpeed * Mathf.Deg2Rad,
                                                       e.RotationSpeed * Mathf.Deg2Rad);
            }

            if (e.NoiseStrength > 1e-3f)
            {
                // Turbulence is the single cheapest thing that stops particles looking
                // like they are on rails. Quality is deliberately low: at these sizes
                // nobody can tell, and it halves the cost.
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = new ParticleSystem.MinMaxCurve(e.NoiseStrength);
                noise.frequency = e.NoiseFrequency;
                noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.35f);
                noise.damping = true;
                noise.quality = ParticleSystemNoiseQuality.Low;
            }

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = e.SizeCurve != VfxCurve.Constant;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, CurveFor(e.SizeCurve));

            var colourOverLife = ps.colorOverLifetime;
            colourOverLife.enabled = true;
            colourOverLife.color = new ParticleSystem.MinMaxGradient(GradientFor(e.AlphaCurve));

            // --- Renderer -----------------------------------------------------
            // sharedMaterial, not material: the getter on `material` instantiates a
            // per-renderer copy, and a hundred copies of the same additive material
            // is a hundred draw calls that should have been one.
            renderer.sharedMaterial = VfxMaterialLibrary.Get(e.Surface, e.Blend, e.Texture);
            renderer.renderMode = e.RenderMode;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sortingFudge = e.SortingFudge;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            if (e.RenderMode == ParticleSystemRenderMode.Stretch)
            {
                renderer.velocityScale = e.VelocityStretch;
                renderer.lengthScale = Mathf.Max(e.VelocityStretch * 0.5f, 1f);
                renderer.cameraVelocityScale = 0f;
            }

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private static void ConfigureShape(ParticleSystem ps, VfxEmitter e)
        {
            var shape = ps.shape;
            shape.enabled = e.Shape != VfxShape.Point;
            shape.radiusThickness = Mathf.Clamp01(e.ShapeThickness);
            shape.radius = Mathf.Max(e.ShapeRadius, 0.0001f);

            switch (e.Shape)
            {
                case VfxShape.Sphere:
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    break;
                case VfxShape.Hemisphere:
                    shape.shapeType = ParticleSystemShapeType.Hemisphere;
                    break;
                case VfxShape.Cone:
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = e.ConeAngle;
                    // Cones point down +Z by default and every anchor in this project
                    // faces +Z by the art contract, so no rotation is needed.
                    break;
                case VfxShape.Disc:
                    shape.shapeType = ParticleSystemShapeType.Circle;
                    shape.radiusThickness = 1f;
                    shape.rotation = new Vector3(90f, 0f, 0f);
                    break;
                case VfxShape.Ring:
                    shape.shapeType = ParticleSystemShapeType.Circle;
                    shape.radiusThickness = 0f;   // emit from the rim only
                    shape.rotation = new Vector3(90f, 0f, 0f);
                    break;
                case VfxShape.Box:
                    shape.shapeType = ParticleSystemShapeType.Box;
                    shape.scale = e.BoxSize;
                    break;
            }
        }

        private static void BuildLight(VfxRecipe recipe, Transform parent)
        {
            // A real light on an impact is what makes the effect belong to the scene
            // rather than sit in front of it. One per effect, short-lived, and range
            // limited so it never becomes an expensive additional light.
            var go = new GameObject("Light");
            go.transform.SetParent(parent, false);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = recipe.LightColor;
            light.range = recipe.LightRange;
            light.intensity = recipe.LightIntensity;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.Auto;

            var pulse = go.AddComponent<VfxLightPulse>();
            pulse.Configure(recipe.LightIntensity, Mathf.Max(recipe.LightDuration, 0.05f));
        }

        private static int EstimateMaxParticles(VfxEmitter e)
        {
            float continuous = e.RateOverTime * e.LifetimeMax;
            float distance = e.RateOverDistance * 12f;   // assume ~12 m of travel
            int total = Mathf.CeilToInt(continuous + distance) + Mathf.Max(e.Burst, 0);
            // A hard ceiling matters: a recipe with a typo in its rate should cost a
            // frame, not the whole budget.
            return Mathf.Clamp(total + 4, 4, 900);
        }

        private static Color Boost(Color c, float boost)
        {
            // Emission lives in the colour's RGB above 1, which is what the HDR
            // buffer and the bloom threshold pick up. Alpha is left alone: it is the
            // particle's coverage, not its brightness.
            float k = Mathf.Max(boost, 0f);
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        // ---------------------------------------------------------------------
        // Curve and gradient shapes
        // ---------------------------------------------------------------------
        private static AnimationCurve CurveFor(VfxCurve kind)
        {
            switch (kind)
            {
                case VfxCurve.Grow:
                    return new AnimationCurve(
                        new Keyframe(0f, 0.15f, 2f, 2f),
                        new Keyframe(1f, 1f, 0.2f, 0.2f));

                case VfxCurve.Shrink:
                    return new AnimationCurve(
                        new Keyframe(0f, 1f, 0f, 0f),
                        new Keyframe(1f, 0f, -1.4f, -1.4f));

                case VfxCurve.Pop:
                    return new AnimationCurve(
                        new Keyframe(0f, 0.2f, 8f, 8f),
                        new Keyframe(0.18f, 1f, 0f, 0f),
                        new Keyframe(1f, 0.05f, -1f, -1f));

                case VfxCurve.Swell:
                    return new AnimationCurve(
                        new Keyframe(0f, 0.35f, 1.5f, 1.5f),
                        new Keyframe(0.45f, 1f, 0f, 0f),
                        new Keyframe(1f, 0.7f, -0.6f, -0.6f));

                case VfxCurve.Flash:
                    return new AnimationCurve(
                        new Keyframe(0f, 1f, 0f, 0f),
                        new Keyframe(0.25f, 0.6f, -1.5f, -1.5f),
                        new Keyframe(1f, 0f, -0.6f, -0.6f));

                default:
                    return AnimationCurve.Constant(0f, 1f, 1f);
            }
        }

        private static Gradient GradientFor(VfxCurve kind)
        {
            var gradient = new Gradient();
            // Colour keys stay white: the particle's start colour already carries the
            // tint, and multiplying two gradients only makes an effect harder to tune.
            var colourKeys = new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f),
            };

            GradientAlphaKey[] alphaKeys;
            switch (kind)
            {
                case VfxCurve.Grow:
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(1f, 0.25f),
                        new GradientAlphaKey(0f, 1f),
                    };
                    break;

                case VfxCurve.Pop:
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 0.12f),
                        new GradientAlphaKey(0f, 1f),
                    };
                    break;

                case VfxCurve.Swell:
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(0.85f, 0.3f),
                        new GradientAlphaKey(0.7f, 0.6f),
                        new GradientAlphaKey(0f, 1f),
                    };
                    break;

                case VfxCurve.Shrink:
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0f, 1f),
                    };
                    break;

                case VfxCurve.Constant:
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 0.9f),
                        new GradientAlphaKey(0f, 1f),
                    };
                    break;

                default:   // Flash
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0.75f, 0.2f),
                        new GradientAlphaKey(0f, 1f),
                    };
                    break;
            }

            gradient.SetKeys(colourKeys, alphaKeys);
            return gradient;
        }
    }

    /// <summary>
    /// Fades an effect's point light out over its lifetime and disables it when it
    /// stops contributing. Without this an impact light would stay at full strength
    /// for as long as the effect object is alive, which reads as a lamp rather than
    /// as a flash.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public sealed class VfxLightPulse : MonoBehaviour
    {
        private Light target;
        private float peakIntensity = 2f;
        private float duration = 0.25f;
        private float elapsed;

        public void Configure(float intensity, float lifetime)
        {
            peakIntensity = intensity;
            duration = Mathf.Max(lifetime, 0.05f);
        }

        private void OnEnable()
        {
            if (target == null) target = GetComponent<Light>();
            elapsed = 0f;
            target.enabled = true;
            target.intensity = peakIntensity;
        }

        private void Update()
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            // Squared falloff: bright for the first instant, then gone. A linear fade
            // reads as a dimmer being turned down.
            float k = 1f - t;
            target.intensity = peakIntensity * k * k;
            if (t >= 1f && target.enabled) target.enabled = false;
        }
    }
}
