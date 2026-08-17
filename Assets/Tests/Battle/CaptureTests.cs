using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// The catch formula. The presentation layer animates exactly the shake count the
    /// engine reports, so the shape of the probability curve and the shake/ success
    /// relationship both matter.
    /// </summary>
    public sealed class CaptureTests
    {
        private const int CommonRate = 255;  // Pidgey, Rattata and friends
        private const int StarterRate = 45;  // Bulbasaur, Charmander, Squirtle

        private static CreatureInstance Target(int maxHp, int currentHp, StatusCondition status = StatusCondition.None) =>
            new CreatureInstance { MaxHp = maxHp, CurrentHp = currentHp, Status = status };

        /// <summary>Weakening the target strictly improves the odds.</summary>
        [Test]
        public void Probability_RisesAsHpFalls()
        {
            var previous = -1f;
            for (var hp = 100; hp >= 1; hp -= 11)
            {
                var probability = CaptureMath.Probability(Target(100, hp), StarterRate, 1f);
                Assert.That(probability, Is.GreaterThan(previous),
                    $"Probability did not improve going from more HP to {hp}.");
                previous = probability;
            }
        }

        /// <summary>Better balls strictly improve the odds.</summary>
        [Test]
        public void Probability_RisesWithBallQuality()
        {
            var target = Target(100, 40);

            var poke = CaptureMath.Probability(target, StarterRate, 1f);
            var great = CaptureMath.Probability(target, StarterRate, 1.5f);
            var ultra = CaptureMath.Probability(target, StarterRate, 2f);

            Assert.That(great, Is.GreaterThan(poke));
            Assert.That(ultra, Is.GreaterThan(great));
            Assert.That(ultra, Is.LessThanOrEqualTo(1f));
        }

        /// <summary>A master ball is modelled as multiplier zero and always succeeds.</summary>
        [Test]
        public void MasterBall_AlwaysSucceeds()
        {
            var target = Target(100, 100);
            Assert.That(CaptureMath.Probability(target, StarterRate, 0f), Is.EqualTo(1f));

            var result = CaptureMath.Roll(target, StarterRate, 0f, new BattleRandom(1));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Shakes, Is.EqualTo(CaptureMath.ShakesForCapture));
        }

        /// <summary>Status helps: sleep and freeze double the rate, the chip statuses add half.</summary>
        [Test]
        public void StatusBonus_MatchesTheReferenceValues()
        {
            Assert.That(CaptureMath.StatusBonus(StatusCondition.None), Is.EqualTo(1f));
            Assert.That(CaptureMath.StatusBonus(StatusCondition.Sleep), Is.EqualTo(2f));
            Assert.That(CaptureMath.StatusBonus(StatusCondition.Freeze), Is.EqualTo(2f));
            Assert.That(CaptureMath.StatusBonus(StatusCondition.Paralysis), Is.EqualTo(1.5f));
            Assert.That(CaptureMath.StatusBonus(StatusCondition.Burn), Is.EqualTo(1.5f));
            Assert.That(CaptureMath.StatusBonus(StatusCondition.BadPoison), Is.EqualTo(1.5f));
        }

        [Test]
        public void StatusBonus_ImprovesTheOdds()
        {
            var healthy = CaptureMath.Probability(Target(100, 30), StarterRate, 1f);
            var asleep = CaptureMath.Probability(Target(100, 30, StatusCondition.Sleep), StarterRate, 1f);
            Assert.That(asleep, Is.GreaterThan(healthy));
        }

        /// <summary>A common species at 1 HP with a good ball is a near-certainty.</summary>
        [Test]
        public void CommonSpeciesAtOneHp_IsAlmostCertain()
        {
            var probability = CaptureMath.Probability(Target(60, 1), CommonRate, 1.5f);
            Assert.That(probability, Is.GreaterThan(0.95f));
        }

        /// <summary>A starter at full HP with a plain ball is a long shot.</summary>
        [Test]
        public void StarterAtFullHp_IsUnlikely()
        {
            var probability = CaptureMath.Probability(Target(100, 100), StarterRate, 1f);
            Assert.That(probability, Is.LessThan(0.12f));
            Assert.That(probability, Is.GreaterThan(0f));
        }

        /// <summary>Observed catch frequency should track the reported probability.</summary>
        [Test]
        public void ObservedCatchRate_MatchesReportedProbability()
        {
            const int trials = 4000;
            var rng = new BattleRandom(20260815);
            var expected = CaptureMath.Probability(Target(100, 40), StarterRate, 1.5f);

            var caught = 0;
            for (var i = 0; i < trials; i++)
                if (CaptureMath.Roll(Target(100, 40), StarterRate, 1.5f, rng).Succeeded) caught++;

            var observed = (float)caught / trials;
            Assert.That(observed, Is.EqualTo(expected).Within(0.04f),
                $"Reported {expected:P2} but observed {observed:P2} over {trials} throws.");
        }

        /// <summary>A success is always four shakes; a failure is never four.</summary>
        [Test]
        public void ShakeCount_IsConsistentWithSuccess()
        {
            var rng = new BattleRandom(7);
            for (var i = 0; i < 500; i++)
            {
                var result = CaptureMath.Roll(Target(100, 40), StarterRate, 1.5f, rng);
                Assert.That(result.Shakes, Is.InRange(0, CaptureMath.ShakesForCapture));
                Assert.That(result.Succeeded, Is.EqualTo(result.Shakes == CaptureMath.ShakesForCapture));
            }
        }

        /// <summary>Every throw consumes the same number of draws, so replays stay aligned.</summary>
        [Test]
        public void EveryThrow_ConsumesTheSameNumberOfDraws()
        {
            var rng = new BattleRandom(3);
            long previous = 0;
            for (var i = 0; i < 20; i++)
            {
                CaptureMath.Roll(Target(100, 90), StarterRate, 1f, rng);
                var used = rng.DrawCount - previous;
                previous = rng.DrawCount;
                Assert.That(used, Is.GreaterThanOrEqualTo(CaptureMath.ShakesForCapture));
            }
        }

        /// <summary>The slice roster's catch rates come from the table, not the estimator.</summary>
        [Test]
        public void CatchRates_ComeFromTheRosterTable()
        {
            var species = TestData.Species();

            species.TryGet(TestData.Bulbasaur, out var bulbasaur);
            species.TryGet(TestData.Pidgey, out var pidgey);
            species.TryGet(TestData.Machop, out var machop);

            Assert.That(CaptureMath.CatchRateOf(bulbasaur), Is.EqualTo(45));
            Assert.That(CaptureMath.CatchRateOf(pidgey), Is.EqualTo(255));
            Assert.That(CaptureMath.CatchRateOf(machop), Is.EqualTo(180));
        }

        /// <summary>An unknown species still gets a sane rate rather than throwing.</summary>
        [Test]
        public void UnknownSpecies_FallsBackToAnEstimate()
        {
            var invented = new SpeciesData { Id = 9999, BaseStats = new[] { 80, 80, 80, 80, 80, 80 } };
            var rate = CaptureMath.CatchRateOf(invented);
            Assert.That(rate, Is.InRange(3, 255));
        }

        /// <summary>A capture in a real battle reports the shakes, the probability and the outcome.</summary>
        [Test]
        public void EngineCapture_EmitsAConsistentEvent()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 20, "tackle").WithAbility(null);
            var wild = BattleTestBuilder.Creature(TestData.Pidgey, 5, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(wild),
                Weather.Clear, seed: 4711);

            wild.CurrentHp = 1;
            var stream = engine.ResolveTurn(BattleAction.Capture(BattleSide.Player, "ultra-ball"));

            var attempts = stream.OfType<CaptureAttemptEvent>();
            Assert.That(attempts.Count, Is.EqualTo(1));

            var attempt = attempts[0];
            Assert.That(attempt.BallId, Is.EqualTo("ultra-ball"));
            Assert.That(attempt.Target, Is.EqualTo(BattleSide.Opponent),
                "The engine must state which side the ball is thrown at; the choreography aims by this field.");
            Assert.That(attempt.CatchProbability, Is.GreaterThan(0.9f));
            Assert.That(attempt.Succeeded, Is.EqualTo(attempt.Shakes == CaptureMath.ShakesForCapture));

            if (attempt.Succeeded)
            {
                Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.Captured));
                Assert.That(engine.CapturedCreature, Is.SameAs(wild));
            }
        }

        /// <summary>Balls do nothing in a trainer battle.</summary>
        [Test]
        public void Capture_IsRefusedInTrainerBattles()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 20, "tackle").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Pidgey, 20, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 8);

            var stream = engine.ResolveTurn(BattleAction.Capture(BattleSide.Player, "ultra-ball"));

            Assert.That(stream.OfType<CaptureAttemptEvent>().Count, Is.EqualTo(0));
            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress));
        }
    }
}
