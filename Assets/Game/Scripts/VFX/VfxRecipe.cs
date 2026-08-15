using System;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>Which of the VFX shaders an emitter renders with.</summary>
    public enum VfxSurface
    {
        /// <summary>PokeLab/VFX/Particle. Sparks, flashes, rings, motes.</summary>
        Particle = 0,
        /// <summary>PokeLab/VFX/Gas. Lit smoke, steam, dust, mist.</summary>
        Gas = 1,
        /// <summary>PokeLab/VFX/EnergyTrail. Beams and stretched projectile trails.</summary>
        Trail = 2,
    }

    public enum VfxBlend
    {
        /// <summary>Adds light. The default for anything that glows.</summary>
        Additive = 0,
        /// <summary>Occludes. Smoke, dust, anything with mass.</summary>
        AlphaBlend = 1,
        /// <summary>Adds and occludes together. Good for fire, which does both.</summary>
        Premultiplied = 2,
    }

    public enum VfxShape
    {
        Point = 0,
        Sphere = 1,
        Hemisphere = 2,
        Cone = 3,
        /// <summary>Flat disc on XZ. Ground rings and shockwaves.</summary>
        Disc = 4,
        Box = 5,
        /// <summary>Hollow ring on XZ. Aura rings and expanding fronts.</summary>
        Ring = 6,
    }

    /// <summary>
    /// Named size and alpha profiles instead of raw AnimationCurves.
    ///
    /// Curves are painful to write in code and impossible to read back, and the
    /// half dozen shapes below cover everything the game needs. The factory turns
    /// them into real curves once, at build time.
    /// </summary>
    public enum VfxCurve
    {
        Constant = 0,
        /// <summary>0 to 1 over the life. Expanding rings, growing clouds.</summary>
        Grow = 1,
        /// <summary>1 to 0. Dying embers.</summary>
        Shrink = 2,
        /// <summary>Snaps up then eases away. Impact flashes.</summary>
        Pop = 3,
        /// <summary>Eases in and out. Motes and drifting debris.</summary>
        Swell = 4,
        /// <summary>Fast in, long tail. The default for anything additive.</summary>
        Flash = 5,
    }

    /// <summary>One particle system inside a composite effect.</summary>
    [Serializable]
    public sealed class VfxEmitter
    {
        public string Name = "Emitter";
        public ProceduralVfxTextures.Kind Texture = ProceduralVfxTextures.Kind.SoftDot;
        public VfxSurface Surface = VfxSurface.Particle;
        public VfxBlend Blend = VfxBlend.Additive;

        [Header("Colour")]
        public Color StartColor = Color.white;
        /// <summary>Second colour; each particle picks a random point between them.</summary>
        public Color StartColorSecondary = Color.white;
        /// <summary>Multiplies the HDR emission, which is what drives bloom.</summary>
        public float EmissionBoost = 1f;
        public VfxCurve AlphaCurve = VfxCurve.Flash;

        [Header("Timing")]
        public float Delay;
        public float Duration = 0.5f;
        public bool Looping;
        public float LifetimeMin = 0.3f;
        public float LifetimeMax = 0.6f;

        [Header("Emission")]
        public int Burst = 12;
        public float RateOverTime;
        /// <summary>Particles per metre travelled. For trails on a moving effect.</summary>
        public float RateOverDistance;

        [Header("Shape")]
        public VfxShape Shape = VfxShape.Sphere;
        public float ShapeRadius = 0.15f;
        public float ShapeThickness = 1f;
        public float ConeAngle = 25f;
        public Vector3 BoxSize = Vector3.one;
        public Vector3 Offset;

        [Header("Motion")]
        public float SpeedMin = 1f;
        public float SpeedMax = 2.5f;
        public float Gravity;
        public float Drag;
        public Vector3 ConstantForce;
        /// <summary>Degrees per second, randomly signed per particle.</summary>
        public float RotationSpeed;
        public float StartRotationRandom = 180f;
        /// <summary>Turbulence amplitude. Sells smoke and pollen more than anything else.</summary>
        public float NoiseStrength;
        public float NoiseFrequency = 0.4f;

        [Header("Size")]
        public float SizeMin = 0.2f;
        public float SizeMax = 0.4f;
        public VfxCurve SizeCurve = VfxCurve.Shrink;
        /// <summary>Stretches the billboard along its velocity. 0 keeps it round.</summary>
        public float VelocityStretch;

        [Header("Rendering")]
        public ParticleSystemRenderMode RenderMode = ParticleSystemRenderMode.Billboard;
        public ParticleSystemSimulationSpace SimulationSpace = ParticleSystemSimulationSpace.Local;
        public float SortingFudge;

        public static VfxEmitter Sparks(Color colour, int count, float speed, float life) =>
            new VfxEmitter
            {
                Name = "Sparks",
                Texture = ProceduralVfxTextures.Kind.Spark,
                Surface = VfxSurface.Particle,
                Blend = VfxBlend.Additive,
                StartColor = colour,
                StartColorSecondary = Color.Lerp(colour, Color.white, 0.55f),
                EmissionBoost = 2.4f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 0.25f,
                LifetimeMin = life * 0.6f,
                LifetimeMax = life,
                Burst = count,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.08f,
                SpeedMin = speed * 0.5f,
                SpeedMax = speed,
                Gravity = 1.6f,
                Drag = 1.2f,
                SizeMin = 0.06f,
                SizeMax = 0.14f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 2.4f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

        public static VfxEmitter Flash(Color colour, float size, float duration) =>
            new VfxEmitter
            {
                Name = "Flash",
                Texture = ProceduralVfxTextures.Kind.Glow,
                Surface = VfxSurface.Particle,
                Blend = VfxBlend.Additive,
                StartColor = colour,
                StartColorSecondary = colour,
                EmissionBoost = 3.2f,
                AlphaCurve = VfxCurve.Pop,
                Duration = 0.1f,
                LifetimeMin = duration,
                LifetimeMax = duration,
                Burst = 1,
                Shape = VfxShape.Point,
                SpeedMin = 0f,
                SpeedMax = 0f,
                SizeMin = size,
                SizeMax = size,
                SizeCurve = VfxCurve.Pop,
            };

        public static VfxEmitter Ring(Color colour, float size, float duration,
                                      ProceduralVfxTextures.Kind kind = ProceduralVfxTextures.Kind.ShockRing) =>
            new VfxEmitter
            {
                Name = "Ring",
                Texture = kind,
                Surface = VfxSurface.Particle,
                Blend = VfxBlend.Additive,
                StartColor = colour,
                StartColorSecondary = colour,
                EmissionBoost = 2.0f,
                AlphaCurve = VfxCurve.Shrink,
                Duration = 0.1f,
                LifetimeMin = duration,
                LifetimeMax = duration,
                Burst = 1,
                Shape = VfxShape.Point,
                SpeedMin = 0f,
                SpeedMax = 0f,
                SizeMin = size * 0.25f,
                SizeMax = size * 0.25f,
                SizeCurve = VfxCurve.Grow,
                StartRotationRandom = 0f,
                // Flat on the ground rather than facing the camera: a shockwave that
                // billboards reads as a bubble, not as a wave across the floor.
                RenderMode = ParticleSystemRenderMode.HorizontalBillboard,
            };

        public static VfxEmitter Smoke(Color colour, int count, float size, float life) =>
            new VfxEmitter
            {
                Name = "Smoke",
                Texture = ProceduralVfxTextures.Kind.Smoke,
                Surface = VfxSurface.Gas,
                Blend = VfxBlend.AlphaBlend,
                StartColor = colour,
                StartColorSecondary = Color.Lerp(colour, Color.black, 0.3f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Swell,
                Duration = 0.4f,
                LifetimeMin = life * 0.7f,
                LifetimeMax = life,
                Burst = count,
                Shape = VfxShape.Hemisphere,
                ShapeRadius = 0.25f,
                SpeedMin = 0.4f,
                SpeedMax = 1.1f,
                Gravity = -0.15f,
                Drag = 1.8f,
                RotationSpeed = 35f,
                NoiseStrength = 0.25f,
                NoiseFrequency = 0.35f,
                SizeMin = size * 0.7f,
                SizeMax = size,
                SizeCurve = VfxCurve.Grow,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };
    }

    /// <summary>
    /// A complete effect: one or more emitters, an optional light, and a lifetime
    /// after which the pool takes it back.
    /// </summary>
    [Serializable]
    public sealed class VfxRecipe
    {
        public string Id = "vfx";
        public VfxEmitter[] Emitters = Array.Empty<VfxEmitter>();

        [Header("Light")]
        /// <summary>A point light pulsed with the effect. Zero range means no light.</summary>
        public Color LightColor = Color.white;
        public float LightRange;
        public float LightIntensity = 2f;
        public float LightDuration = 0.25f;

        [Header("Lifetime")]
        /// <summary>Seconds before the instance returns to the pool. 0 derives it.</summary>
        public float TotalDuration;

        /// <summary>How many instances to keep warm. Impacts need more than casts.</summary>
        public int PoolPrewarm = 2;

        public float ResolveDuration()
        {
            if (TotalDuration > 1e-3f) return TotalDuration;

            float longest = 0.3f;
            if (Emitters != null)
            {
                for (int i = 0; i < Emitters.Length; i++)
                {
                    var e = Emitters[i];
                    if (e == null) continue;
                    longest = Mathf.Max(longest, e.Delay + e.Duration + e.LifetimeMax);
                }
            }
            return longest + 0.15f;
        }
    }
}
