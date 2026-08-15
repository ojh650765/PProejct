using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>Stat stage arithmetic, clamping, and the accuracy/evasion curve.</summary>
    public sealed class StatStageTests
    {
        [Test]
        public void StageMultipliers_FollowTheStandardCurve()
        {
            Assert.That(StatMath.StageMultiplier(0), Is.EqualTo(1f));
            Assert.That(StatMath.StageMultiplier(1), Is.EqualTo(1.5f));
            Assert.That(StatMath.StageMultiplier(2), Is.EqualTo(2f));
            Assert.That(StatMath.StageMultiplier(6), Is.EqualTo(4f));
            Assert.That(StatMath.StageMultiplier(-1), Is.EqualTo(2f / 3f).Within(1e-5f));
            Assert.That(StatMath.StageMultiplier(-2), Is.EqualTo(0.5f));
            Assert.That(StatMath.StageMultiplier(-6), Is.EqualTo(0.25f));
        }

        [Test]
        public void AccuracyStageMultipliers_UseTheShallowerCurve()
        {
            Assert.That(StatMath.AccuracyStageMultiplier(0), Is.EqualTo(1f));
            Assert.That(StatMath.AccuracyStageMultiplier(1), Is.EqualTo(4f / 3f).Within(1e-5f));
            Assert.That(StatMath.AccuracyStageMultiplier(6), Is.EqualTo(3f));
            Assert.That(StatMath.AccuracyStageMultiplier(-1), Is.EqualTo(0.75f));
            Assert.That(StatMath.AccuracyStageMultiplier(-6), Is.EqualTo(1f / 3f).Within(1e-5f));
        }

        [Test]
        public void Stages_SaturateAtPlusAndMinusSix()
        {
            Assert.That(StatMath.Clamp(9), Is.EqualTo(6));
            Assert.That(StatMath.Clamp(-9), Is.EqualTo(-6));
            Assert.That(StatMath.StageMultiplier(99), Is.EqualTo(StatMath.StageMultiplier(6)));
            Assert.That(StatMath.StageMultiplier(-99), Is.EqualTo(StatMath.StageMultiplier(-6)));
        }

        /// <summary>The engine clamps in battle too, and refuses the change once saturated.</summary>
        [Test]
        public void Engine_ClampsStagesAtSixAndReportsRefusal()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "swords-dance").WithAbility(null);
            var opponent = BattleTestBuilder.Creature(TestData.Rattata, 30, "growl").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                Weather.Clear, seed: 1);

            for (var i = 0; i < 10; i++)
                Assert.That(engine.TryChangeStage(BattleSide.Player, StatKind.Attack, 1, "test"),
                    Is.EqualTo(i < 6), $"Change {i} should {(i < 6 ? "succeed" : "be refused")}.");

            Assert.That(engine.State.StatStagesOf(BattleSide.Player)[(int)StatKind.Attack], Is.EqualTo(6));

            for (var i = 0; i < 20; i++) engine.TryChangeStage(BattleSide.Player, StatKind.Attack, -1, "test");
            Assert.That(engine.State.StatStagesOf(BattleSide.Player)[(int)StatKind.Attack], Is.EqualTo(-6));
        }

        /// <summary>A stage change is announced with its delta and the resulting stage.</summary>
        [Test]
        public void Engine_EmitsStageChangeEvents()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "swords-dance").WithAbility(null);
            // Tail Whip drops Defence, so the opponent's change cannot be confused with the
            // Attack boost this test is actually about.
            var opponent = BattleTestBuilder.Creature(TestData.Rattata, 30, "tail-whip").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                Weather.Clear, seed: 2);

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            var changes = stream.OfType<StatStageChangedEvent>();

            var boost = changes.Find(c => c.Target == BattleSide.Player && c.Stat == StatKind.Attack);
            Assert.That(boost, Is.Not.Null, "Swords Dance must announce its own boost.");
            Assert.That(boost.Delta, Is.EqualTo(2));
            Assert.That(boost.NewStage, Is.EqualTo(2));

            var drop = changes.Find(c => c.Target == BattleSide.Player && c.Stat == StatKind.Defense);
            Assert.That(drop, Is.Not.Null, "Tail Whip from the opponent must announce the Defence drop.");
            Assert.That(drop.Delta, Is.EqualTo(-1));
        }

        /// <summary>A +2 Attack stage doubles the damage the move deals.</summary>
        [Test]
        public void AttackBoost_ScalesDamage()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 50, "tackle").WithAbility(null);
            var opponent = BattleTestBuilder.Creature(TestData.Geodude, 50, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                Weather.Clear, seed: 3);

            var flat = engine.ForecastMove(BattleSide.Player, 0);
            engine.TryChangeStage(BattleSide.Player, StatKind.Attack, 2, "test");
            var boosted = engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(boosted.ExpectedFraction, Is.GreaterThan(flat.ExpectedFraction * 1.8f));
            Assert.That(boosted.ExpectedFraction, Is.LessThan(flat.ExpectedFraction * 2.2f));
        }

        /// <summary>Keen Eye refuses an accuracy drop and says so.</summary>
        [Test]
        public void KeenEye_BlocksAccuracyDrops()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Geodude, 30, "sand-attack").WithAbility(null);
            var opponent = BattleTestBuilder.Creature(TestData.Pidgey, 30, "tackle").WithAbility("keen-eye");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                Weather.Clear, seed: 4);

            Assert.That(engine.TryChangeStage(BattleSide.Opponent, StatKind.Accuracy, -1, "test"), Is.False);
            Assert.That(engine.State.StatStagesOf(BattleSide.Opponent)[(int)StatKind.Accuracy], Is.EqualTo(0));

            // The same ability does not object to a boost.
            Assert.That(engine.TryChangeStage(BattleSide.Opponent, StatKind.Accuracy, 1, "test"), Is.True);
        }

        /// <summary>Evasion raises the odds of being missed; accuracy stages counteract it.</summary>
        [Test]
        public void AccuracyAndEvasion_MoveTheHitChance()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Geodude, 30, "rock-throw").WithAbility(null);
            var opponent = BattleTestBuilder.Creature(TestData.Pidgey, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                Weather.Clear, seed: 5);

            BattleTestBuilder.MoveRegistry.TryGet("rock-throw", out var rockThrow);

            var baseline = engine.HitChance(BattleSide.Player, BattleSide.Opponent, rockThrow);
            Assert.That(baseline, Is.EqualTo(0.9f).Within(1e-4f));

            engine.TryChangeStage(BattleSide.Opponent, StatKind.Evasion, 2, "test");
            var evasive = engine.HitChance(BattleSide.Player, BattleSide.Opponent, rockThrow);
            Assert.That(evasive, Is.EqualTo(0.9f * 3f / 5f).Within(1e-4f));

            engine.TryChangeStage(BattleSide.Player, StatKind.Accuracy, 2, "test");
            var corrected = engine.HitChance(BattleSide.Player, BattleSide.Opponent, rockThrow);
            Assert.That(corrected, Is.EqualTo(0.9f).Within(1e-4f));
        }

        /// <summary>An accuracy of zero in the data means the move never misses.</summary>
        [Test]
        public void ZeroAccuracy_NeverMisses()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "swords-dance").WithAbility(null);
            var opponent = BattleTestBuilder.Creature(TestData.Pidgey, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                Weather.Clear, seed: 6);

            BattleTestBuilder.MoveRegistry.TryGet("swords-dance", out var swordsDance);
            engine.TryChangeStage(BattleSide.Opponent, StatKind.Evasion, 6, "test");

            Assert.That(engine.HitChance(BattleSide.Player, BattleSide.Opponent, swordsDance), Is.EqualTo(1f));
        }
    }
}
