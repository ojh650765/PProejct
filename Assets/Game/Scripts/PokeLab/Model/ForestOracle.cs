using System;
using System.Collections.Generic;
using PokeLab.Core;
using PokeLab.Intelligence.Data;

namespace PokeLab.Intelligence.Model
{
    /// <summary>
    /// The species-level predictor: a faithful port of PokemonBattleService.predict.
    ///
    /// Cost note. One Predict is 24 forest evaluations — two for the symmetric
    /// probability, then two more for each of the 11 neutral substitutions. At 120 trees
    /// that is roughly 60k node visits, which is fast but not free, so completed results
    /// are cached by ordered species pair and every scratch buffer is reused.
    /// </summary>
    public sealed class ForestOracle
    {
        private readonly RandomForest _forest;
        private readonly SpeciesRegistry _species;
        private readonly PokeLabTypeChart _chart;
        private readonly CombatRecords _combats;

        // Scratch buffers. Predict is main-thread only, so reusing them is safe and keeps
        // the per-call allocation down to the result object itself.
        private readonly float[] _forward = new float[MatchupFeatures.Count];
        private readonly float[] _reverse = new float[MatchupFeatures.Count];
        private readonly float[] _neutralForward = new float[MatchupFeatures.Count];
        private readonly float[] _neutralReverse = new float[MatchupFeatures.Count];

        private readonly Dictionary<long, MatchupPrediction> _cache = new Dictionary<long, MatchupPrediction>();

        public ForestOracle(RandomForest forest, SpeciesRegistry species,
                            PokeLabTypeChart chart, CombatRecords combats)
        {
            _forest = forest ?? throw new ArgumentNullException(nameof(forest));
            _species = species ?? throw new ArgumentNullException(nameof(species));
            _chart = chart ?? throw new ArgumentNullException(nameof(chart));
            _combats = combats;

            if (_forest.FeatureCount != MatchupFeatures.Count)
                throw new InvalidOperationException(
                    $"forest expects {_forest.FeatureCount} features, the port builds {MatchupFeatures.Count}.");
        }

        public int CachedPredictions => _cache.Count;

        /// <summary>
        /// backend.py::_symmetric_probability — average the forward reading with the
        /// mirrored one. This is the whole reason Predict(A, B) and Predict(B, A) agree:
        /// the forest itself is not order-invariant, so the readout is made so by
        /// construction rather than by hoping the model learned the symmetry.
        /// </summary>
        private double SymmetricProbability(float[] forward, float[] reverse)
        {
            var pAb = _forest.PositiveProbability(forward);
            var pBa = _forest.PositiveProbability(reverse);
            var symmetric = 0.5d * (pAb + 1d - pBa);
            return symmetric < 0d ? 0d : symmetric > 1d ? 1d : symmetric;
        }

        public MatchupPrediction Predict(int firstSpeciesId, int secondSpeciesId)
        {
            var key = ((long)firstSpeciesId << 32) | (uint)secondSpeciesId;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            if (!_species.TryGet(firstSpeciesId, out var first) ||
                !_species.TryGet(secondSpeciesId, out var second))
            {
                throw new KeyNotFoundException(
                    $"species {firstSpeciesId} or {secondSpeciesId} is not in the exported dex.");
            }

            var prediction = Compute(first, second);
            _cache[key] = prediction;
            return prediction;
        }

        private MatchupPrediction Compute(SpeciesData first, SpeciesData second)
        {
            var firstStats = _species.ModelStats(first.Id);
            var secondStats = _species.ModelStats(second.Id);

            MatchupFeatures.Build(first, second, firstStats, secondStats, _chart, _forward);
            MatchupFeatures.Build(second, first, secondStats, firstStats, _chart, _reverse);

            // Mirrors backend.py: a species against itself is 0.5 by definition and gets no
            // evidence, because every difference feature is identically zero.
            var mirrorMatch = first.Id == second.Id;
            var selected = mirrorMatch ? 0.5d : SymmetricProbability(_forward, _reverse);

            var evidence = mirrorMatch
                ? Array.Empty<PredictionEvidence>()
                : BuildEvidence(selected);

            var typeFirst = MatchupFeatures.BestEffect(first, second, _chart, false, out _, out _);
            var typeSecond = MatchupFeatures.BestEffect(second, first, _chart, false, out _, out _);
            var abilityFirst = MatchupFeatures.BestEffect(first, second, _chart, true,
                                                          out var firstBlocked1, out var firstBlocked2);
            var abilitySecond = MatchupFeatures.BestEffect(second, first, _chart, true,
                                                           out var secondBlocked1, out var secondBlocked2);

            var warnings = BuildWarnings(first, second, firstBlocked1, firstBlocked2,
                                         secondBlocked1, secondBlocked2);

            int battles = 0, firstWins = 0, secondWins = 0;
            _combats?.TryGet(first.Id, second.Id, out battles, out firstWins, out secondWins);

            return new MatchupPrediction
            {
                FirstSpeciesId = first.Id,
                SecondSpeciesId = second.Id,
                FirstWinProbability = (float)selected,
                TypeEffectFirst = (float)typeFirst,
                TypeEffectSecond = (float)typeSecond,
                AbilityTypeEffectFirst = (float)abilityFirst,
                AbilityTypeEffectSecond = (float)abilitySecond,
                Evidence = evidence,
                DirectBattles = battles,
                DirectFirstWins = firstWins,
                DirectSecondWins = secondWins,
                Warnings = warnings,
            };
        }

        /// <summary>
        /// The neutral-substitution explanation from backend.py: zero one feature in both
        /// directions, re-read the symmetric probability, and report the gap in percentage
        /// points. Note the neutral probability is deliberately NOT clamped — Python only
        /// clamps the headline figure, and clamping here would flatten evidence at the
        /// extremes.
        /// </summary>
        private PredictionEvidence[] BuildEvidence(double selected)
        {
            var evidence = new PredictionEvidence[MatchupFeatures.Count];

            for (var index = 0; index < MatchupFeatures.Count; index++)
            {
                Array.Copy(_forward, _neutralForward, MatchupFeatures.Count);
                Array.Copy(_reverse, _neutralReverse, MatchupFeatures.Count);
                _neutralForward[index] = 0f;
                _neutralReverse[index] = 0f;

                var pAb = _forest.PositiveProbability(_neutralForward);
                var pBa = _forest.PositiveProbability(_neutralReverse);
                var neutral = 0.5d * (pAb + 1d - pBa);

                evidence[index] = new PredictionEvidence
                {
                    Feature = MatchupFeatures.FeatureNames[index],
                    Label = PokeLabStrings.EvidenceLabel(index),
                    Value = _forward[index],
                    ProbabilityPoints = (float)(100d * (selected - neutral)),
                };
            }

            // Descending absolute contribution, matching backend.py's sort. Python's sort
            // is stable, so equal contributions must keep model feature order; Array.Sort
            // is not, hence the explicit index tiebreak.
            var order = new int[MatchupFeatures.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                var byMagnitude = Math.Abs(evidence[b].ProbabilityPoints)
                                      .CompareTo(Math.Abs(evidence[a].ProbabilityPoints));
                return byMagnitude != 0 ? byMagnitude : a.CompareTo(b);
            });

            var sorted = new PredictionEvidence[MatchupFeatures.Count];
            for (var i = 0; i < order.Length; i++) sorted[i] = evidence[order[i]];
            return sorted;
        }

        private static string[] BuildWarnings(SpeciesData first, SpeciesData second,
                                              ElementType firstBlocked1, ElementType firstBlocked2,
                                              ElementType secondBlocked1, ElementType secondBlocked2)
        {
            var warnings = new List<string>(3) { PokeLabStrings.ModelCaveat };

            var firstBlockedText = JoinTypes(firstBlocked1, firstBlocked2);
            if (firstBlockedText != null)
                warnings.Add(PokeLabStrings.AbilityMayNullify(PokeLabStrings.SpeciesName(second),
                                                              PokeLabStrings.SpeciesName(first), firstBlockedText));

            var secondBlockedText = JoinTypes(secondBlocked1, secondBlocked2);
            if (secondBlockedText != null)
                warnings.Add(PokeLabStrings.AbilityMayNullify(PokeLabStrings.SpeciesName(first),
                                                              PokeLabStrings.SpeciesName(second), secondBlockedText));

            return warnings.ToArray();
        }

        private static string JoinTypes(ElementType a, ElementType b)
        {
            if (a == ElementType.None) return null;
            if (b == ElementType.None) return PokeLabStrings.TypeName(a);
            return $"{PokeLabStrings.TypeName(a)}, {PokeLabStrings.TypeName(b)}";
        }
    }
}
