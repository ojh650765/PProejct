using System;
using PokeLab.Core;
using PokeLab.Intelligence.Data;
using PokeLab.Intelligence.Model;
using PokeLab.Intelligence.Tactics;

namespace PokeLab.Intelligence
{
    /// <summary>
    /// The Poké Lab intelligence layer: the ported random forest plus the tactical
    /// reasoning built on top of it.
    ///
    /// Construct through <see cref="PokeLabData"/> so the heavy files are read once, off
    /// the first frame; this type assumes everything it is handed is already loaded.
    /// </summary>
    public sealed class PokeLabOracle : IPokeLabOracle
    {
        private readonly ForestOracle _forest;
        private readonly SpeciesRegistry _species;
        private readonly TacticalAnalyzer _tactics;

        public PokeLabOracle(PokeLabData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            _species = data.Species;
            _forest = new ForestOracle(data.Forest, data.Species, data.TypeChart, data.Combats);
            _tactics = new TacticalAnalyzer(_forest, data.Species, data.TypeChart, data.Moves);
        }

        public bool IsReady => _forest != null;

        public MatchupPrediction Predict(int firstSpeciesId, int secondSpeciesId) =>
            _forest.Predict(firstSpeciesId, secondSpeciesId);

        public TacticalReadout Analyse(IBattleStateView state, IBattleEngine engine) =>
            _tactics.Analyse(state, engine);

        /// <summary>
        /// Pre-battle scan from the overworld. Identical to <see cref="Predict"/> on the
        /// species pair — there is no battle state yet, so there is nothing further to fold
        /// in, and returning the honest prior beats inventing precision.
        /// </summary>
        public MatchupPrediction ScoutEncounter(CreatureInstance playerLead, int wildSpeciesId)
        {
            if (playerLead == null) throw new ArgumentNullException(nameof(playerLead));
            return _forest.Predict(playerLead.SpeciesId, wildSpeciesId);
        }

        /// <summary>Exposed for the UI's dex screens; the registry is read-only.</summary>
        public ISpeciesRegistry Species => _species;
    }
}
