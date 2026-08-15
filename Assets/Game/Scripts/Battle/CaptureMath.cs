using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>Outcome of one thrown ball, before it becomes a <see cref="CaptureAttemptEvent"/>.</summary>
    public readonly struct CaptureResult
    {
        /// <summary>0-3 for a failure, 4 for a catch. The presenter animates exactly this many shakes.</summary>
        public readonly int Shakes;
        public readonly bool Succeeded;
        /// <summary>Probability the throw had of succeeding, 0-1. Displayed by the Poké Lab readout.</summary>
        public readonly float Probability;

        public CaptureResult(int shakes, bool succeeded, float probability)
        {
            Shakes = shakes; Succeeded = succeeded; Probability = probability;
        }
    }

    /// <summary>
    /// The catch formula, kept apart from the engine because the Poké Lab scanner wants to
    /// display the probability before the player commits to a throw — that call must not
    /// draw from the battle RNG, so probability and roll are separate entry points.
    ///
    /// <see cref="SpeciesData"/> carries no catch rate field, so the rate comes from a
    /// small table for the slice roster with a base-stat-total estimate behind it.
    /// </summary>
    public static class CaptureMath
    {
        /// <summary>Number of shakes that means the ball held.</summary>
        public const int ShakesForCapture = 4;

        private const int MaxRoll = 65536;
        private const float ShakeConstant = 1048560f;
        private const float ShakeNumerator = 16711680f;

        /// <summary>Catch rates by national dex number for the species the slice can encounter.</summary>
        private static readonly Dictionary<int, int> RatesByNationalDex = new Dictionary<int, int>
        {
            { 1, 45 },    // Bulbasaur
            { 4, 45 },    // Charmander
            { 7, 45 },    // Squirtle
            { 16, 255 },  // Pidgey
            { 19, 255 },  // Rattata
            { 25, 190 },  // Pikachu
            { 41, 255 },  // Zubat
            { 43, 255 },  // Oddish
            { 60, 255 },  // Poliwag
            { 66, 180 },  // Machop
            { 74, 255 },  // Geodude
            { 92, 190 },  // Gastly
        };

        /// <summary>
        /// Same rates keyed by the dataset's KaggleID, which is what <see cref="SpeciesData.Id"/>
        /// holds. Used when the export did not fill in the national dex column.
        /// </summary>
        private static readonly Dictionary<int, int> RatesByDatasetId = new Dictionary<int, int>
        {
            { 1, 45 }, { 5, 45 }, { 10, 45 }, { 21, 255 }, { 25, 255 }, { 31, 190 },
            { 47, 255 }, { 49, 255 }, { 66, 255 }, { 73, 180 }, { 81, 255 }, { 100, 190 },
        };

        /// <summary>Catch rate 3-255 for a species. Falls back to a base-stat-total estimate.</summary>
        public static int CatchRateOf(SpeciesData species)
        {
            if (species == null) return 45;

            if (species.NationalDex > 0 && RatesByNationalDex.TryGetValue(species.NationalDex, out var byDex))
                return byDex;
            if (RatesByDatasetId.TryGetValue(species.Id, out var byId))
                return byId;

            if (species.Legendary) return 3;

            // Rough but monotone: frail early-route creatures land near 255, fully evolved
            // ones near 45, which is where the real table puts them.
            var estimate = 255 - (species.BaseStatTotal - 180) * 250 / 500;
            return Math.Clamp(estimate, 3, 255);
        }

        /// <summary>Sleep and freeze are worth the most; the chip statuses are worth half as much.</summary>
        public static float StatusBonus(StatusCondition status)
        {
            switch (status)
            {
                case StatusCondition.Sleep:
                case StatusCondition.Freeze:
                    return 2f;
                case StatusCondition.Paralysis:
                case StatusCondition.Poison:
                case StatusCondition.BadPoison:
                case StatusCondition.Burn:
                    return 1.5f;
                default:
                    return 1f;
            }
        }

        /// <summary>
        /// The modified catch rate <c>a</c>. A value of 255 or more is a guaranteed catch.
        /// </summary>
        public static float ModifiedCatchRate(CreatureInstance target, int catchRate, float ballMultiplier)
        {
            if (target == null || target.MaxHp <= 0) return 0f;

            // A master ball is modelled as multiplier 0, meaning "skip the maths".
            if (ballMultiplier <= 0f) return 255f;

            var max = target.MaxHp;
            var current = Math.Max(1, target.CurrentHp);

            var a = (3f * max - 2f * current) * catchRate * ballMultiplier / (3f * max);
            return a * StatusBonus(target.Status);
        }

        /// <summary>Probability the ball holds, 0-1. RNG-free, so the scanner may call it freely.</summary>
        public static float Probability(CreatureInstance target, int catchRate, float ballMultiplier)
        {
            var a = ModifiedCatchRate(target, catchRate, ballMultiplier);
            if (a <= 0f) return 0f;
            if (a >= 255f) return 1f;

            var b = ShakeThreshold(a);
            var perShake = b / MaxRoll;
            return Math.Clamp(perShake * perShake * perShake * perShake, 0f, 1f);
        }

        /// <summary>
        /// Rolls the four shake checks. Every throw consumes exactly four draws whether it
        /// succeeds early or not, so the RNG stream stays aligned between replays.
        /// </summary>
        public static CaptureResult Roll(CreatureInstance target, int catchRate, float ballMultiplier, BattleRandom rng)
        {
            var a = ModifiedCatchRate(target, catchRate, ballMultiplier);
            var probability = Probability(target, catchRate, ballMultiplier);

            if (a >= 255f)
            {
                // Still burn the draws, so a guaranteed ball does not shift later rolls
                // relative to a failed throw of the same turn.
                for (var i = 0; i < ShakesForCapture; i++) rng.Next(MaxRoll);
                return new CaptureResult(ShakesForCapture, true, 1f);
            }

            var b = (int)ShakeThreshold(a);
            var shakes = 0;
            var broke = false;

            for (var i = 0; i < ShakesForCapture; i++)
            {
                var roll = rng.Next(MaxRoll);
                if (broke) continue;
                if (roll < b) shakes++;
                else broke = true;
            }

            return new CaptureResult(shakes, !broke && shakes >= ShakesForCapture, probability);
        }

        /// <summary>The per-shake threshold <c>b</c>, the value each 0-65535 roll is compared against.</summary>
        public static float ShakeThreshold(float a)
        {
            if (a <= 0f) return 0f;
            if (a >= 255f) return MaxRoll;
            return ShakeConstant / (float)Math.Pow(ShakeNumerator / a, 0.25);
        }
    }
}
