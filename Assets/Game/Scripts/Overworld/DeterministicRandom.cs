using System;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A small xorshift128 generator.
    ///
    /// Why not <see cref="UnityEngine.Random"/>: encounter rolls have to be reproducible from
    /// a saved world seed plus a step counter, so a replay of the same walk through the same
    /// grass produces the same encounters. Unity's global generator is shared state that any
    /// system can perturb, which makes that impossible.
    /// </summary>
    public struct DeterministicRandom
    {
        private uint _x, _y, _z, _w;

        public DeterministicRandom(int seed)
        {
            // Splitmix-style expansion so adjacent seeds do not produce correlated streams.
            var s = (uint)seed;
            _x = Scramble(ref s);
            _y = Scramble(ref s);
            _z = Scramble(ref s);
            _w = Scramble(ref s);
            if ((_x | _y | _z | _w) == 0u) _x = 0x9E3779B9u; // xorshift dies on the all-zero state
        }

        private static uint Scramble(ref uint state)
        {
            state += 0x9E3779B9u;
            var z = state;
            z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
            z = (z ^ (z >> 13)) * 0xC2B2AE35u;
            return z ^ (z >> 16);
        }

        /// <summary>Raw 32-bit draw.</summary>
        public uint NextUInt()
        {
            var t = _x ^ (_x << 11);
            _x = _y; _y = _z; _z = _w;
            _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
            return _w;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

        /// <summary>Uniform in [min, max).</summary>
        public float Range(float min, float max) => min + (max - min) * NextFloat();

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            var span = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % span);
        }

        /// <summary>Inclusive integer draw, matching the level ranges authored on encounter tables.</summary>
        public int RangeInclusive(int min, int max) => Range(min, max + 1);

        public bool Chance(float probability) => NextFloat() < probability;

        /// <summary>A fresh independent seed, for handing a deterministic sub-stream to a battle.</summary>
        public int NextSeed() => unchecked((int)NextUInt());

        /// <summary>
        /// Stateless hash of a counter against a seed. Used where a roll must be a pure
        /// function of "which step was this" rather than of call order, so an encounter check
        /// cannot be shifted by an unrelated system consuming a draw.
        /// </summary>
        public static float Hash01(int seed, int counter)
        {
            unchecked
            {
                var h = (uint)seed * 2654435761u ^ (uint)counter * 2246822519u;
                h ^= h >> 15; h *= 2246822519u;
                h ^= h >> 13; h *= 3266489917u;
                h ^= h >> 16;
                return (h >> 8) * (1f / 16777216f);
            }
        }

        /// <summary>Deterministic 32-bit hash of a string, for turning ids into seeds.</summary>
        public static int HashString(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            unchecked
            {
                var hash = 2166136261u; // FNV-1a, chosen because it is stable across .NET versions
                for (var i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619u;
                }
                return (int)hash;
            }
        }
    }
}
