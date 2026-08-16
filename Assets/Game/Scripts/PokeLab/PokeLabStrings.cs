using System;
using System.Globalization;
using PokeLab.Core;

namespace PokeLab.Intelligence
{
    /// <summary>
    /// Every string the Poké Lab shows a player, in one place so the whole voice can be
    /// reviewed and localised in a single pass. Nothing outside this file may compose
    /// player-facing prose.
    ///
    /// The voice is a trainer's field device: it names the creature, names the mechanism,
    /// and commits to a number. It never mentions features, models or probabilities as
    /// such — "Speed advantage" is a thing a trainer sees, "Speed_diff" is not.
    /// </summary>
    public static class PokeLabStrings
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        // --- Type names ---------------------------------------------------------------

        private static readonly string[] TypeNames =
        {
            "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison",
            "Ground", "Flying", "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark",
            "Steel", "Fairy",
        };

        public static string TypeName(ElementType type) =>
            type == ElementType.None || (int)type >= TypeNames.Length ? "Typeless" : TypeNames[(int)type];

        /// <summary>"Grass/Poison", or just "Fire" for a single-typed creature.</summary>
        public static string TypeLine(ElementType first, ElementType second) =>
            second == ElementType.None ? TypeName(first) : $"{TypeName(first)}/{TypeName(second)}";

        // --- Names --------------------------------------------------------------------

        // Name selection is a localisation decision and there is now one place that makes
        // it. Before Loc existed these preferred English while SpeciesData.DisplayName
        // preferred Korean, so the same creature could be named in two languages inside one
        // sentence depending on which path reached it.

        public static string MoveName(MoveData move)
        {
            if (move == null) return "an unknown move";
            return Loc.Pick(move.NameEn, move.NameKo);
        }

        public static string SpeciesName(SpeciesData species)
        {
            if (species == null) return "the opposing creature";
            return Loc.Pick(species.NameEn, species.NameKo);
        }

        /// <summary>Same, but the fallback reads as one of the player's own party.</summary>
        public static string PartyMemberName(SpeciesData species)
        {
            if (species == null) return "your next creature";
            return Loc.Pick(species.NameEn, species.NameKo);
        }

        // --- Evidence labels ----------------------------------------------------------

        /// <summary>
        /// Player-facing names for the 11 model features, indexed to match
        /// MatchupFeatures.FeatureNames. backend.py's FEATURE_LABELS are Korean; these are
        /// the English equivalents, phrased as things a trainer would notice.
        /// </summary>
        private static readonly string[] EvidenceLabels =
        {
            "HP advantage",
            "Attack advantage",
            "Defence advantage",
            "Sp. Attack advantage",
            "Sp. Defence advantage",
            "Speed advantage",
            "Size difference",
            "Weight difference",
            "Physical pressure",
            "Special pressure",
            "Type advantage",
        };

        public static string EvidenceLabel(int featureIndex) =>
            featureIndex >= 0 && featureIndex < EvidenceLabels.Length
                ? EvidenceLabels[featureIndex]
                : "Unknown factor";

        // --- Warnings -----------------------------------------------------------------

        /// <summary>backend.py's ability-immunity caveat, in the scanner's voice.</summary>
        public static string AbilityMayNullify(string defenderName, string attackerName, string blockedTypes) =>
            $"{defenderName}'s possible ability may nullify {attackerName}'s {blockedTypes} attacks.";

        public static string ModelCaveat =>
            "Read from historical win patterns — it does not account for held items, levels or move choice.";

        public static string NotScouted(string opponentName) =>
            $"{opponentName} is unscouted. These numbers are a species estimate, not a read on this individual.";

        // --- Type summary -------------------------------------------------------------

        public static string TypeSummary(string playerName, string opponentName,
                                         ElementType opponentType1, ElementType opponentType2,
                                         float playerEffect, float opponentEffect)
        {
            var opponentTypes = TypeLine(opponentType1, opponentType2);

            if (playerEffect <= 0f && opponentEffect <= 0f)
                return $"Neither side's typing can touch the other — {opponentTypes} against yours is a stalemate.";
            if (playerEffect <= 0f)
                return $"Its {opponentTypes} typing walls your attacks outright. You need coverage from elsewhere.";
            if (opponentEffect <= 0f)
                return $"{opponentName} cannot reach you with its own typing — press the advantage.";

            if (playerEffect > opponentEffect)
                return $"Your typing bites into {opponentTypes} at {Multiplier(playerEffect)} — {playerName} holds the better angle.";
            if (playerEffect < opponentEffect)
                return $"Its {opponentTypes} typing blunts your attacks and hits back at {Multiplier(opponentEffect)}.";
            return $"Typing is even against {opponentTypes} — this comes down to stats and speed.";
        }

        /// <summary>"2x", "0.5x", "0.25x" — trimmed so it reads like a gauge, not a float.</summary>
        public static string Multiplier(float value)
        {
            if (value <= 0f) return "0x";
            var text = value.ToString("0.##", Culture);
            return text + "x";
        }

        public static string Percent(float probability01) =>
            Math.Round(probability01 * 100f).ToString("0", Culture) + "%";

        public static string SignedPoints(float points)
        {
            var rounded = Math.Round(points);
            return (rounded > 0 ? "+" : string.Empty) + rounded.ToString("0", Culture) + " pts";
        }

        // --- Threats ------------------------------------------------------------------

        public static string ThreatHeadline(ThreatLevel level, string opponentName, string moveName) => level switch
        {
            ThreatLevel.Lethal => $"{opponentName}'s {moveName} knocks you out next turn.",
            ThreatLevel.Dangerous => $"{opponentName}'s {moveName} is close to lethal.",
            ThreatLevel.Manageable => $"{opponentName}'s {moveName} will hurt, but you survive it.",
            _ => $"{opponentName}'s {moveName} barely registers.",
        };

        public static string ThreatDetail(string moveName, ElementType moveType, float expectedFraction,
                                          float koChance, Effectiveness effectiveness)
        {
            var damage = Math.Round(expectedFraction * 100f).ToString("0", Culture);
            var effect = effectiveness switch
            {
                Effectiveness.SuperEffective => " and it is super effective",
                Effectiveness.NotVeryEffective => " though your typing resists it",
                Effectiveness.Immune => " but you are immune",
                _ => string.Empty,
            };
            var ko = koChance >= 0.01f
                ? $" Knockout chance {Percent(koChance)}."
                : string.Empty;
            return $"{moveName} ({TypeName(moveType)}) averages {damage}% of your health{effect}.{ko}";
        }

        public static string UnknownThreat(string opponentName) =>
            $"{opponentName}'s moves are unknown — treat anything it does as a live threat.";

        // --- Switches -----------------------------------------------------------------

        public static string SwitchGain(string candidateName, float gainPoints, string reason) =>
            $"{candidateName} swings this {SignedPoints(gainPoints)} — {reason}.";

        public static string SwitchLoss(string candidateName) =>
            $"{candidateName} comes in worse off than what you already have out.";

        public static string SwitchReasonTyping(ElementType against) =>
            $"its typing answers {TypeName(against)}";

        public static string SwitchReasonBulk() =>
            "it can absorb what is coming";

        public static string SwitchCostsFreeHit(string candidateName, float fraction) =>
            $"{candidateName} eats roughly {Math.Round(fraction * 100f).ToString("0", Culture)}% on the way in";

        // --- Recommended line ---------------------------------------------------------

        public static string RecommendMoveNamed(string creatureName, string moveName, float projected) =>
            $"{creatureName}'s {moveName} swings this to {Percent(projected)}.";

        public static string RecommendKo(string moveName) =>
            $"{moveName} finishes it this turn — take it.";

        public static string RecommendSwitch(string candidateName, float projected) =>
            $"Pull out and send {candidateName} in; that resets the matchup to {Percent(projected)}.";

        public static string RecommendStall(string opponentName) =>
            $"Nothing you have dents {opponentName} right now — buy a turn and reposition.";

        public static string RecommendHold() =>
            "You are ahead on this exchange. Keep the pressure on and do not switch.";

        public static string RecommendHeal(string creatureName) =>
            $"{creatureName} is one hit from fainting. Heal or pull it out now.";

        public static string RecommendScout(string opponentName) =>
            $"Let {opponentName} move once — the read is guesswork until it shows you a move.";

        // --- Status commentary --------------------------------------------------------

        public static string StatusNote(StatusCondition status, string creatureName) => status switch
        {
            StatusCondition.Paralysis => $"{creatureName} is paralysed — it loses most speed ties and stalls a quarter of its turns.",
            StatusCondition.Burn => $"{creatureName} is burned — its physical damage is halved and it chips away each turn.",
            StatusCondition.Poison => $"{creatureName} is poisoned and bleeding health every turn.",
            StatusCondition.BadPoison => $"{creatureName} is badly poisoned — the damage climbs every turn it stays in.",
            StatusCondition.Sleep => $"{creatureName} is asleep and cannot act yet.",
            StatusCondition.Freeze => $"{creatureName} is frozen solid.",
            _ => null,
        };
    }
}
