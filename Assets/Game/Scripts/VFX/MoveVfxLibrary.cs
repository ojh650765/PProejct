using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>Which beat of a move an effect belongs to.</summary>
    public enum VfxPhase
    {
        /// <summary>Charge-up at the attacker's muzzle. Telegraphs the type.</summary>
        Cast = 0,
        /// <summary>The projectile itself, travelling from muzzle to target.</summary>
        Travel = 1,
        /// <summary>The hit at the target's body.</summary>
        Impact = 2,
    }

    /// <summary>
    /// The elemental palette and the recipes built from it.
    ///
    /// Every type gets a distinct cast, travel and impact so a projectile reads as
    /// a real trajectory rather than as a flash at each end. The three phases share
    /// the type's palette, which is what makes an attack read as one event: you see
    /// the same colour gather, fly and land.
    ///
    /// The palette is the important part. It is the only place a type's colour is
    /// defined, and the UI, the scanner and the audio worker can all be pointed at
    /// it so a Fire move is the same orange everywhere in the game.
    /// </summary>
    public static class MoveVfxLibrary
    {
        // ---------------------------------------------------------------------
        // Palette. Values above 1 are intentional: they are what reaches bloom.
        // ---------------------------------------------------------------------
        public static Color PrimaryColor(ElementType type)
        {
            switch (type)
            {
                case ElementType.Normal:   return new Color(1.00f, 0.96f, 0.86f);
                case ElementType.Fire:     return new Color(1.00f, 0.42f, 0.10f);
                case ElementType.Water:    return new Color(0.24f, 0.62f, 1.00f);
                case ElementType.Electric: return new Color(1.00f, 0.90f, 0.25f);
                case ElementType.Grass:    return new Color(0.45f, 0.95f, 0.35f);
                case ElementType.Ice:      return new Color(0.62f, 0.94f, 1.00f);
                case ElementType.Fighting: return new Color(1.00f, 0.45f, 0.32f);
                case ElementType.Poison:   return new Color(0.72f, 0.32f, 0.88f);
                case ElementType.Ground:   return new Color(0.85f, 0.66f, 0.38f);
                case ElementType.Flying:   return new Color(0.78f, 0.88f, 1.00f);
                case ElementType.Psychic:  return new Color(1.00f, 0.36f, 0.70f);
                case ElementType.Bug:      return new Color(0.72f, 0.86f, 0.28f);
                case ElementType.Rock:     return new Color(0.78f, 0.66f, 0.42f);
                case ElementType.Ghost:    return new Color(0.52f, 0.40f, 0.82f);
                case ElementType.Dragon:   return new Color(0.42f, 0.48f, 1.00f);
                case ElementType.Dark:     return new Color(0.36f, 0.28f, 0.42f);
                case ElementType.Steel:    return new Color(0.78f, 0.84f, 0.92f);
                case ElementType.Fairy:    return new Color(1.00f, 0.68f, 0.90f);
                default:                   return new Color(1.00f, 0.96f, 0.86f);
            }
        }

        /// <summary>The hot core colour, always brighter and less saturated.</summary>
        public static Color CoreColor(ElementType type)
        {
            Color c = PrimaryColor(type);
            return Color.Lerp(c, Color.white, 0.65f) * 1.6f;
        }

        /// <summary>The dark counterpart, for smoke and debris.</summary>
        public static Color SmokeColor(ElementType type)
        {
            Color c = PrimaryColor(type);
            return Color.Lerp(c, new Color(0.16f, 0.15f, 0.18f), 0.72f);
        }

        // ---------------------------------------------------------------------
        // Keys
        // ---------------------------------------------------------------------
        public static string KeyFor(ElementType type, VfxPhase phase) =>
            "move." + type.ToString().ToLowerInvariant() + "." + phase.ToString().ToLowerInvariant();

        // ---------------------------------------------------------------------
        // Recipe construction
        // ---------------------------------------------------------------------
        public static void AddAll(Dictionary<string, VfxRecipe> into)
        {
            foreach (ElementType type in System.Enum.GetValues(typeof(ElementType)))
            {
                if (type == ElementType.None) continue;
                into[KeyFor(type, VfxPhase.Cast)] = Cast(type);
                into[KeyFor(type, VfxPhase.Travel)] = Travel(type);
                into[KeyFor(type, VfxPhase.Impact)] = Impact(type);
            }
        }

        /// <summary>
        /// The gather. Energy converges on the muzzle, so the audience knows a move
        /// is coming and knows what type it is before anything leaves the creature.
        /// Inward motion is the whole trick: particles spawn on a shell and fall in.
        /// </summary>
        public static VfxRecipe Cast(ElementType type)
        {
            Color primary = PrimaryColor(type);
            Color core = CoreColor(type);

            var recipe = new VfxRecipe
            {
                Id = KeyFor(type, VfxPhase.Cast),
                LightColor = primary,
                LightRange = 3.5f,
                LightIntensity = 2.2f,
                LightDuration = 0.45f,
                PoolPrewarm = 2,
            };

            // Converging motes: emitted on the surface of a sphere with negative
            // speed, so they travel inwards towards the muzzle.
            var gather = new VfxEmitter
            {
                Name = "Gather",
                Texture = TextureFor(type),
                Blend = BlendFor(type),
                StartColor = primary,
                StartColorSecondary = core,
                EmissionBoost = 2.2f,
                AlphaCurve = VfxCurve.Grow,
                Duration = 0.35f,
                LifetimeMin = 0.28f,
                LifetimeMax = 0.42f,
                Burst = 24,
                RateOverTime = 30f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.85f,
                ShapeThickness = 0f,
                SpeedMin = -3.2f,
                SpeedMax = -1.8f,
                Drag = 0.5f,
                SizeMin = 0.07f,
                SizeMax = 0.16f,
                SizeCurve = VfxCurve.Shrink,
                RotationSpeed = 90f,
                SimulationSpace = ParticleSystemSimulationSpace.Local,
            };

            // The core that grows as the energy arrives.
            var charge = new VfxEmitter
            {
                Name = "Charge",
                Texture = ProceduralVfxTextures.Kind.Glow,
                Blend = VfxBlend.Additive,
                StartColor = core,
                StartColorSecondary = primary,
                EmissionBoost = 2.6f,
                AlphaCurve = VfxCurve.Grow,
                Duration = 0.1f,
                LifetimeMin = 0.45f,
                LifetimeMax = 0.45f,
                Burst = 1,
                Shape = VfxShape.Point,
                SpeedMin = 0f,
                SpeedMax = 0f,
                SizeMin = 0.55f,
                SizeMax = 0.55f,
                SizeCurve = VfxCurve.Grow,
                StartRotationRandom = 0f,
            };

            // A radial telegraph on the ground so the cast reads even when the
            // creature's body is between the camera and the muzzle.
            var telegraph = new VfxEmitter
            {
                Name = "Telegraph",
                Texture = ProceduralVfxTextures.Kind.Burst,
                Blend = VfxBlend.Additive,
                StartColor = primary,
                StartColorSecondary = primary,
                EmissionBoost = 1.6f,
                AlphaCurve = VfxCurve.Flash,
                Delay = 0.08f,
                Duration = 0.1f,
                LifetimeMin = 0.4f,
                LifetimeMax = 0.4f,
                Burst = 1,
                Shape = VfxShape.Point,
                SpeedMin = 0f,
                SpeedMax = 0f,
                SizeMin = 0.4f,
                SizeMax = 0.4f,
                SizeCurve = VfxCurve.Grow,
                StartRotationRandom = 180f,
                RotationSpeed = 40f,
            };

            recipe.Emitters = new[] { gather, charge, telegraph };
            recipe.TotalDuration = 0.75f;
            return recipe;
        }

        /// <summary>
        /// The projectile. A bright head with a trail behind it, plus type-specific
        /// character: Fire sheds embers, Water sheds droplets, Electric forks.
        /// The presenter moves this object along the arc; the emitters only have to
        /// look right while being dragged.
        /// </summary>
        public static VfxRecipe Travel(ElementType type)
        {
            Color primary = PrimaryColor(type);
            Color core = CoreColor(type);

            var recipe = new VfxRecipe
            {
                Id = KeyFor(type, VfxPhase.Travel),
                LightColor = primary,
                LightRange = 4.5f,
                LightIntensity = 2.6f,
                LightDuration = 4f,   // outlives the flight; the pool ends it
                PoolPrewarm = 3,
            };

            var head = new VfxEmitter
            {
                Name = "Head",
                Texture = ProceduralVfxTextures.Kind.Glow,
                Blend = VfxBlend.Additive,
                StartColor = core,
                StartColorSecondary = primary,
                EmissionBoost = 3.0f,
                AlphaCurve = VfxCurve.Constant,
                Looping = true,
                Duration = 1f,
                LifetimeMin = 0.12f,
                LifetimeMax = 0.12f,
                Burst = 0,
                RateOverTime = 45f,
                Shape = VfxShape.Point,
                SpeedMin = 0f,
                SpeedMax = 0f,
                SizeMin = 0.34f,
                SizeMax = 0.44f,
                SizeCurve = VfxCurve.Shrink,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            // Distance-based emission means the trail density follows the projectile's
            // actual speed instead of clumping when it slows down.
            var trail = new VfxEmitter
            {
                Name = "Trail",
                Texture = TextureFor(type),
                Blend = BlendFor(type),
                StartColor = primary,
                StartColorSecondary = core,
                EmissionBoost = 2.0f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 1f,
                LifetimeMin = 0.22f,
                LifetimeMax = 0.45f,
                Burst = 0,
                RateOverDistance = 26f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.09f,
                SpeedMin = 0.15f,
                SpeedMax = 0.6f,
                Drag = 1.5f,
                Gravity = GravityFor(type),
                SizeMin = 0.14f,
                SizeMax = 0.28f,
                SizeCurve = VfxCurve.Shrink,
                RotationSpeed = 120f,
                NoiseStrength = 0.15f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var shed = new VfxEmitter
            {
                Name = "Shed",
                Texture = SecondaryTextureFor(type),
                Blend = VfxBlend.Additive,
                StartColor = primary,
                StartColorSecondary = core,
                EmissionBoost = 2.2f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 1f,
                LifetimeMin = 0.3f,
                LifetimeMax = 0.7f,
                Burst = 0,
                RateOverDistance = 8f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.12f,
                SpeedMin = 0.4f,
                SpeedMax = 1.4f,
                Gravity = 0.9f,
                Drag = 0.8f,
                SizeMin = 0.05f,
                SizeMax = 0.11f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 2.0f,
                RenderMode = ParticleSystemRenderMode.Stretch,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            recipe.Emitters = new[] { head, trail, shed };
            // Looping emitters never end on their own; the presenter stops the object
            // when the projectile arrives, and this is only the safety net.
            recipe.TotalDuration = 4f;
            return recipe;
        }

        /// <summary>
        /// The landing. Flash, radial burst, ground ring and debris, tuned per type.
        /// The ring is what gives the hit a sense of scale; without it an impact is
        /// just a brighter patch.
        /// </summary>
        public static VfxRecipe Impact(ElementType type)
        {
            Color primary = PrimaryColor(type);
            Color core = CoreColor(type);
            Color smoke = SmokeColor(type);

            var recipe = new VfxRecipe
            {
                Id = KeyFor(type, VfxPhase.Impact),
                LightColor = primary,
                LightRange = 6f,
                LightIntensity = 4.5f,
                LightDuration = 0.3f,
                PoolPrewarm = 4,
            };

            var flash = VfxEmitter.Flash(core, 1.5f, 0.22f);

            var ring = VfxEmitter.Ring(primary, 4.2f, 0.42f);
            ring.EmissionBoost = 2.4f;

            var burst = new VfxEmitter
            {
                Name = "Burst",
                Texture = TextureFor(type),
                Blend = BlendFor(type),
                StartColor = primary,
                StartColorSecondary = core,
                EmissionBoost = 2.4f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 0.12f,
                LifetimeMin = 0.32f,
                LifetimeMax = 0.62f,
                Burst = 34,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.18f,
                SpeedMin = 2.5f,
                SpeedMax = 6.5f,
                Gravity = GravityFor(type),
                Drag = 1.6f,
                SizeMin = 0.14f,
                SizeMax = 0.34f,
                SizeCurve = VfxCurve.Shrink,
                RotationSpeed = 150f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var sparks = VfxEmitter.Sparks(core, 18, 7.5f, 0.5f);

            var debris = VfxEmitter.Smoke(smoke, 8, 1.1f, 0.9f);
            debris.Delay = 0.04f;

            recipe.Emitters = new[] { flash, ring, burst, sparks, debris };
            recipe.TotalDuration = 1.3f;
            return recipe;
        }

        // ---------------------------------------------------------------------
        // Per-type character
        // ---------------------------------------------------------------------
        private static ProceduralVfxTextures.Kind TextureFor(ElementType type)
        {
            switch (type)
            {
                case ElementType.Fire:     return ProceduralVfxTextures.Kind.Flame;
                case ElementType.Water:    return ProceduralVfxTextures.Kind.Droplet;
                case ElementType.Electric: return ProceduralVfxTextures.Kind.Bolt;
                case ElementType.Grass:    return ProceduralVfxTextures.Kind.Leaf;
                case ElementType.Ice:      return ProceduralVfxTextures.Kind.Shard;
                case ElementType.Poison:   return ProceduralVfxTextures.Kind.Puff;
                case ElementType.Ground:   return ProceduralVfxTextures.Kind.Puff;
                case ElementType.Rock:     return ProceduralVfxTextures.Kind.Shard;
                case ElementType.Steel:    return ProceduralVfxTextures.Kind.Shard;
                case ElementType.Flying:   return ProceduralVfxTextures.Kind.Wing;
                case ElementType.Bug:      return ProceduralVfxTextures.Kind.Wing;
                case ElementType.Ghost:    return ProceduralVfxTextures.Kind.Smoke;
                case ElementType.Dark:     return ProceduralVfxTextures.Kind.Smoke;
                case ElementType.Psychic:  return ProceduralVfxTextures.Kind.Sparkle;
                case ElementType.Fairy:    return ProceduralVfxTextures.Kind.Petal;
                case ElementType.Dragon:   return ProceduralVfxTextures.Kind.Flare;
                case ElementType.Fighting: return ProceduralVfxTextures.Kind.Burst;
                default:                   return ProceduralVfxTextures.Kind.SoftDot;
            }
        }

        private static ProceduralVfxTextures.Kind SecondaryTextureFor(ElementType type)
        {
            switch (type)
            {
                case ElementType.Fire:     return ProceduralVfxTextures.Kind.Spark;
                case ElementType.Water:    return ProceduralVfxTextures.Kind.Droplet;
                case ElementType.Electric: return ProceduralVfxTextures.Kind.Spark;
                case ElementType.Grass:    return ProceduralVfxTextures.Kind.Petal;
                case ElementType.Psychic:  return ProceduralVfxTextures.Kind.Sparkle;
                case ElementType.Fairy:    return ProceduralVfxTextures.Kind.Sparkle;
                case ElementType.Ghost:    return ProceduralVfxTextures.Kind.SoftDot;
                default:                   return ProceduralVfxTextures.Kind.Spark;
            }
        }

        /// <summary>
        /// Gravity is character, not physics. Rock and Ground fall hard, Ghost and
        /// Psychic drift upwards, Fire rises because heat does.
        /// </summary>
        private static float GravityFor(ElementType type)
        {
            switch (type)
            {
                case ElementType.Rock:
                case ElementType.Ground:
                case ElementType.Steel: return 2.2f;
                case ElementType.Water: return 1.4f;
                case ElementType.Ice:   return 1.0f;
                case ElementType.Fire:  return -0.55f;
                case ElementType.Ghost:
                case ElementType.Psychic:
                case ElementType.Flying:
                case ElementType.Fairy: return -0.35f;
                default: return 0.1f;
            }
        }

        /// <summary>
        /// Most types add light. The ones with mass occlude instead, which is what
        /// makes a Ground attack feel heavy next to an Electric one.
        /// </summary>
        private static VfxBlend BlendFor(ElementType type)
        {
            switch (type)
            {
                case ElementType.Ground:
                case ElementType.Rock:
                case ElementType.Dark:
                    return VfxBlend.AlphaBlend;
                case ElementType.Fire:
                case ElementType.Poison:
                case ElementType.Ghost:
                    return VfxBlend.Premultiplied;
                default:
                    return VfxBlend.Additive;
            }
        }
    }
}
