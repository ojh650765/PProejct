using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Everything a battle needs that is not a move: effectiveness variants, crits,
    /// status application and lingering auras, stat changes, healing, fainting,
    /// capture and level up.
    ///
    /// These read as a family on purpose. A super-effective hit and a critical hit
    /// are both "a bigger version of what just happened" and share the impact's
    /// vocabulary, while status effects all use the same slow orbiting motion so
    /// the player learns to read "this creature has a condition" at a glance,
    /// separately from learning which condition it is.
    /// </summary>
    public static class BattleStateVfxLibrary
    {
        public const string KeyCritical = "hit.critical";
        public const string KeySuperEffective = "hit.super";
        public const string KeyNotVeryEffective = "hit.resisted";
        public const string KeyImmune = "hit.immune";
        public const string KeyHeal = "state.heal";
        public const string KeyFaint = "state.faint";
        public const string KeyLevelUp = "state.levelup";
        public const string KeyCaptureBeam = "capture.beam";
        public const string KeyCaptureFlash = "capture.flash";
        public const string KeyCaptureSuccess = "capture.success";
        public const string KeyStatUp = "state.statup";
        public const string KeyStatDown = "state.statdown";
        public const string KeyShield = "state.shield";

        public static string StatusKey(StatusCondition status) =>
            "status." + status.ToString().ToLowerInvariant();

        public static void AddAll(Dictionary<string, VfxRecipe> into)
        {
            into[KeyCritical] = Critical();
            into[KeySuperEffective] = SuperEffective();
            into[KeyNotVeryEffective] = NotVeryEffective();
            into[KeyImmune] = Immune();
            into[KeyHeal] = Heal();
            into[KeyFaint] = Faint();
            into[KeyLevelUp] = LevelUp();
            into[KeyCaptureBeam] = CaptureBeam();
            into[KeyCaptureFlash] = CaptureFlash();
            into[KeyCaptureSuccess] = CaptureSuccess();
            into[KeyStatUp] = StatChange(true);
            into[KeyStatDown] = StatChange(false);
            into[KeyShield] = Shield();

            into[StatusKey(StatusCondition.Burn)] = Burn();
            into[StatusKey(StatusCondition.Freeze)] = Freeze();
            into[StatusKey(StatusCondition.Paralysis)] = Paralysis();
            into[StatusKey(StatusCondition.Poison)] = Poison(false);
            into[StatusKey(StatusCondition.BadPoison)] = Poison(true);
            into[StatusKey(StatusCondition.Sleep)] = Sleep();
        }

        // ---------------------------------------------------------------------
        // Hit variants
        // ---------------------------------------------------------------------

        /// <summary>
        /// A white cross-flash plus a hard shockwave. Deliberately achromatic so it
        /// layers over any move's own colour without muddying it.
        /// </summary>
        private static VfxRecipe Critical()
        {
            var flash = VfxEmitter.Flash(new Color(3.2f, 3.0f, 2.6f), 2.4f, 0.16f);
            flash.Texture = ProceduralVfxTextures.Kind.Flare;
            flash.StartRotationRandom = 25f;

            var ring = VfxEmitter.Ring(new Color(2.6f, 2.4f, 2.0f), 5.5f, 0.3f);
            ring.RenderMode = ParticleSystemRenderMode.Billboard;

            var shards = VfxEmitter.Sparks(new Color(2.4f, 2.2f, 1.8f), 26, 11f, 0.45f);

            return new VfxRecipe
            {
                Id = KeyCritical,
                Emitters = new[] { flash, ring, shards },
                LightColor = Color.white,
                LightRange = 8f,
                LightIntensity = 7f,
                LightDuration = 0.18f,
                TotalDuration = 0.8f,
                PoolPrewarm = 3,
            };
        }

        /// <summary>Bigger, hotter, with a second delayed ring so it lands twice.</summary>
        private static VfxRecipe SuperEffective()
        {
            var flash = VfxEmitter.Flash(new Color(2.8f, 2.4f, 1.6f), 2.0f, 0.2f);

            var ring1 = VfxEmitter.Ring(new Color(2.2f, 1.9f, 1.2f), 5.0f, 0.36f);
            var ring2 = VfxEmitter.Ring(new Color(1.8f, 1.5f, 1.0f), 7.0f, 0.5f);
            ring2.Delay = 0.10f;

            var burst = VfxEmitter.Sparks(new Color(2.6f, 2.0f, 1.2f), 30, 9f, 0.55f);

            return new VfxRecipe
            {
                Id = KeySuperEffective,
                Emitters = new[] { flash, ring1, ring2, burst },
                LightColor = new Color(1f, 0.9f, 0.7f),
                LightRange = 8f,
                LightIntensity = 6f,
                LightDuration = 0.24f,
                TotalDuration = 1.0f,
                PoolPrewarm = 3,
            };
        }

        /// <summary>
        /// Small, dull and short. Reading "that barely worked" needs the effect to be
        /// visibly weaker than a neutral hit, not merely smaller.
        /// </summary>
        private static VfxRecipe NotVeryEffective()
        {
            var puff = new VfxEmitter
            {
                Name = "Puff",
                Texture = ProceduralVfxTextures.Kind.Puff,
                Surface = VfxSurface.Gas,
                Blend = VfxBlend.AlphaBlend,
                StartColor = new Color(0.62f, 0.64f, 0.70f),
                StartColorSecondary = new Color(0.42f, 0.44f, 0.50f),
                EmissionBoost = 1f,
                AlphaCurve = VfxCurve.Swell,
                Duration = 0.1f,
                LifetimeMin = 0.3f,
                LifetimeMax = 0.5f,
                Burst = 6,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.2f,
                SpeedMin = 0.6f,
                SpeedMax = 1.4f,
                Drag = 2.5f,
                SizeMin = 0.4f,
                SizeMax = 0.7f,
                SizeCurve = VfxCurve.Grow,
                RotationSpeed = 40f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            return new VfxRecipe
            {
                Id = KeyNotVeryEffective,
                Emitters = new[] { puff },
                TotalDuration = 0.7f,
                PoolPrewarm = 2,
            };
        }

        /// <summary>A shield flare and nothing else: the attack simply stopped.</summary>
        private static VfxRecipe Immune()
        {
            var ring = VfxEmitter.Ring(new Color(1.4f, 1.6f, 2.2f), 2.4f, 0.35f);
            ring.RenderMode = ParticleSystemRenderMode.Billboard;

            var glint = VfxEmitter.Flash(new Color(1.6f, 1.8f, 2.4f), 1.2f, 0.25f);
            glint.Texture = ProceduralVfxTextures.Kind.Sparkle;

            return new VfxRecipe
            {
                Id = KeyImmune,
                Emitters = new[] { ring, glint },
                TotalDuration = 0.6f,
                PoolPrewarm = 1,
            };
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        /// <summary>Sparkles rising through the creature, plus a soft green wash.</summary>
        private static VfxRecipe Heal()
        {
            var sparkles = new VfxEmitter
            {
                Name = "Sparkles",
                Texture = ProceduralVfxTextures.Kind.Sparkle,
                Blend = VfxBlend.Additive,
                StartColor = new Color(0.5f, 1.0f, 0.6f),
                StartColorSecondary = new Color(1.0f, 1.0f, 0.85f),
                EmissionBoost = 2.4f,
                AlphaCurve = VfxCurve.Swell,
                Duration = 0.8f,
                LifetimeMin = 0.7f,
                LifetimeMax = 1.2f,
                Burst = 14,
                RateOverTime = 22f,
                Shape = VfxShape.Disc,
                ShapeRadius = 0.55f,
                SpeedMin = 0.9f,
                SpeedMax = 1.8f,
                Gravity = -0.25f,
                Drag = 0.4f,
                SizeMin = 0.10f,
                SizeMax = 0.22f,
                SizeCurve = VfxCurve.Swell,
                RotationSpeed = 80f,
                NoiseStrength = 0.18f,
            };

            var ring = VfxEmitter.Ring(new Color(0.6f, 1.6f, 0.8f), 2.2f, 0.7f,
                                       ProceduralVfxTextures.Kind.Ring);
            ring.AlphaCurve = VfxCurve.Swell;

            return new VfxRecipe
            {
                Id = KeyHeal,
                Emitters = new[] { ring, sparkles },
                LightColor = new Color(0.5f, 1f, 0.6f),
                LightRange = 4f,
                LightIntensity = 2.2f,
                LightDuration = 0.8f,
                TotalDuration = 1.8f,
                PoolPrewarm = 2,
            };
        }

        /// <summary>
        /// The faint. Motes drift up and away while the creature's own dissolve runs
        /// underneath, driven by the presenter on the creature material.
        /// </summary>
        private static VfxRecipe Faint()
        {
            var motes = new VfxEmitter
            {
                Name = "Motes",
                Texture = ProceduralVfxTextures.Kind.SoftDot,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.85f, 0.55f),
                StartColorSecondary = new Color(1.0f, 0.55f, 0.30f),
                EmissionBoost = 2.0f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 1.0f,
                LifetimeMin = 0.8f,
                LifetimeMax = 1.6f,
                Burst = 20,
                RateOverTime = 28f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.5f,
                SpeedMin = 0.3f,
                SpeedMax = 1.0f,
                Gravity = -0.4f,
                Drag = 0.5f,
                SizeMin = 0.07f,
                SizeMax = 0.16f,
                SizeCurve = VfxCurve.Shrink,
                NoiseStrength = 0.3f,
                NoiseFrequency = 0.25f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var collapse = VfxEmitter.Smoke(new Color(0.35f, 0.32f, 0.38f), 10, 1.0f, 1.2f);
            collapse.Delay = 0.25f;

            return new VfxRecipe
            {
                Id = KeyFaint,
                Emitters = new[] { motes, collapse },
                TotalDuration = 2.6f,
                PoolPrewarm = 2,
            };
        }

        /// <summary>A rising column of light with a burst of stars at the top.</summary>
        private static VfxRecipe LevelUp()
        {
            var column = new VfxEmitter
            {
                Name = "Column",
                Texture = ProceduralVfxTextures.Kind.Glow,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.92f, 0.5f),
                StartColorSecondary = new Color(1.0f, 1.0f, 0.85f),
                EmissionBoost = 3.0f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 0.6f,
                LifetimeMin = 0.5f,
                LifetimeMax = 0.9f,
                Burst = 10,
                RateOverTime = 40f,
                Shape = VfxShape.Disc,
                ShapeRadius = 0.45f,
                SpeedMin = 3.5f,
                SpeedMax = 6.0f,
                Gravity = -0.6f,
                SizeMin = 0.25f,
                SizeMax = 0.5f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 2.6f,
                RenderMode = ParticleSystemRenderMode.Stretch,
            };

            var stars = new VfxEmitter
            {
                Name = "Stars",
                Texture = ProceduralVfxTextures.Kind.Sparkle,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.95f, 0.65f),
                StartColorSecondary = Color.white,
                EmissionBoost = 2.8f,
                AlphaCurve = VfxCurve.Flash,
                Delay = 0.35f,
                Duration = 0.2f,
                LifetimeMin = 0.6f,
                LifetimeMax = 1.1f,
                Burst = 22,
                Offset = new Vector3(0f, 1.6f, 0f),
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.25f,
                SpeedMin = 1.5f,
                SpeedMax = 3.5f,
                Gravity = 0.8f,
                Drag = 1.2f,
                SizeMin = 0.12f,
                SizeMax = 0.3f,
                SizeCurve = VfxCurve.Shrink,
                RotationSpeed = 120f,
            };

            var ring = VfxEmitter.Ring(new Color(2.0f, 1.7f, 0.9f), 3.5f, 0.55f);

            return new VfxRecipe
            {
                Id = KeyLevelUp,
                Emitters = new[] { ring, column, stars },
                LightColor = new Color(1f, 0.9f, 0.55f),
                LightRange = 6f,
                LightIntensity = 4f,
                LightDuration = 0.7f,
                TotalDuration = 2.2f,
                PoolPrewarm = 1,
            };
        }

        // ---------------------------------------------------------------------
        // Capture
        // ---------------------------------------------------------------------

        /// <summary>The beam that draws the creature into the ball.</summary>
        private static VfxRecipe CaptureBeam()
        {
            var beam = new VfxEmitter
            {
                Name = "Beam",
                Texture = ProceduralVfxTextures.Kind.Glow,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.35f, 0.30f),
                StartColorSecondary = new Color(1.0f, 0.85f, 0.7f),
                EmissionBoost = 3.2f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 1f,
                LifetimeMin = 0.18f,
                LifetimeMax = 0.3f,
                RateOverDistance = 40f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.06f,
                SpeedMin = 0f,
                SpeedMax = 0.2f,
                SizeMin = 0.28f,
                SizeMax = 0.42f,
                SizeCurve = VfxCurve.Shrink,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var swirl = new VfxEmitter
            {
                Name = "Swirl",
                Texture = ProceduralVfxTextures.Kind.Spark,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.5f, 0.35f),
                StartColorSecondary = Color.white,
                EmissionBoost = 2.6f,
                AlphaCurve = VfxCurve.Flash,
                Looping = true,
                Duration = 1f,
                LifetimeMin = 0.3f,
                LifetimeMax = 0.6f,
                RateOverTime = 60f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.7f,
                ShapeThickness = 0f,
                SpeedMin = -2.6f,
                SpeedMax = -1.4f,
                SizeMin = 0.06f,
                SizeMax = 0.14f,
                SizeCurve = VfxCurve.Shrink,
                VelocityStretch = 2.2f,
                RenderMode = ParticleSystemRenderMode.Stretch,
            };

            return new VfxRecipe
            {
                Id = KeyCaptureBeam,
                Emitters = new[] { beam, swirl },
                LightColor = new Color(1f, 0.45f, 0.35f),
                LightRange = 5f,
                LightIntensity = 3f,
                LightDuration = 3f,
                TotalDuration = 3.5f,
                PoolPrewarm = 1,
            };
        }

        /// <summary>The single white flash on each shake of the ball.</summary>
        private static VfxRecipe CaptureFlash()
        {
            var flash = VfxEmitter.Flash(new Color(3.0f, 2.6f, 2.0f), 0.9f, 0.18f);
            flash.Texture = ProceduralVfxTextures.Kind.Sparkle;

            return new VfxRecipe
            {
                Id = KeyCaptureFlash,
                Emitters = new[] { flash },
                LightColor = Color.white,
                LightRange = 3f,
                LightIntensity = 3f,
                LightDuration = 0.15f,
                TotalDuration = 0.4f,
                PoolPrewarm = 4,
            };
        }

        /// <summary>The confirmation burst when the ball clicks shut.</summary>
        private static VfxRecipe CaptureSuccess()
        {
            var stars = new VfxEmitter
            {
                Name = "Stars",
                Texture = ProceduralVfxTextures.Kind.Sparkle,
                Blend = VfxBlend.Additive,
                StartColor = new Color(1.0f, 0.92f, 0.5f),
                StartColorSecondary = Color.white,
                EmissionBoost = 3.0f,
                AlphaCurve = VfxCurve.Flash,
                Duration = 0.15f,
                LifetimeMin = 0.6f,
                LifetimeMax = 1.1f,
                Burst = 26,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.15f,
                SpeedMin = 2.0f,
                SpeedMax = 5.0f,
                Gravity = 1.2f,
                Drag = 1.0f,
                SizeMin = 0.14f,
                SizeMax = 0.3f,
                SizeCurve = VfxCurve.Shrink,
                RotationSpeed = 140f,
                SimulationSpace = ParticleSystemSimulationSpace.World,
            };

            var ring = VfxEmitter.Ring(new Color(2.2f, 1.9f, 1.0f), 3.0f, 0.45f);

            return new VfxRecipe
            {
                Id = KeyCaptureSuccess,
                Emitters = new[] { ring, stars },
                LightColor = new Color(1f, 0.9f, 0.6f),
                LightRange = 5f,
                LightIntensity = 4f,
                LightDuration = 0.4f,
                TotalDuration = 1.6f,
                PoolPrewarm = 1,
            };
        }

        // ---------------------------------------------------------------------
        // Stat stages
        // ---------------------------------------------------------------------

        /// <summary>
        /// Arrows of light rising or falling around the creature. Direction is the
        /// only thing that changes, which is exactly the read we want: same event,
        /// opposite sign.
        /// </summary>
        private static VfxRecipe StatChange(bool up)
        {
            Color colour = up ? new Color(1.0f, 0.75f, 0.25f) : new Color(0.45f, 0.55f, 0.95f);

            var arrows = new VfxEmitter
            {
                Name = up ? "RisingArrows" : "FallingArrows",
                Texture = ProceduralVfxTextures.Kind.Spark,
                Blend = VfxBlend.Additive,
                StartColor = colour,
                StartColorSecondary = Color.Lerp(colour, Color.white, 0.5f),
                EmissionBoost = 2.4f,
                AlphaCurve = VfxCurve.Swell,
                Duration = 0.7f,
                LifetimeMin = 0.6f,
                LifetimeMax = 1.0f,
                Burst = 10,
                RateOverTime = 16f,
                Shape = VfxShape.Ring,
                ShapeRadius = 0.7f,
                Offset = new Vector3(0f, up ? 0.1f : 1.8f, 0f),
                SpeedMin = 0.1f,
                SpeedMax = 0.3f,
                ConstantForce = new Vector3(0f, up ? 3.2f : -3.2f, 0f),
                Drag = 0.7f,
                SizeMin = 0.14f,
                SizeMax = 0.26f,
                SizeCurve = VfxCurve.Swell,
                VelocityStretch = 2.2f,
                RenderMode = ParticleSystemRenderMode.Stretch,
            };

            var aura = VfxEmitter.Ring(colour * 1.4f, 2.2f, 0.6f, ProceduralVfxTextures.Kind.Ring);
            aura.AlphaCurve = VfxCurve.Swell;
            if (!up) aura.Offset = new Vector3(0f, 1.9f, 0f);

            return new VfxRecipe
            {
                Id = up ? KeyStatUp : KeyStatDown,
                Emitters = new[] { aura, arrows },
                LightColor = colour,
                LightRange = 3.5f,
                LightIntensity = 1.6f,
                LightDuration = 0.7f,
                TotalDuration = 1.8f,
                PoolPrewarm = 2,
            };
        }

        /// <summary>Protect and ability blocks: a brief hexagonal shell flash.</summary>
        private static VfxRecipe Shield()
        {
            var flare = VfxEmitter.Flash(new Color(1.2f, 1.8f, 2.6f), 2.6f, 0.35f);
            flare.Texture = ProceduralVfxTextures.Kind.Ring;
            flare.SizeCurve = VfxCurve.Pop;

            var sparks = VfxEmitter.Sparks(new Color(1.4f, 2.0f, 2.8f), 14, 5f, 0.4f);

            return new VfxRecipe
            {
                Id = KeyShield,
                Emitters = new[] { flare, sparks },
                LightColor = new Color(0.5f, 0.8f, 1f),
                LightRange = 5f,
                LightIntensity = 3f,
                LightDuration = 0.3f,
                TotalDuration = 0.9f,
                PoolPrewarm = 2,
            };
        }

        // ---------------------------------------------------------------------
        // Status conditions. All looping: the presenter parents them to the
        // creature and stops them when the condition clears.
        // ---------------------------------------------------------------------

        private static VfxEmitter Orbit(string name, Color colour,
                                        ProceduralVfxTextures.Kind texture,
                                        float radius, float rate, float size)
        {
            // Emitted on a ring with a small tangential push and heavy drag, so they
            // hang around the creature rather than flying off. Reads as an orbit
            // without needing a custom simulation.
            return new VfxEmitter
            {
                Name = name,
                Texture = texture,
                Blend = VfxBlend.Additive,
                StartColor = colour,
                StartColorSecondary = Color.Lerp(colour, Color.white, 0.4f),
                EmissionBoost = 2.0f,
                AlphaCurve = VfxCurve.Swell,
                Looping = true,
                Duration = 2f,
                LifetimeMin = 0.9f,
                LifetimeMax = 1.6f,
                Burst = 0,
                RateOverTime = rate,
                Shape = VfxShape.Ring,
                ShapeRadius = radius,
                Offset = new Vector3(0f, 0.8f, 0f),
                SpeedMin = 0.15f,
                SpeedMax = 0.45f,
                Drag = 1.4f,
                SizeMin = size * 0.7f,
                SizeMax = size,
                SizeCurve = VfxCurve.Swell,
                RotationSpeed = 70f,
                NoiseStrength = 0.22f,
                NoiseFrequency = 0.3f,
            };
        }

        private static VfxRecipe Looping(string id, params VfxEmitter[] emitters) => new VfxRecipe
        {
            Id = id,
            Emitters = emitters,
            // Zero total duration marks it as persistent: the presenter releases it
            // by hand when the status clears rather than on a timer.
            TotalDuration = 0f,
            PoolPrewarm = 1,
        };

        private static VfxRecipe Burn()
        {
            var flames = Orbit("Flames", new Color(1.0f, 0.42f, 0.10f),
                               ProceduralVfxTextures.Kind.Flame, 0.55f, 18f, 0.26f);
            flames.Blend = VfxBlend.Premultiplied;
            flames.Gravity = -0.5f;

            var embers = Orbit("Embers", new Color(1.0f, 0.65f, 0.2f),
                               ProceduralVfxTextures.Kind.SoftDot, 0.45f, 10f, 0.08f);
            embers.Gravity = -0.8f;

            var recipe = Looping(StatusKey(StatusCondition.Burn), flames, embers);
            recipe.LightColor = new Color(1f, 0.45f, 0.15f);
            recipe.LightRange = 3f;
            recipe.LightIntensity = 1.4f;
            recipe.LightDuration = 999f;
            return recipe;
        }

        private static VfxRecipe Freeze()
        {
            var crystals = Orbit("Crystals", new Color(0.7f, 0.95f, 1.0f),
                                 ProceduralVfxTextures.Kind.Shard, 0.6f, 8f, 0.22f);
            crystals.RotationSpeed = 25f;
            crystals.NoiseStrength = 0.05f;

            var mist = Orbit("Mist", new Color(0.75f, 0.92f, 1.0f),
                             ProceduralVfxTextures.Kind.Puff, 0.5f, 6f, 0.5f);
            mist.Surface = VfxSurface.Gas;
            mist.Blend = VfxBlend.AlphaBlend;
            mist.Gravity = 0.25f;

            return Looping(StatusKey(StatusCondition.Freeze), mist, crystals);
        }

        private static VfxRecipe Paralysis()
        {
            var arcs = Orbit("Arcs", new Color(1.0f, 0.92f, 0.3f),
                             ProceduralVfxTextures.Kind.Bolt, 0.5f, 14f, 0.3f);
            arcs.LifetimeMin = 0.12f;
            arcs.LifetimeMax = 0.24f;
            arcs.AlphaCurve = VfxCurve.Pop;
            arcs.NoiseStrength = 0.5f;
            arcs.NoiseFrequency = 1.2f;

            var recipe = Looping(StatusKey(StatusCondition.Paralysis), arcs);
            recipe.LightColor = new Color(1f, 0.95f, 0.4f);
            recipe.LightRange = 3f;
            recipe.LightIntensity = 1.1f;
            recipe.LightDuration = 999f;
            return recipe;
        }

        private static VfxRecipe Poison(bool bad)
        {
            Color colour = bad ? new Color(0.55f, 0.15f, 0.62f) : new Color(0.72f, 0.32f, 0.88f);

            var bubbles = Orbit("Bubbles", colour, ProceduralVfxTextures.Kind.SoftDot,
                                0.5f, bad ? 20f : 12f, 0.14f);
            bubbles.Gravity = -0.6f;

            var haze = Orbit("Haze", colour, ProceduralVfxTextures.Kind.Puff,
                             0.55f, bad ? 10f : 6f, 0.55f);
            haze.Surface = VfxSurface.Gas;
            haze.Blend = VfxBlend.AlphaBlend;
            haze.Gravity = -0.25f;

            return Looping(StatusKey(bad ? StatusCondition.BadPoison : StatusCondition.Poison),
                           haze, bubbles);
        }

        private static VfxRecipe Sleep()
        {
            var zzz = new VfxEmitter
            {
                Name = "Drift",
                Texture = ProceduralVfxTextures.Kind.SoftDot,
                Blend = VfxBlend.Additive,
                StartColor = new Color(0.6f, 0.72f, 1.0f),
                StartColorSecondary = new Color(0.9f, 0.95f, 1.0f),
                EmissionBoost = 1.6f,
                AlphaCurve = VfxCurve.Swell,
                Looping = true,
                Duration = 2.5f,
                LifetimeMin = 1.6f,
                LifetimeMax = 2.6f,
                RateOverTime = 3.5f,
                Shape = VfxShape.Sphere,
                ShapeRadius = 0.2f,
                Offset = new Vector3(0.25f, 1.4f, 0f),
                SpeedMin = 0.2f,
                SpeedMax = 0.45f,
                Gravity = -0.15f,
                Drag = 0.6f,
                SizeMin = 0.12f,
                SizeMax = 0.3f,
                SizeCurve = VfxCurve.Grow,
                RotationSpeed = 20f,
                NoiseStrength = 0.12f,
                NoiseFrequency = 0.2f,
            };

            return Looping(StatusKey(StatusCondition.Sleep), zzz);
        }
    }
}
