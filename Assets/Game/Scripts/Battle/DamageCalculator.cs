using System;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// Mutable working set for one damage computation. Abilities and items adjust the
    /// multiplier fields through their hooks rather than reaching into the formula, which
    /// is what keeps <see cref="DamageCalculator"/> a single readable expression.
    /// </summary>
    public sealed class DamageContext
    {
        public MoveData Move;
        /// <summary>Move type after any ability that changes it. Starts as <see cref="MoveData.Type"/>.</summary>
        public ElementType MoveType;

        public CreatureInstance Attacker;
        public CreatureInstance Defender;
        public SpeciesData AttackerSpecies;
        public SpeciesData DefenderSpecies;
        public BattleSideState AttackerState;
        public BattleSideState DefenderState;
        public BattleSide AttackerSide;

        public Weather Weather;
        public bool Critical;

        /// <summary>Damage roll in percent, 85-100. Fixed by the caller so forecasts stay RNG-free.</summary>
        public int RollPercent = 100;

        /// <summary>
        /// True for Struggle, which is typeless: it neither gains STAB nor consults the
        /// chart, so it can never be walled to zero and leave a battle unable to end.
        /// </summary>
        public bool Typeless;

        public float PowerMultiplier = 1f;
        public float AttackMultiplier = 1f;
        public float DefenseMultiplier = 1f;
        /// <summary>Applied last, after type effectiveness. Used by defensive resist abilities.</summary>
        public float FinalMultiplier = 1f;

        // Filled in by the calculator so callers can report them without recomputing.
        public float TypeMultiplier = 1f;
        public float Stab = 1f;
        public int Damage;

        public void ResetModifiers()
        {
            Typeless = false;
            PowerMultiplier = 1f;
            AttackMultiplier = 1f;
            DefenseMultiplier = 1f;
            FinalMultiplier = 1f;
            TypeMultiplier = 1f;
            Stab = 1f;
            Damage = 0;
        }
    }

    /// <summary>
    /// The damage formula. Pure: given the same context it returns the same number, which
    /// is what lets <see cref="BattleEngine.ForecastMove"/> reuse it with fixed rolls.
    ///
    /// Evaluation order is fixed and load-bearing — the tests hand-compute against exactly
    /// this sequence:
    /// base → weather → crit → roll → STAB → type → burn → final multipliers.
    /// </summary>
    public static class DamageCalculator
    {
        /// <summary>Lowest damage roll, as a percent of the unrolled figure.</summary>
        public const int MinRoll = 85;
        /// <summary>Highest damage roll — the roll never increases damage above the base figure.</summary>
        public const int MaxRoll = 100;
        /// <summary>Mean of the 16 equally likely rolls, used for forecasts.</summary>
        public const float AverageRoll = 92.5f;

        public const float CritMultiplier = 1.5f;
        public const float StabMultiplier = 1.5f;

        /// <summary>Base crit denominator before <see cref="MoveData.CritStageBonus"/>.</summary>
        public static readonly int[] CritDenominators = { 24, 8, 2, 1 };

        /// <summary>Computes damage and fills <paramref name="ctx"/>'s reporting fields.</summary>
        public static int Compute(DamageContext ctx, ITypeChart typeChart)
        {
            if (ctx?.Move == null || ctx.Move.Category == MoveCategory.Status)
            {
                if (ctx != null) ctx.Damage = 0;
                return 0;
            }

            var physical = ctx.Move.Category == MoveCategory.Physical;

            var power = Math.Max(1, Floor(ctx.Move.Power * ctx.PowerMultiplier));
            if (ctx.Move.Power <= 0)
            {
                ctx.Damage = 0;
                return 0;
            }

            var attack = OffensiveStat(ctx, physical);
            var defense = Math.Max(1, DefensiveStat(ctx, physical));

            var level = Math.Max(1, ctx.Attacker.Level);
            var damage = 2 * level / 5 + 2;
            damage = damage * power * attack / defense;
            damage = damage / 50 + 2;

            damage = Floor(damage * WeatherMultiplier(ctx.Weather, ctx.MoveType));

            if (ctx.Critical) damage = Floor(damage * CritMultiplier);

            // Integer roll, exactly as the reference games do it: one of 16 values.
            damage = damage * Math.Clamp(ctx.RollPercent, MinRoll, MaxRoll) / 100;

            ctx.Stab = !ctx.Typeless && HasType(ctx.AttackerSpecies, ctx.MoveType) ? StabMultiplier : 1f;
            damage = Floor(damage * ctx.Stab);

            ctx.TypeMultiplier = TypeMultiplier(ctx, typeChart);
            damage = Floor(damage * ctx.TypeMultiplier);

            // Burn halves physical output. Guts trades the halving for an attack boost, so
            // the ability clears the flag by raising AttackMultiplier and setting this.
            if (physical && ctx.Attacker.Status == StatusCondition.Burn && !IgnoresBurn(ctx))
                damage = Floor(damage * 0.5f);

            damage = Floor(damage * ctx.FinalMultiplier);

            ctx.Damage = ctx.TypeMultiplier <= 0f ? 0 : Math.Max(1, damage);
            return ctx.Damage;
        }

        /// <summary>Combined offensive and defensive type multiplier, including ability modifiers.</summary>
        public static float TypeMultiplier(DamageContext ctx, ITypeChart typeChart)
        {
            if (ctx.Typeless || typeChart == null || ctx.DefenderSpecies == null) return 1f;

            var multiplier = typeChart.Multiplier(ctx.MoveType, ctx.DefenderSpecies.Type1, ctx.DefenderSpecies.Type2);

            // The exported ability table carries immunities and resistances the chart alone
            // cannot express (levitate, flash-fire, and the pokerogue-derived amplifiers).
            if (!string.IsNullOrEmpty(ctx.Defender?.AbilityId))
                multiplier *= typeChart.AbilityModifier(ctx.Defender.AbilityId, ctx.MoveType);

            return multiplier;
        }

        /// <summary>Attack or SpAttack after stages, crit rules and ability multipliers.</summary>
        public static int OffensiveStat(DamageContext ctx, bool physical)
        {
            var stat = physical ? StatKind.Attack : StatKind.SpAttack;
            var raw = ctx.Attacker.Stats[(int)stat];
            var stage = ctx.AttackerState?.Stages[(int)stat] ?? 0;

            // A crit refuses to be blunted by the attacker's own drops.
            if (ctx.Critical && stage < 0) stage = 0;

            return Math.Max(1, Floor(raw * StatMath.StageMultiplier(stage) * ctx.AttackMultiplier));
        }

        /// <summary>Defense or SpDefense after stages, crit rules and ability multipliers.</summary>
        public static int DefensiveStat(DamageContext ctx, bool physical)
        {
            var stat = physical ? StatKind.Defense : StatKind.SpDefense;
            var raw = ctx.Defender.Stats[(int)stat];
            var stage = ctx.DefenderState?.Stages[(int)stat] ?? 0;

            // A crit ignores the target's boosts but still respects its drops.
            if (ctx.Critical && stage > 0) stage = 0;

            var value = raw * StatMath.StageMultiplier(stage) * ctx.DefenseMultiplier;

            // A sandstorm hardens Rock types' special bulk by half.
            if (!physical && ctx.Weather == Weather.Sandstorm && HasType(ctx.DefenderSpecies, ElementType.Rock))
                value *= 1.5f;

            return Math.Max(1, Floor(value));
        }

        /// <summary>Rain and sun push their own element and smother the opposing one.</summary>
        public static float WeatherMultiplier(Weather weather, ElementType moveType)
        {
            switch (weather)
            {
                case Weather.Rain:
                    if (moveType == ElementType.Water) return 1.5f;
                    if (moveType == ElementType.Fire) return 0.5f;
                    break;
                case Weather.Sun:
                    if (moveType == ElementType.Fire) return 1.5f;
                    if (moveType == ElementType.Water) return 0.5f;
                    break;
            }
            return 1f;
        }

        /// <summary>Crit denominator for a move, saturating at the guaranteed-crit stage.</summary>
        public static int CritDenominator(MoveData move)
        {
            var stage = Math.Clamp(move?.CritStageBonus ?? 0, 0, CritDenominators.Length - 1);
            return CritDenominators[stage];
        }

        /// <summary>Probability of a crit for a move, as a 0-1 fraction. Used by forecasts.</summary>
        public static float CritChance(MoveData move) => 1f / CritDenominator(move);

        private static bool IgnoresBurn(DamageContext ctx) =>
            string.Equals(ctx.Attacker?.AbilityId, AbilityIds.Guts, StringComparison.Ordinal);

        internal static bool HasType(SpeciesData species, ElementType type) =>
            species != null && type != ElementType.None && (species.Type1 == type || species.Type2 == type);

        /// <summary>
        /// Floor toward zero on a non-negative product. Written out because the reference
        /// implementation truncates at every step and rounding differences compound.
        /// The epsilon is there because binary floats represent x1.5 and x0.5 chains as
        /// 2.9999998 just as often as 3.0000002, and a bare cast would lose a whole point.
        /// </summary>
        private static int Floor(float value) => value <= 0f ? 0 : (int)(value + 1e-4f);
    }
}
