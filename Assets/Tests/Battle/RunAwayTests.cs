using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// The wild-battle escape formula. The Gen-1 rule: F = speed * 128 / blockerSpeed +
    /// 30 per prior attempt, guaranteed escape at F >= 256, a 0-255 roll below that.
    ///
    /// These tests exist because the formula once shipped with `% 256` where the cap
    /// belonged, which inverted it at its best — a runner twice as fast as its blocker
    /// wrapped from 286 down to 30 out of 256 and was cornered more often than an
    /// equal-speed one. The fast-runner cases below pin the cap; the equal-speed case
    /// pins the rolled branch the wrap bug never touched.
    /// </summary>
    public sealed class RunAwayTests
    {
        /// <summary>
        /// A wild battle with hand-set speed stats, so the escape factor is exact rather
        /// than whatever the stat formula derives. Neither side gets an ability, because
        /// Run Away would bypass the formula under test.
        /// </summary>
        private static BattleEngine WildBattle(int seed, int ownSpeed, int foeSpeed)
        {
            var engine = BattleTestBuilder.Engine();
            // Generous HP so a string of failed attempts cannot end in a knockout before
            // the assertion under test gets its chance.
            var player = BattleTestBuilder.Creature(TestData.Rattata, 20, "tackle")
                .WithAbility(null).WithStats(400, 40, 35, 25, 35, ownSpeed);
            var foe = BattleTestBuilder.Creature(TestData.Pidgey, 20, "tackle")
                .WithAbility(null).WithStats(60, 40, 35, 25, 35, foeSpeed);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed);
            return engine;
        }

        /// <summary>
        /// Equal speed on the first attempt gives F = 128 + 30 = 158, so escape should
        /// land near 158/256 ≈ 62% — the bucket the wrap bug never distorted, asserted
        /// here so the fix cannot have moved it.
        /// </summary>
        [Test]
        public void EqualSpeed_EscapesAtTheFormulaRate()
        {
            const int trials = 300;
            const float expected = 158f / 256f;

            var escapes = 0;
            for (var seed = 0; seed < trials; seed++)
            {
                var engine = WildBattle(seed, 100, 100);
                engine.ResolveTurn(BattleAction.Run(BattleSide.Player));
                if (engine.State.Outcome == BattleOutcome.Fled) escapes++;
            }

            var observed = (float)escapes / trials;
            Assert.That(observed, Is.EqualTo(expected).Within(0.07f),
                $"Equal speed should escape near {expected:P1} but escaped {observed:P1}.");
        }

        /// <summary>
        /// Twice the blocker's speed makes F = 256 before the attempt bonus: escape must
        /// be unconditional. Under the old `% 256` this was the WORST case, not the best.
        /// </summary>
        [Test]
        public void TwiceTheSpeed_AlwaysEscapes()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var engine = WildBattle(seed, 100, 50);
                engine.ResolveTurn(BattleAction.Run(BattleSide.Player));
                Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.Fled),
                    $"Seed {seed}: a runner at double speed must always get away.");
            }
        }

        /// <summary>
        /// The guaranteed branch consumes NO draw, by documented choice: the battle ends
        /// on the spot, so there is no later roll whose alignment a burnt draw would
        /// protect (the reason CaptureMath makes the opposite choice for a guaranteed
        /// ball). The rolled branch must still draw. This pins both, so the draw policy
        /// cannot drift silently.
        /// </summary>
        [Test]
        public void GuaranteedEscape_ConsumesNoDraw_ButARolledEscapeDoes()
        {
            var guaranteed = WildBattle(7, 100, 50);
            var before = guaranteed.Random.DrawCount;
            guaranteed.ResolveTurn(BattleAction.Run(BattleSide.Player));
            Assert.That(guaranteed.State.Outcome, Is.EqualTo(BattleOutcome.Fled));
            Assert.That(guaranteed.Random.DrawCount, Is.EqualTo(before),
                "A guaranteed escape is decidable without randomness and must not draw.");

            var rolled = WildBattle(7, 100, 100);
            var rolledBefore = rolled.Random.DrawCount;
            rolled.ResolveTurn(BattleAction.Run(BattleSide.Player));
            Assert.That(rolled.Random.DrawCount, Is.GreaterThan(rolledBefore),
                "A non-guaranteed escape must be settled by the battle RNG.");
        }

        /// <summary>Same seed, same attempt, same number of draws — the determinism yardstick.</summary>
        [Test]
        public void RunAttempt_IsDeterministicInOutcomeAndDrawCount()
        {
            foreach (var speeds in new[] { new[] { 100, 100 }, new[] { 100, 50 }, new[] { 60, 90 } })
            {
                var first = WildBattle(31337, speeds[0], speeds[1]);
                var streamA = first.ResolveTurn(BattleAction.Run(BattleSide.Player));

                var second = WildBattle(31337, speeds[0], speeds[1]);
                var streamB = second.ResolveTurn(BattleAction.Run(BattleSide.Player));

                Assert.That(streamB.Signature(), Is.EqualTo(streamA.Signature()),
                    $"Speeds {speeds[0]}/{speeds[1]} diverged between identical runs.");
                Assert.That(second.Random.DrawCount, Is.EqualTo(first.Random.DrawCount),
                    $"Speeds {speeds[0]}/{speeds[1]} consumed a different number of draws.");
            }
        }

        /// <summary>
        /// Attempts accumulate: even a slower runner is guaranteed out once 30 per attempt
        /// pushes F past 255, instead of wrapping back to hopeless odds.
        /// </summary>
        [Test]
        public void RepeatedAttempts_EventuallyGuaranteeEscape()
        {
            // ownSpeed 50 vs 100 gives F = 64 + 30n: attempt 7 reaches 274 >= 256.
            var engine = WildBattle(5, 50, 100);

            for (var attempt = 0; attempt < 7 && engine.State.Outcome == BattleOutcome.InProgress; attempt++)
                engine.ResolveTurn(BattleAction.Run(BattleSide.Player));

            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.Fled),
                "By the seventh attempt the formula guarantees the escape.");
        }
    }
}
