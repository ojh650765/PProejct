using System;

namespace PokeLab.Intelligence.Data
{
    /// <summary>
    /// The five exported artefacts, parsed. Deliberately engine-free: something else reads
    /// the bytes (from StreamingAssets in the player, from disk in the verification
    /// harness) and hands them here, so the whole data layer stays testable without Unity.
    /// </summary>
    public sealed class PokeLabData
    {
        public SpeciesRegistry Species { get; }
        public MoveRegistry Moves { get; }
        public PokeLabTypeChart TypeChart { get; }
        public Model.RandomForest Forest { get; }
        public CombatRecords Combats { get; }

        public PokeLabData(SpeciesRegistry species, MoveRegistry moves, PokeLabTypeChart typeChart,
                           Model.RandomForest forest, CombatRecords combats)
        {
            Species = species ?? throw new ArgumentNullException(nameof(species));
            Moves = moves ?? throw new ArgumentNullException(nameof(moves));
            TypeChart = typeChart ?? throw new ArgumentNullException(nameof(typeChart));
            Forest = forest ?? throw new ArgumentNullException(nameof(forest));
            // combats.bin is the one optional artefact: without it the readout simply omits
            // the historical head-to-head line.
            Combats = combats;
        }

        /// <summary>File names under StreamingAssets/pokelab, produced by Tools/Export.</summary>
        public static class Files
        {
            public const string Directory = "pokelab";
            public const string Species = "species.json";
            public const string Moves = "moves.json";
            public const string TypeChart = "typechart.json";
            public const string Forest = "forest.bin";
            public const string Combats = "combats.bin";
        }

        /// <summary>Parses already-read file contents. Cheap enough to sit on a worker thread.</summary>
        public static PokeLabData Parse(string speciesJson, string movesJson, string typeChartJson,
                                        byte[] forestBytes, byte[] combatBytes)
        {
            return new PokeLabData(
                SpeciesRegistry.Load(speciesJson),
                MoveRegistry.Load(movesJson),
                PokeLabTypeChart.Load(typeChartJson),
                Model.RandomForest.Load(forestBytes),
                combatBytes != null && combatBytes.Length > 0 ? CombatRecords.Load(combatBytes) : null);
        }
    }
}
