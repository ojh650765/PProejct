using System;
using System.Collections.Generic;

namespace PokeLab.Core
{
    /// <summary>
    /// One feature's contribution to the win probability, ported from the
    /// neutral-substitution explanation in backend.py: each feature is zeroed in turn
    /// and the resulting probability shift is reported in percentage points.
    /// </summary>
    [Serializable]
    public struct PredictionEvidence
    {
        /// <summary>Model feature name, e.g. "Speed_diff".</summary>
        public string Feature;
        /// <summary>Player-facing label, e.g. "Speed advantage".</summary>
        public string Label;
        /// <summary>The raw feature value for this matchup.</summary>
        public float Value;
        /// <summary>Signed percentage points this feature moved the probability.</summary>
        public float ProbabilityPoints;
    }

    /// <summary>
    /// Result of the ported random forest, matching BattlePrediction in backend.py.
    /// The probability is order-invariant: predicting (B, A) yields exactly 1 - p.
    /// </summary>
    [Serializable]
    public sealed class MatchupPrediction
    {
        public int FirstSpeciesId;
        public int SecondSpeciesId;

        /// <summary>Symmetric win probability for the first species, 0-1.</summary>
        public float FirstWinProbability;
        public float SecondWinProbability => 1f - FirstWinProbability;

        /// <summary>Best offensive type multiplier each way, before abilities.</summary>
        public float TypeEffectFirst = 1f;
        public float TypeEffectSecond = 1f;
        /// <summary>Same, after applying defensive ability immunities.</summary>
        public float AbilityTypeEffectFirst = 1f;
        public float AbilityTypeEffectSecond = 1f;

        /// <summary>Sorted by descending absolute contribution.</summary>
        public PredictionEvidence[] Evidence = Array.Empty<PredictionEvidence>();

        /// <summary>Historical head-to-head record from final_combats.csv, 0 when unseen.</summary>
        public int DirectBattles;
        public int DirectFirstWins;
        public int DirectSecondWins;

        /// <summary>Caveats to surface in the scanner, e.g. a possible immunity ability.</summary>
        public string[] Warnings = Array.Empty<string>();
    }

    public enum ThreatLevel
    {
        Trivial = 0,
        Manageable = 1,
        Dangerous = 2,
        Lethal = 3,
    }

    /// <summary>A specific danger the scanner has identified on the field right now.</summary>
    [Serializable]
    public struct ThreatAssessment
    {
        public ThreatLevel Level;
        /// <summary>One-line explanation, already localised.</summary>
        public string Headline;
        public string Detail;
        /// <summary>Move that produces this threat, when attributable.</summary>
        public string SourceMoveId;
        /// <summary>Chance the active player creature is knocked out next turn, 0-1.</summary>
        public float IncomingKoChance;
    }

    /// <summary>A recommended switch, ranked by the tactical layer.</summary>
    [Serializable]
    public struct SwitchRecommendation
    {
        public int PartyIndex;
        public string InstanceId;
        /// <summary>Predicted win probability after the switch, 0-1.</summary>
        public float ProjectedWinProbability;
        /// <summary>Net gain over staying in, in percentage points.</summary>
        public float GainPoints;
        public string Rationale;
    }

    /// <summary>
    /// The full scanner readout. Recomputed whenever battle conditions change, and
    /// rendered by the Poké Lab UI as an in-world trainer device.
    /// </summary>
    [Serializable]
    public sealed class TacticalReadout
    {
        /// <summary>Species-level prior straight from the ported forest.</summary>
        public MatchupPrediction Matchup;

        /// <summary>
        /// Live probability blending the forest prior with the actual battle state
        /// (current HP, status, stat stages, move coverage). This is the number the
        /// scanner shows as the headline percentage.
        /// </summary>
        public float LiveWinProbability;

        /// <summary>Change in percentage points since the previous readout, for the trend arrow.</summary>
        public float DeltaPoints;

        public ThreatAssessment[] Threats = Array.Empty<ThreatAssessment>();
        public SwitchRecommendation[] Switches = Array.Empty<SwitchRecommendation>();

        /// <summary>Per-move forecasts for the active creature, aligned to its move slots.</summary>
        public DamageForecast[] MoveForecasts = Array.Empty<DamageForecast>();

        /// <summary>Best single line of play, e.g. "Lead with Thunder Wave to remove the speed gap".</summary>
        public string RecommendedLine;

        /// <summary>Human-readable type advantage summary for the readout header.</summary>
        public string TypeSummary;

        /// <summary>False until the opponent has been scouted enough to trust the numbers.</summary>
        public bool IsConfident;
    }

    /// <summary>
    /// The Poké Lab intelligence layer: the ported prediction model plus the tactical
    /// reasoning built on top of it. Implementations must be allocation-light enough to
    /// run on every battle state change.
    /// </summary>
    public interface IPokeLabOracle
    {
        bool IsReady { get; }

        /// <summary>Species-vs-species prior. Order-invariant. Safe to call outside battle.</summary>
        MatchupPrediction Predict(int firstSpeciesId, int secondSpeciesId);

        /// <summary>Full live readout for the current field.</summary>
        TacticalReadout Analyse(IBattleStateView state, IBattleEngine engine);

        /// <summary>
        /// Pre-battle scan from the overworld, before an encounter is committed.
        /// Used by the scanner when the player aims it at grass or at a trainer.
        /// </summary>
        MatchupPrediction ScoutEncounter(CreatureInstance playerLead, int wildSpeciesId);
    }

    /// <summary>Raised whenever the readout changes so the UI can animate the delta.</summary>
    public interface IPokeLabListener
    {
        void OnReadoutUpdated(TacticalReadout readout);
    }
}
