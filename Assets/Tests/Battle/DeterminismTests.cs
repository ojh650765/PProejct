using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Determinism is a project non-negotiable: the same seed plus the same action
    /// sequence must always produce the same event stream. These tests run whole battles
    /// twice and compare a full textual rendering of every event, field by field.
    /// </summary>
    public sealed class DeterminismTests
    {
        /// <summary>Runs a fixed script and returns the concatenated stream signature.</summary>
        private static string RunScriptedBattle(int seed, BattleKind kind = BattleKind.Wild, int turns = 40)
        {
            var engine = BattleTestBuilder.Engine();

            var lead = BattleTestBuilder.Creature(TestData.Charmander, 18, "ember", "scratch", "growl", "take-down");
            var second = BattleTestBuilder.Creature(TestData.Squirtle, 17, "water-gun", "tackle", "protect");
            var third = BattleTestBuilder.Creature(TestData.Pikachu, 16, "thunder-shock", "quick-attack", "thunder-wave");

            var foeLead = BattleTestBuilder.Creature(TestData.Oddish, 18, "absorb", "sleep-powder", "poison-powder");
            var foeSecond = BattleTestBuilder.Creature(TestData.Geodude, 17, "rock-throw", "tackle", "take-down");

            engine.Begin(kind,
                BattleTestBuilder.Party(lead, second, third),
                BattleTestBuilder.Party(foeLead, foeSecond),
                Weather.Rain, seed);

            var stream = new List<BattleEvent>(engine.DrainPendingEvents());

            // A fixed script that exercises moves, a switch and an item, so the comparison
            // covers more of the engine than a single repeated attack would.
            for (var turn = 0; turn < turns && engine.State.Outcome == BattleOutcome.InProgress; turn++)
            {
                BattleAction action;
                switch (turn % 7)
                {
                    case 3: action = BattleAction.SwitchTo(BattleSide.Player, 1); break;
                    case 5: action = BattleAction.UseItem(BattleSide.Player, "potion"); break;
                    default: action = BattleAction.UseMove(BattleSide.Player, turn % 3); break;
                }

                stream.AddRange(engine.ResolveTurn(action));
            }

            return stream.Signature();
        }

        [Test]
        public void SameSeed_ProducesIdenticalEventStreams()
        {
            var first = RunScriptedBattle(20260815);
            var second = RunScriptedBattle(20260815);

            Assert.That(second, Is.EqualTo(first), "The same seed and script diverged between runs.");
            Assert.That(first.Length, Is.GreaterThan(0), "The battle produced no events at all.");
        }

        [Test]
        public void SameSeed_IsStableAcrossManySeeds()
        {
            for (var seed = 0; seed < 12; seed++)
            {
                var first = RunScriptedBattle(seed);
                var second = RunScriptedBattle(seed);
                Assert.That(second, Is.EqualTo(first), $"Seed {seed} diverged between runs.");
            }
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentStreams()
        {
            var a = RunScriptedBattle(1);
            var b = RunScriptedBattle(2);
            Assert.That(a, Is.Not.EqualTo(b), "Two different seeds produced identical battles.");
        }

        [Test]
        public void TrainerBattles_AreAlsoDeterministic()
        {
            var first = RunScriptedBattle(555, BattleKind.Trainer);
            var second = RunScriptedBattle(555, BattleKind.Trainer);
            Assert.That(second, Is.EqualTo(first));
        }

        /// <summary>
        /// The generator itself: same seed, same sequence; different seeds, different
        /// sequences; and no all-zero degenerate state.
        /// </summary>
        [Test]
        public void Random_IsReproducibleAndWellSpread()
        {
            var a = new BattleRandom(42);
            var b = new BattleRandom(42);
            var c = new BattleRandom(43);

            var differed = false;
            for (var i = 0; i < 1000; i++)
            {
                var left = a.NextUInt64();
                Assert.That(b.NextUInt64(), Is.EqualTo(left));
                if (c.NextUInt64() != left) differed = true;
            }

            Assert.That(differed, Is.True, "Adjacent seeds produced the same stream.");
        }

        /// <summary>Range draws stay inside their bounds and cover them.</summary>
        [Test]
        public void Random_RespectsItsBounds()
        {
            var rng = new BattleRandom(9);
            var sawMin = false;
            var sawMax = false;

            for (var i = 0; i < 5000; i++)
            {
                var value = rng.Range(85, 100);
                Assert.That(value, Is.InRange(85, 100));
                if (value == 85) sawMin = true;
                if (value == 100) sawMax = true;
            }

            Assert.That(sawMin && sawMax, Is.True, "The inclusive bounds were never produced.");
        }

        /// <summary>A percentage chance lands near its nominal rate.</summary>
        [Test]
        public void Random_ChanceIsUnbiased()
        {
            var rng = new BattleRandom(1234);
            var hits = 0;
            for (var i = 0; i < 20000; i++) if (rng.Chance(30)) hits++;
            Assert.That(hits / 20000f, Is.EqualTo(0.30f).Within(0.02f));
        }

        /// <summary>A fork is deterministic and independent of the parent's draw position.</summary>
        [Test]
        public void Random_ForkIsDeterministicAndIndependent()
        {
            var parent = new BattleRandom(77);
            var forkA = parent.Fork(3);

            for (var i = 0; i < 50; i++) parent.NextUInt64();
            var forkB = parent.Fork(3);

            for (var i = 0; i < 100; i++)
                Assert.That(forkB.NextUInt64(), Is.EqualTo(forkA.NextUInt64()),
                    "A fork must depend only on the seed and salt, not on how far the parent has run.");
        }

        /// <summary>
        /// Instance ids must be derived, never random: experience events carry them, and a
        /// GUID would make the whole stream unreproducible. The factory uses the shared
        /// <see cref="CreatureIds.Derive"/> scheme so every creature in the game agrees.
        /// </summary>
        [Test]
        public void CreatureFactory_ProducesDeterministicInstanceIds()
        {
            var species = TestData.Species();
            var moves = TestData.Moves();

            var a = CreatureFactory.Create(TestData.Pikachu, 12, 555, species, moves);
            var b = CreatureFactory.Create(TestData.Pikachu, 12, 555, species, moves);
            var c = CreatureFactory.Create(TestData.Pikachu, 12, 556, species, moves);

            Assert.That(b.InstanceId, Is.EqualTo(a.InstanceId));
            Assert.That(c.InstanceId, Is.Not.EqualTo(a.InstanceId));
            Assert.That(a.InstanceId, Is.EqualTo(CreatureIds.Derive(TestData.Pikachu, 12, 555, 0)),
                "The factory must use the shared id scheme, not one of its own.");
            Assert.That(a.InstanceId, Does.Not.Contain("-"), "A GUID id leaked through.");

            for (var i = 0; i < StatKinds.BaseCount; i++)
                Assert.That(b.Ivs[i], Is.EqualTo(a.Ivs[i]));
        }

        /// <summary>
        /// Two same-species, same-level creatures built from one seed must still be
        /// distinguishable — the presentation layers key their views on the instance id.
        /// </summary>
        [Test]
        public void CreatureFactory_OrdinalSeparatesCreaturesFromOneSeed()
        {
            var species = TestData.Species();
            var moves = TestData.Moves();

            var first = CreatureFactory.Create(TestData.Geodude, 12, 999, species, moves, ordinal: 0);
            var second = CreatureFactory.Create(TestData.Geodude, 12, 999, species, moves, ordinal: 1);

            Assert.That(second.InstanceId, Is.Not.EqualTo(first.InstanceId));
            Assert.That(CreatureFactory.Create(TestData.Geodude, 12, 999, species, moves, ordinal: 1).InstanceId,
                Is.EqualTo(second.InstanceId), "The same ordinal must reproduce the same id.");
        }
    }
}
