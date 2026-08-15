using System;
using PokeLab.Core;
using PokeLab.Intelligence.Data;

namespace PokeLab.Intelligence.Model
{
    /// <summary>
    /// Builds the 11-feature row the forest expects — a direct port of
    /// PokemonBattleService._feature_frame in backend.py.
    ///
    /// Every intermediate is computed in double and only narrowed to float at the end,
    /// because Python builds the frame in float64 and hands pandas a float32 DataFrame.
    /// Narrowing earlier would round differently and shift values across split thresholds.
    /// </summary>
    public static class MatchupFeatures
    {
        public const int Count = 11;

        // Order is the frozen model contract; see feature_columns in the joblib bundle.
        public const int HpDiff = 0;
        public const int AttackDiff = 1;
        public const int DefenseDiff = 2;
        public const int SpAtkDiff = 3;
        public const int SpDefDiff = 4;
        public const int SpeedDiff = 5;
        public const int LogHeightDiff = 6;
        public const int LogWeightDiff = 7;
        public const int PhysicalPressureDiff = 8;
        public const int SpecialPressureDiff = 9;
        public const int TypeAdvantageDiff = 10;

        public static readonly string[] FeatureNames =
        {
            "HP_diff", "Attack_diff", "Defense_diff", "Sp_Atk_diff", "Sp_Def_diff",
            "Speed_diff", "LogHeight_diff", "LogWeight_diff", "PhysicalPressure_diff",
            "SpecialPressure_diff", "TypeAdvantage_diff",
        };

        /// <summary>
        /// backend.py::_effect_score. An immune matchup gets a hard -3 floor rather than
        /// negative infinity, so the tree can still split on it.
        /// </summary>
        public static double EffectScore(double effect) => effect == 0d ? -3d : Math.Log(effect, 2d);

        /// <summary>
        /// backend.py::_best_effect — the best multiplier the attacker can reach: max over
        /// the attacker's own types, each multiplied across every defender type. With
        /// <paramref name="useAbility"/>, a defensive immunity ability zeroes that type
        /// outright and records it in <paramref name="blocked"/>.
        /// </summary>
        public static double BestEffect(SpeciesData attacker, SpeciesData defender,
                                        PokeLabTypeChart chart, bool useAbility,
                                        out ElementType blockedFirst, out ElementType blockedSecond)
        {
            blockedFirst = ElementType.None;
            blockedSecond = ElementType.None;

            var best = double.NegativeInfinity;
            var any = false;

            for (var slot = 0; slot < 2; slot++)
            {
                var attackType = slot == 0 ? attacker.Type1 : attacker.Type2;
                if (attackType == ElementType.None) continue;

                double effect = 1d;
                if (defender.Type1 != ElementType.None) effect *= chart.Multiplier(attackType, defender.Type1);
                if (defender.Type2 != ElementType.None) effect *= chart.Multiplier(attackType, defender.Type2);

                if (useAbility && chart.AnyGrantsImmunity(defender.Abilities, attackType))
                {
                    effect = 0d;
                    if (blockedFirst == ElementType.None) blockedFirst = attackType;
                    else blockedSecond = attackType;
                }

                if (!any || effect > best) best = effect;
                any = true;
            }

            // A typeless attacker cannot happen in the dex, but Python falls back to 1.0.
            return any ? best : 1d;
        }

        /// <summary>Writes the 11 features for (first vs second) into <paramref name="destination"/>.</summary>
        public static void Build(SpeciesData first, SpeciesData second,
                                 int[] firstStats, int[] secondStats,
                                 PokeLabTypeChart chart, float[] destination)
        {
            if (destination == null || destination.Length != Count)
                throw new ArgumentException($"destination must hold {Count} features", nameof(destination));

            for (var stat = 0; stat < StatKinds.BaseCount; stat++)
                destination[stat] = (float)((double)firstStats[stat] - secondStats[stat]);

            // Height and weight stay in PokeAPI units (decimetres / hectograms) because
            // that is what the model was trained on. log1p keeps the tiny end well spread.
            destination[LogHeightDiff] = (float)(Log1p(first.Height) - Log1p(second.Height));
            destination[LogWeightDiff] = (float)(Log1p(first.Weight) - Log1p(second.Weight));

            var firstAttack = (double)firstStats[(int)StatKind.Attack];
            var firstDefense = (double)firstStats[(int)StatKind.Defense];
            var secondAttack = (double)secondStats[(int)StatKind.Attack];
            var secondDefense = (double)secondStats[(int)StatKind.Defense];
            destination[PhysicalPressureDiff] =
                (float)(firstAttack / (secondDefense + 1d) - secondAttack / (firstDefense + 1d));

            var firstSpAtk = (double)firstStats[(int)StatKind.SpAttack];
            var firstSpDef = (double)firstStats[(int)StatKind.SpDefense];
            var secondSpAtk = (double)secondStats[(int)StatKind.SpAttack];
            var secondSpDef = (double)secondStats[(int)StatKind.SpDefense];
            destination[SpecialPressureDiff] =
                (float)(firstSpAtk / (secondSpDef + 1d) - secondSpAtk / (firstSpDef + 1d));

            // Deliberately the ability-free effect: backend.py feeds the model the raw type
            // matchup and uses the ability-aware one only for the warning text.
            var typeFirst = BestEffect(first, second, chart, false, out _, out _);
            var typeSecond = BestEffect(second, first, chart, false, out _, out _);
            destination[TypeAdvantageDiff] = (float)(EffectScore(typeFirst) - EffectScore(typeSecond));
        }

        /// <summary>
        /// math.log1p. .NET Framework — which is what Unity's Mono still resolves for some
        /// profiles — has no Math.Log1P, so this is the standard accurate formulation:
        /// for small x, log(1+x) * x / ((1+x) - 1) cancels the representation error.
        /// </summary>
        public static double Log1p(double x)
        {
            var sum = 1d + x;
            if (sum == 1d) return x;
            return Math.Log(sum) * x / (sum - 1d);
        }
    }
}
