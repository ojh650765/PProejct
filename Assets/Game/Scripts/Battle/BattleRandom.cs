using System;

namespace PokeLab.Battle
{
    /// <summary>
    /// Seeded xorshift128+ generator. The battle engine uses this and nothing else.
    ///
    /// Why not <see cref="System.Random"/>: its algorithm is not specified across .NET
    /// versions and Mono/CoreCLR have historically disagreed, so a replay recorded on one
    /// runtime would diverge on another. Why not UnityEngine.Random: it is a global,
    /// shared with every other system in the process, so any unrelated code that draws a
    /// number would silently desync a battle. Determinism is a project non-negotiable, so
    /// the sequence is defined here in full.
    ///
    /// This is a mutable class rather than a struct on purpose: it is threaded through the
    /// whole resolution path by reference, and a copied struct would replay draws.
    /// </summary>
    public sealed class BattleRandom
    {
        private ulong _s0;
        private ulong _s1;

        /// <summary>The seed this generator was constructed with, kept for replay logging.</summary>
        public int Seed { get; }

        /// <summary>Number of raw draws taken. Useful when diffing two supposedly equal runs.</summary>
        public long DrawCount { get; private set; }

        public BattleRandom(int seed)
        {
            Seed = seed;

            // SplitMix64 expansion so that adjacent seeds (0, 1, 2 …) produce well separated
            // streams instead of correlated ones.
            var z = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x9E3779B97F4A7C15UL);
            _s0 = SplitMix(ref z);
            _s1 = SplitMix(ref z);

            // xorshift is degenerate at an all-zero state; the SplitMix constant avoids it.
            if (_s0 == 0 && _s1 == 0) _s1 = 0x9E3779B97F4A7C15UL;
        }

        private static ulong SplitMix(ref ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            var r = z;
            r = (r ^ (r >> 30)) * 0xBF58476D1CE4E5B9UL;
            r = (r ^ (r >> 27)) * 0x94D049BB133111EBUL;
            return r ^ (r >> 31);
        }

        /// <summary>Raw 64-bit draw.</summary>
        public ulong NextUInt64()
        {
            DrawCount++;
            var x = _s0;
            var y = _s1;
            _s0 = y;
            x ^= x << 23;
            _s1 = x ^ y ^ (x >> 17) ^ (y >> 26);
            return unchecked(_s1 + y);
        }

        /// <summary>Uniform integer in [0, exclusiveMax). Returns 0 when the range is empty.</summary>
        public int Next(int exclusiveMax)
        {
            if (exclusiveMax <= 1) return 0;

            // Rejection sampling keeps the distribution exactly uniform; modulo alone would
            // bias low values, which is visible in accuracy tests over long runs.
            var bound = (ulong)exclusiveMax;
            var limit = ulong.MaxValue - (ulong.MaxValue % bound) - 1;
            ulong draw;
            do { draw = NextUInt64(); } while (draw > limit);
            return (int)(draw % bound);
        }

        /// <summary>Uniform integer in [inclusiveMin, inclusiveMax].</summary>
        public int Range(int inclusiveMin, int inclusiveMax)
        {
            if (inclusiveMax <= inclusiveMin) return inclusiveMin;
            return inclusiveMin + Next(inclusiveMax - inclusiveMin + 1);
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public float NextFloat() => (NextUInt64() >> 40) * (1f / 16777216f);

        /// <summary>True with the supplied probability expressed in percent (0-100).</summary>
        public bool Chance(int percent)
        {
            if (percent <= 0) return false;
            if (percent >= 100) return true;
            return Next(100) < percent;
        }

        /// <summary>True with probability 1/denominator. Used for crits and thaw rolls.</summary>
        public bool OneIn(int denominator) => denominator <= 1 || Next(denominator) == 0;

        /// <summary>Unbiased coin flip, used only to break exact speed ties.</summary>
        public bool CoinFlip() => (NextUInt64() & 1UL) == 0UL;

        /// <summary>
        /// Forks a child stream. Lets a subsystem (the AI) draw without shifting the
        /// battle's own draw sequence, which would otherwise change every later roll.
        /// </summary>
        public BattleRandom Fork(int salt) => new BattleRandom(unchecked(Seed * 31 + salt));
    }
}
