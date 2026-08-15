using System;
using System.IO;

namespace PokeLab.Intelligence.Data
{
    /// <summary>
    /// Head-to-head tallies from final_combats.csv, collapsed to one row per unordered
    /// species pair.
    ///
    /// Layout (little-endian):
    ///   "PLCB" | u32 version | u32 pairCount
    ///   u32[pairCount] keys, ascending — (min id &lt;&lt; 16) | max id
    ///   u16[pairCount] wins for the lower id
    ///   u16[pairCount] wins for the higher id
    ///
    /// The key packing is what makes the lookup a single binary search over a sorted uint
    /// array: no hashing, no allocation, and 8 bytes per pair keeps all 35k rows inside
    /// 275 KB so it stays resident.
    /// </summary>
    public sealed class CombatRecords
    {
        private const uint ExpectedMagic = 0x4243_4C50; // "PLCB" little-endian
        private const uint SupportedVersion = 1;

        private readonly uint[] _keys;
        private readonly ushort[] _lowWins;
        private readonly ushort[] _highWins;

        public int PairCount => _keys.Length;

        private CombatRecords(uint[] keys, ushort[] lowWins, ushort[] highWins)
        {
            _keys = keys;
            _lowWins = lowWins;
            _highWins = highWins;
        }

        public static CombatRecords Load(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadUInt32();
            if (magic != ExpectedMagic)
                throw new InvalidDataException($"combats.bin: bad magic 0x{magic:X8}; re-run Tools/Export.");
            var version = reader.ReadUInt32();
            if (version != SupportedVersion)
                throw new InvalidDataException($"combats.bin: version {version}, expected {SupportedVersion}.");

            var count = (int)reader.ReadUInt32();
            var keys = new uint[count];
            for (var i = 0; i < count; i++) keys[i] = reader.ReadUInt32();
            var lowWins = new ushort[count];
            for (var i = 0; i < count; i++) lowWins[i] = reader.ReadUInt16();
            var highWins = new ushort[count];
            for (var i = 0; i < count; i++) highWins[i] = reader.ReadUInt16();

            return new CombatRecords(keys, lowWins, highWins);
        }

        /// <summary>
        /// Historical record between the two species. Returns false when the pair never
        /// appeared in the dataset, which is the common case — the scanner then simply
        /// omits the "past encounters" line rather than showing a misleading 0-0.
        /// </summary>
        public bool TryGet(int firstSpeciesId, int secondSpeciesId,
                           out int battles, out int firstWins, out int secondWins)
        {
            battles = firstWins = secondWins = 0;
            if (firstSpeciesId <= 0 || secondSpeciesId <= 0 ||
                firstSpeciesId > ushort.MaxValue || secondSpeciesId > ushort.MaxValue ||
                firstSpeciesId == secondSpeciesId)
                return false;

            var low = Math.Min(firstSpeciesId, secondSpeciesId);
            var high = Math.Max(firstSpeciesId, secondSpeciesId);
            var key = ((uint)low << 16) | (uint)high;

            var index = Array.BinarySearch(_keys, key);
            if (index < 0) return false;

            int lowWins = _lowWins[index];
            int highWins = _highWins[index];
            battles = lowWins + highWins;
            if (firstSpeciesId == low)
            {
                firstWins = lowWins;
                secondWins = highWins;
            }
            else
            {
                firstWins = highWins;
                secondWins = lowWins;
            }
            return true;
        }
    }
}
