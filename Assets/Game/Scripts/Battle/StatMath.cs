using System;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// The arithmetic every other battle file agrees on: derived stats, stat stage
    /// multipliers and the experience curve. Kept separate from the engine so the tests
    /// can pin the numbers without standing up a battle.
    ///
    /// All of it is integer-exact where the reference games are integer-exact; float only
    /// appears where the mechanic itself is a multiplier.
    /// </summary>
    public static class StatMath
    {
        /// <summary>Lowest and highest stat stage. Stages saturate here, they do not wrap.</summary>
        public const int MinStage = -6;
        public const int MaxStage = 6;

        /// <summary>Effort values are not authored in this slice; the formula still needs the term.</summary>
        public const int DefaultEffortValue = 0;

        /// <summary>HP stat from base HP, IV and level. Shedinja-style 1 HP species are not in the roster.</summary>
        public static int ComputeHp(int baseHp, int iv, int level, int effort = DefaultEffortValue)
        {
            level = Math.Max(1, level);
            return ((2 * baseHp + iv + effort / 4) * level) / 100 + level + 10;
        }

        /// <summary>Any non-HP stat from base value, IV and level. Nature is intentionally omitted.</summary>
        public static int ComputeStat(int baseValue, int iv, int level, int effort = DefaultEffortValue)
        {
            level = Math.Max(1, level);
            // Natures are not part of the slice, so the 1.0 nature multiplier is folded out
            // rather than left as a redundant float round-trip that could drift.
            return ((2 * baseValue + iv + effort / 4) * level) / 100 + 5;
        }

        /// <summary>
        /// Multiplier for a regular battle stat at the given stage: (2+n)/2 rising,
        /// 2/(2+|n|) falling.
        /// </summary>
        public static float StageMultiplier(int stage)
        {
            stage = Clamp(stage);
            return stage >= 0 ? (2f + stage) / 2f : 2f / (2f - stage);
        }

        /// <summary>
        /// Accuracy and evasion use the shallower 3/3 curve so that a single drop does not
        /// halve the chance to connect.
        /// </summary>
        public static float AccuracyStageMultiplier(int stage)
        {
            stage = Clamp(stage);
            return stage >= 0 ? (3f + stage) / 3f : 3f / (3f - stage);
        }

        /// <summary>Saturating clamp into [-6, +6].</summary>
        public static int Clamp(int stage) => stage < MinStage ? MinStage : stage > MaxStage ? MaxStage : stage;

        /// <summary>True when <paramref name="stat"/> uses the accuracy curve rather than the regular one.</summary>
        public static bool IsAccuracyLike(StatKind stat) => stat == StatKind.Accuracy || stat == StatKind.Evasion;

        /// <summary>Multiplier for any stat kind, picking the right curve automatically.</summary>
        public static float MultiplierFor(StatKind stat, int stage) =>
            IsAccuracyLike(stat) ? AccuracyStageMultiplier(stage) : StageMultiplier(stage);

        // ---- Experience -------------------------------------------------------------

        /// <summary>
        /// Total experience required to reach a level on the medium-fast curve (n³).
        /// The slice does not author per-species growth rates, so one curve is used for all.
        /// </summary>
        public static int ExperienceForLevel(int level)
        {
            if (level <= 1) return 0;
            var capped = Math.Min(level, MaxLevel);
            return capped * capped * capped;
        }

        /// <summary>Level implied by a total experience value, on the same medium-fast curve.</summary>
        public static int LevelForExperience(int totalExperience)
        {
            var level = 1;
            while (level < MaxLevel && totalExperience >= ExperienceForLevel(level + 1)) level++;
            return level;
        }

        /// <summary>Experience awarded for defeating one creature, split across participants.</summary>
        public static int ExperienceFromDefeat(int baseExperience, int defeatedLevel, int participantCount, bool isTrainerBattle)
        {
            if (baseExperience <= 0 || defeatedLevel <= 0) return 0;
            var participants = Math.Max(1, participantCount);
            // The classic gen 5 share: base × level / 7, then split, then a trainer bonus.
            var raw = baseExperience * defeatedLevel / 7;
            if (isTrainerBattle) raw = raw * 3 / 2;
            return Math.Max(1, raw / participants);
        }

        public const int MaxLevel = 100;
    }
}
