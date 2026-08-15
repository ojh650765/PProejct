using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// The environmental activity that makes the world feel inhabited.
    ///
    /// These sell the place more than any hero effect does. A still route reads as
    /// a diorama no matter how good the terrain shader is; the same route with
    /// pollen in the sunbeams, leaves crossing the path, birds over the treeline
    /// and fireflies after dark reads as somewhere that exists.
    ///
    /// Everything here is deliberately cheap and long-lived: low emission rates,
    /// small particles, world-space simulation so an effect can be parented to the
    /// camera rig and follow the player without its existing particles snapping
    /// along with it.
    /// </summary>
    public static class AmbientVfxLibrary
    {
        public const string KeyPollen = "ambient.pollen";
        public const string KeyLeaves = "ambient.leaves";
        public const string KeyButterflies = "ambient.butterflies";
        public const string KeyBirds = "ambient.birds";
        public const string KeySunbeamDust = "ambient.sunbeamdust";
        public const string KeyWaterfallMist = "ambient.waterfallmist";
        public const string KeyWaterSplash = "ambient.watersplash";
        public const string KeyRain = "ambient.rain";
        public const string KeyRainRipples = "ambient.rainripples";
        public const string KeyRainSplash = "ambient.rainsplash";
        public const string KeyWindGust = "ambient.windgust";
        public const string KeyCampfireSmoke = "ambient.campfiresmoke";
        public const string KeyCampfireFlame = "ambient.campfireflame";
        public const string KeyCaveDrips = "ambient.cavedrips";
        public const string KeyFireflies = "ambient.fireflies";
        public const string KeySnow = "ambient.snow";
        public const string KeySandstorm = "ambient.sandstorm";

        public static void AddAll(Dictionary<string, VfxRecipe> into)
        {
            into[KeyPollen] = Pollen();
            into[KeyLeaves] = Leaves();
            into[KeyButterflies] = Butterflies();
            into[KeyBirds] = Birds();
            into[KeySunbeamDust] = SunbeamDust();
            into[KeyWaterfallMist] = WaterfallMist();
            into[KeyWaterSplash] = WaterSplash();
            into[KeyRain] = Rain();
            into[KeyRainRipples] = RainRipples();
            into[KeyRainSplash] = RainSplash();
            into[KeyWindGust] = WindGust();
            into[KeyCampfireSmoke] = CampfireSmoke();
            into[KeyCampfireFlame] = CampfireFlame();
            into[KeyCaveDrips] = CaveDrips();
            into[KeyFireflies] = Fireflies();
            into[KeySnow] = Snow();
            into[KeySandstorm] = Sandstorm();
        }

        /// <summary>
        /// A volume of ambient particles centred on the player. Box-shaped and
        /// world-simulated: the emitter follows the camera, the particles do not.
        /// </summary>
        private static VfxEmitter AmbientVolume(string name, ProceduralVfxTextures.Kind texture,
                                                Color colour, float rate, Vector3 size,
                                                float sizeMin, float sizeMax, float life)
        {
            return new VfxEmitter
            {
                Name = name,
                Texture = texture,
                Blend = VfxBlend.Additive,
                StartColor = colour,
                StartColorSecondary = Color.Lerp(colour, Color.white, 0.35f),
                EmissionBoost = 1.4f,
                AlphaCurve = VfxCurve.Swell,
                Looping = true,
                Duration = 5f,
                LifetimeMin = life * 0.6f,
                LifetimeMax = life,
                Burst = 0,
                RateOverTime = rate,
                Shape = VfxShape.Box,
                BoxSize = size,
                SpeedMin = 0.05f,
                SpeedMax = 0.25f,
                Drag = 0.3f,
                SizeMin = sizeMin,
                SizeMax = sizeMax,
                SizeCurve = VfxCurve.Swell,
                NoiseStrength = 0.35f,
                NoiseFrequency = 0.12f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };
        }

        private static VfxRecipe Persistent(string id, int prewarm, params VfxEmitter[] emitters) =>
            new VfxRecipe { Id = id, Emitters = emitters, TotalDuration = 0f, PoolPrewarm = prewarm };

        // ---------------------------------------------------------------------

        private static VfxRecipe Pollen()
        {
            var motes = AmbientVolume("Pollen", ProceduralVfxTextures.Kind.SoftDot,
                                      new Color(1.0f, 0.94f, 0.62f), 14f,
                                      new Vector3(26f, 8f, 26f), 0.02f, 0.055f, 9f);
            // Barely rising. Pollen that falls reads as dust; pollen that hangs and
            // drifts upward reads as a warm afternoon.
            motes.Gravity = -0.02f;
            motes.NoiseStrength = 0.22f;

            var bigger = AmbientVolume("Seeds", ProceduralVfxTextures.Kind.Petal,
                                       new Color(1.0f, 0.98f, 0.85f), 2.2f,
                                       new Vector3(24f, 6f, 24f), 0.05f, 0.11f, 12f);
            bigger.RotationSpeed = 45f;
            bigger.Gravity = 0.03f;

            return Persistent(KeyPollen, 1, motes, bigger);
        }

        private static VfxRecipe Leaves()
        {
            var leaves = AmbientVolume("Leaves", ProceduralVfxTextures.Kind.Leaf,
                                       new Color(0.62f, 0.78f, 0.34f), 3.5f,
                                       new Vector3(22f, 9f, 22f), 0.09f, 0.17f, 10f);
            leaves.Blend = VfxBlend.AlphaBlend;
            leaves.EmissionBoost = 1f;
            leaves.Gravity = 0.22f;
            leaves.Drag = 1.4f;
            leaves.RotationSpeed = 130f;
            leaves.NoiseStrength = 0.7f;
            leaves.NoiseFrequency = 0.25f;
            // Blown along the same axis the foliage shader sways on.
            leaves.ConstantForce = new Vector3(0.9f, 0f, 0.35f);

            return Persistent(KeyLeaves, 1, leaves);
        }

        private static VfxRecipe Butterflies()
        {
            var wings = AmbientVolume("Butterflies", ProceduralVfxTextures.Kind.Wing,
                                      new Color(1.0f, 0.78f, 0.95f), 0.6f,
                                      new Vector3(16f, 3f, 16f), 0.10f, 0.16f, 14f);
            wings.Blend = VfxBlend.AlphaBlend;
            wings.EmissionBoost = 1f;
            wings.Offset = new Vector3(0f, 1.0f, 0f);
            wings.SpeedMin = 0.4f;
            wings.SpeedMax = 0.9f;
            wings.Drag = 0.9f;
            wings.RotationSpeed = 60f;
            // High-frequency turbulence is what makes them flutter rather than glide.
            wings.NoiseStrength = 1.4f;
            wings.NoiseFrequency = 1.1f;

            return Persistent(KeyButterflies, 1, wings);
        }

        private static VfxRecipe Birds()
        {
            var birds = AmbientVolume("Birds", ProceduralVfxTextures.Kind.Wing,
                                      new Color(0.28f, 0.30f, 0.36f), 0.25f,
                                      new Vector3(60f, 6f, 60f), 0.35f, 0.6f, 22f);
            birds.Blend = VfxBlend.AlphaBlend;
            birds.EmissionBoost = 1f;
            birds.Offset = new Vector3(0f, 22f, 0f);
            birds.SpeedMin = 3.5f;
            birds.SpeedMax = 6.5f;
            birds.Drag = 0f;
            birds.NoiseStrength = 0.5f;
            birds.NoiseFrequency = 0.15f;
            birds.ConstantForce = new Vector3(2.5f, 0f, 1.2f);
            birds.AlphaCurve = VfxCurve.Swell;

            return Persistent(KeyBirds, 1, birds);
        }

        private static VfxRecipe SunbeamDust()
        {
            // Placed by hand inside a light shaft. Very small, very slow, additive:
            // it only shows where the beam is, which is exactly the effect.
            var dust = AmbientVolume("Dust", ProceduralVfxTextures.Kind.SoftDot,
                                     new Color(1.0f, 0.95f, 0.80f), 26f,
                                     new Vector3(4f, 6f, 4f), 0.012f, 0.035f, 8f);
            dust.EmissionBoost = 2.6f;
            dust.SpeedMin = 0.02f;
            dust.SpeedMax = 0.09f;
            dust.Gravity = 0.015f;
            dust.NoiseStrength = 0.1f;
            dust.NoiseFrequency = 0.08f;

            return Persistent(KeySunbeamDust, 1, dust);
        }

        private static VfxRecipe WaterfallMist()
        {
            var mist = new VfxEmitter
            {
                Name = "Mist",
                Texture = ProceduralVfxTextures.Kind.Puff,
                Surface = VfxSurface.Gas,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.92f, 0.96f, 1.0f),
                StartColorSecondary = new Color(0.78f, 0.86f, 0.95f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Swell,
                Looping = true,
                Duration = 4f,
                LifetimeMin = 1.6f,
                LifetimeMax = 3.2f,
                RateOverTime = 22f,
                Shape = VfxShape.Box,
                BoxSize = new Vector3(3f, 0.5f, 1.2f),
                SpeedMin = 0.5f,
                SpeedMax = 1.6f,
                Gravity = -0.25f,
                Drag = 1.2f,
                SizeMin = 1.2f,
                SizeMax = 2.6f,
                SizeCurve = VfxCurve.Grow,
                RotationSpeed = 22f,
                NoiseStrength = 0.5f,
                NoiseFrequency = 0.2f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var spray = new VfxEmitter
            {
                Name = "Spray",
                Texture = ProceduralVfxTextures.Kind.Droplet,
                Blend = VfxBlend.Additive,
                StartColor = new Color(0.9f, 0.97f, 1.0f),
                StartColorSecondary = Color.white,
                EmissionBoost = 1.8f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 4f,
                LifetimeMin = 0.5f,
                LifetimeMax = 1.1f,
                RateOverTime = 45f,
                Shape = VfxShape.Cone,
                ConeAngle = 42f,
                ShapeRadius = 1.4f,
                SpeedMin = 1.5f,
                SpeedMax = 4.0f,
                Gravity = 2.2f,
                Drag = 0.6f,
                SizeMin = 0.03f,
                SizeMax = 0.08f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 1.8f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            return Persistent(KeyWaterfallMist, 1, mist, spray);
        }

        private static VfxRecipe WaterSplash()
        {
            var crown = new VfxEmitter
            {
                Name = "Crown",
                Texture = ProceduralVfxTextures.Kind.Splash,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.85f, 0.94f, 1.0f),
                StartColorSecondary = Color.white,
                EmissionBoost = 1.3f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 0.08f,
                LifetimeMin = 0.28f,
                LifetimeMax = 0.42f,
                Burst = 4,
                Shape = VfxShape.Ring,
                ShapeRadius = 0.12f,
                SpeedMin = 0.4f,
                SpeedMax = 0.9f,
                SizeMin = 0.3f,
                SizeMax = 0.6f,
                SizeCurve = VfxCurve.Grow,
                RenderMode = ParticleSystemRenderMode.Billboard,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var droplets = new VfxEmitter
            {
                Name = "Droplets",
                Texture = ProceduralVfxTextures.Kind.Droplet,
                Blend = VfxBlend.Additive,
                StartColor = new Color(0.88f, 0.96f, 1.0f),
                StartColorSecondary = Color.white,
                EmissionBoost = 1.6f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 0.08f,
                LifetimeMin = 0.35f,
                LifetimeMax = 0.7f,
                Burst = 14,
                Shape = VfxShape.Cone,
                ConeAngle = 35f,
                ShapeRadius = 0.1f,
                SpeedMin = 1.6f,
                SpeedMax = 3.6f,
                Gravity = 2.6f,
                SizeMin = 0.035f,
                SizeMax = 0.08f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 2.0f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var ripple = VfxEmitter.Ring(new Color(0.9f, 0.97f, 1.0f), 1.1f, 0.55f,
                                         ProceduralVfxTextures.Kind.Ring);
            ripple.Blend = VfxBlend.AlphaBlend;
            ripple.EmissionBoost = 1f;

            return new VfxRecipe
            {
                Id = KeyWaterSplash,
                Emitters = new[] { ripple, crown, droplets },
                TotalDuration = 1.0f,
                PoolPrewarm = 4,
            };
        }

        private static VfxRecipe Rain()
        {
            var drops = new VfxEmitter
            {
                Name = "Rain",
                Texture = ProceduralVfxTextures.Kind.Droplet,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.72f, 0.80f, 0.90f, 0.55f),
                StartColorSecondary = new Color(0.85f, 0.90f, 0.98f, 0.75f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Constant,
                Looping = true,
                Duration = 4f,
                LifetimeMin = 0.9f,
                LifetimeMax = 1.3f,
                RateOverTime = 420f,
                Shape = VfxShape.Box,
                BoxSize = new Vector3(26f, 0.5f, 26f),
                Offset = new Vector3(0f, 14f, 0f),
                SpeedMin = 13f,
                SpeedMax = 17f,
                ConstantForce = new Vector3(-2.2f, 0f, -0.8f),
                SizeMin = 0.02f,
                SizeMax = 0.045f,
                SizeCurve = VfxCurve.Constant,
                // Long stretch is what turns a dot into a rain streak.
                VelocityStretch = 6.5f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
                StartRotationRandom = 0f,
            };

            return Persistent(KeyRain, 1, drops);
        }

        private static VfxRecipe RainRipples()
        {
            // Flat expanding rings on the ground. Placed on the terrain surface by
            // the ambient controller; this is what makes rain read as landing rather
            // than as falling past the camera.
            var ripples = new VfxEmitter
            {
                Name = "Ripples",
                Texture = ProceduralVfxTextures.Kind.Ring,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.85f, 0.92f, 1.0f, 0.5f),
                StartColorSecondary = new Color(1f, 1f, 1f, 0.35f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Shrink,
                Looping = true,
                Duration = 3f,
                LifetimeMin = 0.5f,
                LifetimeMax = 0.85f,
                RateOverTime = 55f,
                Shape = VfxShape.Disc,
                ShapeRadius = 11f,
                SpeedMin = 0f,
                SpeedMax = 0f,
                SizeMin = 0.06f,
                SizeMax = 0.13f,
                SizeCurve = VfxCurve.Grow,
                RenderMode = ParticleSystemRenderMode.HorizontalBillboard,
                SimulationSpace = ParticleSystemSimulationSpace.World,
                StartRotationRandom = 0f,
            };

            return Persistent(KeyRainRipples, 1, ripples);
        }

        private static VfxRecipe RainSplash()
        {
            var splash = new VfxEmitter
            {
                Name = "Splash",
                Texture = ProceduralVfxTextures.Kind.Spark,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.85f, 0.92f, 1.0f, 0.6f),
                StartColorSecondary = Color.white,
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 3f,
                LifetimeMin = 0.16f,
                LifetimeMax = 0.28f,
                RateOverTime = 90f,
                Shape = VfxShape.Disc,
                ShapeRadius = 10f,
                SpeedMin = 1.2f,
                SpeedMax = 2.4f,
                Gravity = 3.5f,
                SizeMin = 0.015f,
                SizeMax = 0.035f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 2.2f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            return Persistent(KeyRainSplash, 1, splash);
        }

        private static VfxRecipe WindGust()
        {
            // Visible air: a sheet of faint streaks crossing the grass. Triggered by
            // the ambient controller in step with the shader's gust global, so the
            // grass bends and the streaks pass at the same moment.
            var streaks = new VfxEmitter
            {
                Name = "Streaks",
                Texture = ProceduralVfxTextures.Kind.Spark,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(1f, 1f, 1f, 0.16f),
                StartColorSecondary = new Color(0.9f, 0.95f, 1f, 0.10f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Swell,
                Duration = 0.8f,
                LifetimeMin = 0.8f,
                LifetimeMax = 1.4f,
                Burst = 18,
                RateOverTime = 14f,
                Shape = VfxShape.Box,
                BoxSize = new Vector3(2f, 1.6f, 14f),
                Offset = new Vector3(0f, 0.8f, 0f),
                SpeedMin = 7f,
                SpeedMax = 12f,
                Drag = 0.3f,
                SizeMin = 0.08f,
                SizeMax = 0.2f,
                SizeCurve = VfxCurve.Swell,
                VelocityStretch = 5f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
                NoiseStrength = 0.4f,
                NoiseFrequency = 0.3f,
            };

            var debris = new VfxEmitter
            {
                Name = "Debris",
                Texture = ProceduralVfxTextures.Kind.Leaf,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.65f, 0.72f, 0.38f),
                StartColorSecondary = new Color(0.78f, 0.68f, 0.35f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Swell,
                Duration = 0.8f,
                LifetimeMin = 1.2f,
                LifetimeMax = 2.2f,
                Burst = 8,
                Shape = VfxShape.Box,
                BoxSize = new Vector3(2f, 1f, 12f),
                Offset = new Vector3(0f, 0.4f, 0f),
                SpeedMin = 5f,
                SpeedMax = 9f,
                Gravity = 0.35f,
                Drag = 1.1f,
                RotationSpeed = 220f,
                SizeMin = 0.09f,
                SizeMax = 0.16f,
                SizeCurve = VfxCurve.Constant,
                NoiseStrength = 0.9f,
                NoiseFrequency = 0.5f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            return new VfxRecipe
            {
                Id = KeyWindGust,
                Emitters = new[] { streaks, debris },
                TotalDuration = 3.2f,
                PoolPrewarm = 2,
            };
        }

        private static VfxRecipe CampfireSmoke()
        {
            var smoke = new VfxEmitter
            {
                Name = "Smoke",
                Texture = ProceduralVfxTextures.Kind.Smoke,
                Surface = VfxSurface.Gas,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.34f, 0.33f, 0.36f),
                StartColorSecondary = new Color(0.20f, 0.19f, 0.22f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Swell,
                Looping = true,
                Duration = 4f,
                LifetimeMin = 2.5f,
                LifetimeMax = 4.5f,
                RateOverTime = 9f,
                Shape = VfxShape.Cone,
                ConeAngle = 9f,
                ShapeRadius = 0.16f,
                SpeedMin = 0.7f,
                SpeedMax = 1.4f,
                Gravity = -0.35f,
                Drag = 0.9f,
                SizeMin = 0.5f,
                SizeMax = 0.9f,
                SizeCurve = VfxCurve.Grow,
                RotationSpeed = 18f,
                // Turbulence rising with height is what gives a smoke column its
                // characteristic widening wobble.
                NoiseStrength = 0.55f,
                NoiseFrequency = 0.22f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            return Persistent(KeyCampfireSmoke, 1, smoke);
        }

        private static VfxRecipe CampfireFlame()
        {
            var flames = new VfxEmitter
            {
                Name = "Flames",
                Texture = ProceduralVfxTextures.Kind.Flame,
                Blend = VfxBlend.Premultiplied,
                StartColor = new Color(1.0f, 0.48f, 0.14f),
                StartColorSecondary = new Color(1.0f, 0.82f, 0.35f),
                EmissionBoost = 2.6f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 2f,
                LifetimeMin = 0.35f,
                LifetimeMax = 0.7f,
                RateOverTime = 26f,
                Shape = VfxShape.Cone,
                ConeAngle = 14f,
                ShapeRadius = 0.18f,
                SpeedMin = 0.8f,
                SpeedMax = 1.6f,
                Gravity = -0.5f,
                Drag = 1.4f,
                SizeMin = 0.24f,
                SizeMax = 0.45f,
                SizeCurve = VfxCurve.Shrink,
                NoiseStrength = 0.35f,
                NoiseFrequency = 1.1f,
                StartRotationRandom = 0f,
                SimulationSpace = ParticleSystemSimulationSpace.Local,
            };

            var embers = new VfxEmitter
            {
                Name = "Embers",
                Texture = ProceduralVfxTextures.Kind.SoftDot,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.55f, 0.18f),
                StartColorSecondary = new Color(1.0f, 0.85f, 0.5f),
                EmissionBoost = 2.4f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 3f,
                LifetimeMin = 1.0f,
                LifetimeMax = 2.2f,
                RateOverTime = 7f,
                Shape = VfxShape.Cone,
                ConeAngle = 22f,
                ShapeRadius = 0.2f,
                SpeedMin = 1.0f,
                SpeedMax = 2.2f,
                Gravity = -0.55f,
                Drag = 0.7f,
                SizeMin = 0.025f,
                SizeMax = 0.06f,
                SizeCurve = VfxCurve.Shrink,
                NoiseStrength = 0.8f,
                NoiseFrequency = 0.55f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var recipe = Persistent(KeyCampfireFlame, 1, flames, embers);
            recipe.LightColor = new Color(1f, 0.55f, 0.2f);
            recipe.LightRange = 8f;
            recipe.LightIntensity = 3.2f;
            recipe.LightDuration = 999f;
            return recipe;
        }

        private static VfxRecipe CaveDrips()
        {
            var drips = new VfxEmitter
            {
                Name = "Drips",
                Texture = ProceduralVfxTextures.Kind.Droplet,
                Blend = VfxBlend.Additive,
                StartColor = new Color(0.55f, 0.75f, 0.95f),
                StartColorSecondary = new Color(0.8f, 0.9f, 1.0f),
                EmissionBoost = 1.8f,
                AlphaCurve = VfxCurve.Constant,
                Looping = true,
                Duration = 5f,
                LifetimeMin = 1.2f,
                LifetimeMax = 2.0f,
                RateOverTime = 2.5f,
                Shape = VfxShape.Box,
                BoxSize = new Vector3(10f, 0.4f, 10f),
                Offset = new Vector3(0f, 4f, 0f),
                SpeedMin = 0.2f,
                SpeedMax = 0.5f,
                Gravity = 1.6f,
                SizeMin = 0.03f,
                SizeMax = 0.06f,
                SizeCurve = VfxCurve.Constant,
                VelocityStretch = 3.5f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
                StartRotationRandom = 0f,
            };

            return Persistent(KeyCaveDrips, 1, drips);
        }

        private static VfxRecipe Fireflies()
        {
            var flies = AmbientVolume("Fireflies", ProceduralVfxTextures.Kind.Glow,
                                      new Color(0.75f, 1.0f, 0.42f), 3.5f,
                                      new Vector3(20f, 3f, 20f), 0.05f, 0.11f, 8f);
            flies.EmissionBoost = 3.4f;
            flies.Offset = new Vector3(0f, 0.9f, 0f);
            flies.SpeedMin = 0.2f;
            flies.SpeedMax = 0.6f;
            flies.Drag = 1.2f;
            // Their signature is the blink, which comes from a Swell alpha curve on a
            // short lifetime rather than from any per-particle animation.
            flies.AlphaCurve = VfxCurve.Swell;
            flies.LifetimeMin = 1.4f;
            flies.LifetimeMax = 3.0f;
            flies.NoiseStrength = 1.1f;
            flies.NoiseFrequency = 0.45f;

            return Persistent(KeyFireflies, 1, flies);
        }

        private static VfxRecipe Snow()
        {
            var flakes = AmbientVolume("Snow", ProceduralVfxTextures.Kind.SoftDot,
                                       new Color(1f, 1f, 1f), 90f,
                                       new Vector3(26f, 0.5f, 26f), 0.03f, 0.07f, 6f);
            flakes.Blend = VfxBlend.AlphaBlend;
            flakes.EmissionBoost = 1f;
            flakes.Offset = new Vector3(0f, 12f, 0f);
            flakes.SpeedMin = 0.8f;
            flakes.SpeedMax = 1.6f;
            flakes.Gravity = 0.25f;
            flakes.Drag = 1.6f;
            flakes.AlphaCurve = VfxCurve.Constant;
            flakes.NoiseStrength = 0.9f;
            flakes.NoiseFrequency = 0.3f;

            return Persistent(KeySnow, 1, flakes);
        }

        private static VfxRecipe Sandstorm()
        {
            var sand = AmbientVolume("Sand", ProceduralVfxTextures.Kind.Smoke,
                                     new Color(0.82f, 0.70f, 0.46f), 26f,
                                     new Vector3(4f, 8f, 26f), 1.6f, 3.4f, 4f);
            sand.Surface = VfxSurface.Gas;
            sand.Blend = VfxBlend.AlphaBlend;
            sand.EmissionBoost = 1f;
            sand.SpeedMin = 6f;
            sand.SpeedMax = 11f;
            sand.Drag = 0.2f;
            sand.RotationSpeed = 30f;
            sand.NoiseStrength = 1.2f;
            sand.NoiseFrequency = 0.2f;

            return Persistent(KeySandstorm, 1, sand);
        }
    }
}
