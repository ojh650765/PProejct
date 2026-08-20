using System;
using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// The experience curve, as the UI needs to read it.
    ///
    /// <b>This is a mirror, not a second opinion.</b> The authority is
    /// <c>PokeLab.Battle.StatMath.ExperienceForLevel</c> — the medium-fast cubic, n³, one
    /// curve for every species in the slice. PokeLab.UI cannot reference PokeLab.Battle
    /// (see the assembly definitions: UI sees Core and nothing else of the game), and the
    /// alternative to mirroring the three lines here was every bar in the project guessing
    /// its own band, which is exactly what <see cref="CreatureStatusPanel"/> was doing
    /// inline with a comment apologising for it.
    ///
    /// If the engine's curve ever stops being n³, this file is the one place the UI has to
    /// follow it to.
    /// </summary>
    public static class ExperienceCurve
    {
        /// <summary>Matches <c>StatMath.MaxLevel</c>.</summary>
        public const int MaxLevel = 100;

        /// <summary>Total experience needed to have reached <paramref name="level"/>.</summary>
        public static int TotalFor(int level)
        {
            if (level <= 1) return 0;
            var capped = Mathf.Min(level, MaxLevel);
            return capped * capped * capped;
        }

        /// <summary>The level a running total implies.</summary>
        public static int LevelFor(int totalExperience)
        {
            var level = 1;
            while (level < MaxLevel && totalExperience >= TotalFor(level + 1)) level++;
            return level;
        }

        /// <summary>Experience the whole of <paramref name="level"/> is worth. Never zero.</summary>
        public static int SpanOf(int level) => Mathf.Max(1, TotalFor(level + 1) - TotalFor(level));

        /// <summary>
        /// How far through its current level a total sits, 0-1.
        ///
        /// The level is passed in rather than derived because the caller usually already has
        /// the authoritative one — the engine's, or the server's — and deriving a second one
        /// here is how a bar ends up disagreeing with the "Lv." beside it.
        /// </summary>
        public static float FractionWithin(int totalExperience, int level)
        {
            if (level >= MaxLevel) return 1f;
            var floor = TotalFor(level);
            return Mathf.Clamp01((totalExperience - floor) / (float)SpanOf(level));
        }
    }

    /// <summary>
    /// One experience bar filling from one running total to another, rolling over at every
    /// level it crosses on the way.
    ///
    /// <b>Why this is a chain and not a tween.</b> A level-up is not a value change, it is a
    /// discontinuity: the bar has to reach the end, be <i>seen</i> to reach it, empty, and
    /// start again. Three levels in one battle is three of those moments, and one
    /// interpolation across the lot would show none of them — which is exactly what "the
    /// experience bar just slid a bit" looked like.
    ///
    /// The budget is the whole sequence's target length. It is shared out in proportion to how
    /// much bar each segment covers, so a level gained on the last two points of experience
    /// does not get the same second and a half as a level gained from empty — with a floor, so
    /// it still registers as a rollover rather than a flicker.
    ///
    /// It is cancellable because both callers can be skipped: the battle-mode summary by the
    /// player pressing a key, and the in-battle plate by the creature being switched out from
    /// under it. A cancelled roll stops chaining and does <b>not</b> report completion — the
    /// caller that cancelled is the one that decides what the bar should read instead.
    /// </summary>
    public sealed class ExperienceRoll
    {
        private bool _cancelled;

        /// <summary>True until the roll finishes or is cancelled.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>Stops the chain where it is. Safe to call more than once.</summary>
        public void Cancel()
        {
            _cancelled = true;
            IsRunning = false;
        }

        /// <summary>
        /// Starts a roll. <paramref name="onRollover"/> is handed the level just reached, and
        /// is where the caller puts its own flourish — the level caption, a shout, a punch.
        /// The bar is emptied for the next lap after that callback returns.
        /// </summary>
        public static ExperienceRoll Play(AnimatedBar bar, int fromTotal, int fromLevel,
            int toTotal, int toLevel, float budget, Action<int> onRollover, Action onComplete)
        {
            var roll = new ExperienceRoll();
            if (bar == null) { onComplete?.Invoke(); return roll; }

            fromLevel = Mathf.Clamp(fromLevel, 1, ExperienceCurve.MaxLevel);
            toLevel = Mathf.Clamp(Mathf.Max(toLevel, fromLevel), 1, ExperienceCurve.MaxLevel);

            var start = ExperienceCurve.FractionWithin(fromTotal, fromLevel);
            var end = ExperienceCurve.FractionWithin(toTotal, toLevel);
            bar.SetImmediate(start);

            var levels = Mathf.Max(0, toLevel - fromLevel);
            var covered = levels == 0
                ? Mathf.Max(0.02f, end - start)
                : (1f - start) + (levels - 1) + end;
            var perUnit = Mathf.Clamp(budget / Mathf.Max(0.05f, covered), 0.25f, 2.4f);

            roll.IsRunning = true;
            roll.Step(bar, fromLevel, fromTotal, toTotal, toLevel, perUnit, onRollover, onComplete);
            return roll;
        }

        private void Step(AnimatedBar bar, int level, int fromTotal, int toTotal, int toLevel,
            float perUnit, Action<int> onRollover, Action onComplete)
        {
            if (_cancelled) return;
            if (bar == null) { IsRunning = false; onComplete?.Invoke(); return; }

            var start = ExperienceCurve.FractionWithin(fromTotal, level);

            if (level >= toLevel)
            {
                var end = ExperienceCurve.FractionWithin(toTotal, toLevel);
                var seconds = Mathf.Clamp(Mathf.Abs(end - start) * perUnit, 0.28f, 1.6f);
                bar.SetValue(end, seconds, () =>
                {
                    if (_cancelled) return;
                    IsRunning = false;
                    onComplete?.Invoke();
                });
                return;
            }

            var fill = Mathf.Clamp((1f - start) * perUnit, 0.26f, 1.4f);
            bar.SetValue(1f, fill, () =>
            {
                if (_cancelled) return;
                if (bar == null) { IsRunning = false; onComplete?.Invoke(); return; }

                var next = level + 1;

                // The rollover, in the order that makes it read as a lap rather than a reset:
                // the bar blows out white while it is still full — where the eye already is —
                // the caller ticks its level over, and only after a held beat does the bar
                // snap to empty and start again.
                bar.Flash(BattleSkin.Cyan, 0.34f, 1f);
                onRollover?.Invoke(next);

                UiTween.Delay(0.34f, () =>
                {
                    if (_cancelled) return;
                    if (bar == null) { IsRunning = false; onComplete?.Invoke(); return; }
                    bar.SetImmediate(0f);
                    bar.SetColorImmediate(BattleSkin.Cyan);
                    Step(bar, next, ExperienceCurve.TotalFor(next), toTotal, toLevel,
                        perUnit, onRollover, onComplete);
                });
            });
        }
    }
}
