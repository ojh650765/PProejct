using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Intelligence.Data
{
    /// <summary>
    /// The exported dex. Also carries the model-facing stat table, which is not always the
    /// same thing: SpeciesData.BaseStats has the gen7+ corrections applied because that is
    /// what the game should simulate, while the forest was trained on the uncorrected gen6
    /// numbers. Feeding it corrected stats would quietly diverge from backend.py, so the
    /// export ships an override row for each of the species that changed.
    /// </summary>
    public sealed class SpeciesRegistry : ISpeciesRegistry
    {
        private readonly SpeciesData[] _ordered;
        private readonly Dictionary<int, SpeciesData> _byId;
        private readonly Dictionary<string, SpeciesData> _byName;
        private readonly Dictionary<int, int[]> _modelStats;

        private SpeciesRegistry(SpeciesData[] ordered, Dictionary<int, SpeciesData> byId,
                                Dictionary<string, SpeciesData> byName, Dictionary<int, int[]> modelStats)
        {
            _ordered = ordered;
            _byId = byId;
            _byName = byName;
            _modelStats = modelStats;
        }

        public int Count => _ordered.Length;
        public IReadOnlyList<SpeciesData> All => _ordered;

        public bool TryGet(int speciesId, out SpeciesData species) => _byId.TryGetValue(speciesId, out species);

        public bool TryFindByName(string nameEnOrKo, out SpeciesData species)
        {
            species = null;
            if (string.IsNullOrWhiteSpace(nameEnOrKo)) return false;
            return _byName.TryGetValue(nameEnOrKo.Trim(), out species);
        }

        /// <summary>
        /// Base stats as the model was trained on them, in <see cref="StatKind"/> order.
        /// Identical to <see cref="SpeciesData.BaseStats"/> except for the 25 species the
        /// gen7+ correction table touched.
        /// </summary>
        public int[] ModelStats(int speciesId)
        {
            if (_modelStats.TryGetValue(speciesId, out var overridden)) return overridden;
            return _byId.TryGetValue(speciesId, out var species) ? species.BaseStats : null;
        }

        public static SpeciesRegistry Load(string json)
        {
            var root = JsonValue.Parse(json);
            var rows = root["Species"].AsArray();
            if (rows.Count == 0) throw new InvalidOperationException("species.json contains no species.");

            var ordered = new SpeciesData[rows.Count];
            var byId = new Dictionary<int, SpeciesData>(rows.Count);
            // Names are matched case-insensitively; Korean names are unaffected by casing.
            var byName = new Dictionary<string, SpeciesData>(rows.Count * 2, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var species = new SpeciesData
                {
                    Id = row["Id"].AsInt(),
                    NationalDex = row["NationalDex"].AsInt(),
                    NameEn = row["NameEn"].AsString(string.Empty),
                    NameKo = row["NameKo"].AsString(string.Empty),
                    Type1 = (ElementType)row["Type1"].AsInt(-1),
                    Type2 = (ElementType)row["Type2"].AsInt(-1),
                    BaseStats = row["BaseStats"].AsIntArray(),
                    Height = row["Height"].AsFloat(),
                    Weight = row["Weight"].AsFloat(),
                    BaseExperience = row["BaseExperience"].AsInt(),
                    Generation = row["Generation"].AsInt(),
                    Legendary = row["Legendary"].AsBool(),
                    Abilities = row["Abilities"].AsStringArray(),
                    AbilitiesKo = row["AbilitiesKo"].AsStringArray(),
                    StarterCost = row["StarterCost"].AsInt(),
                    ArtKey = row["ArtKey"].AsString(string.Empty),
                };

                if (species.BaseStats.Length != StatKinds.BaseCount)
                    throw new InvalidOperationException(
                        $"species {species.Id} has {species.BaseStats.Length} base stats, expected {StatKinds.BaseCount}");

                ordered[i] = species;
                byId[species.Id] = species;
                if (!string.IsNullOrEmpty(species.NameEn)) byName[species.NameEn] = species;
                if (!string.IsNullOrEmpty(species.NameKo)) byName[species.NameKo] = species;
            }

            var modelStats = new Dictionary<int, int[]>();
            foreach (var entry in root["ModelStatOverrides"].AsArray())
            {
                var stats = entry["ModelStats"].AsIntArray();
                if (stats.Length == StatKinds.BaseCount) modelStats[entry["Id"].AsInt()] = stats;
            }

            return new SpeciesRegistry(ordered, byId, byName, modelStats);
        }
    }
}
