using System;
using System.IO;

namespace PokeLab.Intelligence.Model
{
    /// <summary>
    /// The ported scikit-learn RandomForestClassifier: 120 trees over 11 features,
    /// flattened by Tools/Export/export_pokelab.py into forest.bin.
    ///
    /// Layout (little-endian throughout), structure-of-arrays so each section loads as one
    /// contiguous read:
    ///   "PLFR" | u32 version | u32 treeCount | u32 featureCount | u32 nodeCount
    ///   u32[treeCount + 1] tree node offsets (prefix sums; roots live at offsets[t])
    ///   i16[nodeCount] split feature, negative marks a leaf
    ///   f32[nodeCount] split threshold
    ///   i32[nodeCount] left child  (global index, -1 at leaves)
    ///   i32[nodeCount] right child (global index, -1 at leaves)
    ///   f32[nodeCount] P(class = 1) at the node
    ///
    /// Child indices are global rather than tree-local so descent needs no per-tree base.
    /// </summary>
    public sealed class RandomForest
    {
        private const uint ExpectedMagic = 0x5246_4C50; // "PLFR" little-endian
        private const uint SupportedVersion = 1;

        private readonly uint[] _treeOffsets;
        private readonly short[] _feature;
        private readonly float[] _threshold;
        private readonly int[] _left;
        private readonly int[] _right;
        private readonly float[] _positive;

        public int TreeCount { get; }
        public int FeatureCount { get; }
        public int NodeCount { get; }

        private RandomForest(uint[] treeOffsets, short[] feature, float[] threshold,
                             int[] left, int[] right, float[] positive, int featureCount)
        {
            _treeOffsets = treeOffsets;
            _feature = feature;
            _threshold = threshold;
            _left = left;
            _right = right;
            _positive = positive;
            TreeCount = treeOffsets.Length - 1;
            FeatureCount = featureCount;
            NodeCount = feature.Length;
        }

        public static RandomForest Load(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadUInt32();
            if (magic != ExpectedMagic)
                throw new InvalidDataException($"forest.bin: bad magic 0x{magic:X8}; re-run Tools/Export.");

            var version = reader.ReadUInt32();
            if (version != SupportedVersion)
                throw new InvalidDataException($"forest.bin: version {version}, expected {SupportedVersion}.");

            var treeCount = (int)reader.ReadUInt32();
            var featureCount = (int)reader.ReadUInt32();
            var nodeCount = (int)reader.ReadUInt32();
            if (treeCount <= 0 || featureCount <= 0 || nodeCount <= 0)
                throw new InvalidDataException("forest.bin: empty forest.");

            var offsets = new uint[treeCount + 1];
            for (var i = 0; i < offsets.Length; i++) offsets[i] = reader.ReadUInt32();
            if (offsets[treeCount] != nodeCount)
                throw new InvalidDataException("forest.bin: tree offsets disagree with node count.");

            var feature = new short[nodeCount];
            for (var i = 0; i < nodeCount; i++) feature[i] = reader.ReadInt16();

            var threshold = new float[nodeCount];
            for (var i = 0; i < nodeCount; i++) threshold[i] = reader.ReadSingle();

            var left = new int[nodeCount];
            for (var i = 0; i < nodeCount; i++) left[i] = reader.ReadInt32();

            var right = new int[nodeCount];
            for (var i = 0; i < nodeCount; i++) right[i] = reader.ReadInt32();

            var positive = new float[nodeCount];
            for (var i = 0; i < nodeCount; i++) positive[i] = reader.ReadSingle();

            return new RandomForest(offsets, feature, threshold, left, right, positive, featureCount);
        }

        /// <summary>
        /// Mean P(class = 1) across the trees — exactly what
        /// RandomForestClassifier.predict_proba(x)[0, 1] returns, since sklearn averages
        /// each tree's normalised leaf distribution.
        ///
        /// <paramref name="features"/> must already be float32-rounded: sklearn casts X to
        /// float32 before descending, and the exported thresholds were rounded down to the
        /// nearest float32 so that a float32 comparison reproduces sklearn's float64 one
        /// exactly.
        /// </summary>
        public double PositiveProbability(float[] features)
        {
            if (features == null) throw new ArgumentNullException(nameof(features));
            if (features.Length != FeatureCount)
                throw new ArgumentException($"expected {FeatureCount} features, got {features.Length}");

            // Local copies: the JIT hoists the bounds checks out of the descent loop.
            var featureIndex = _feature;
            var threshold = _threshold;
            var left = _left;
            var right = _right;
            var positive = _positive;
            var offsets = _treeOffsets;

            var total = 0.0;
            for (var tree = 0; tree < TreeCount; tree++)
            {
                var node = (int)offsets[tree];
                while (true)
                {
                    var split = featureIndex[node];
                    if (split < 0) break; // leaf
                    node = features[split] <= threshold[node] ? left[node] : right[node];
                }
                total += positive[node];
            }
            return total / TreeCount;
        }
    }
}
