using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Turn order: priority beats Speed, Speed beats nothing, and an exact tie is settled
    /// by the seeded RNG rather than by whichever side happens to be first in an array.
    /// </summary>
    public sealed class TurnOrderTests
    {
        /// <summary>Which side declared a move first in this turn's stream.</summary>
        private static BattleSide? FirstMover(IReadOnlyList<BattleEvent> stream)
        {
            for (var i = 0; i < stream.Count; i++)
                if (stream[i] is MoveDeclaredEvent declared) return declared.Side;
            return null;
        }

        private static BattleEngine Fight(int seed, int playerSpecies, string playerMove,
            int opponentSpecies, string opponentMove, out CreatureInstance player, out CreatureInstance opponent,
            int level = 50, Weather weather = Weather.Clear)
        {
            var engine = BattleTestBuilder.Engine();
            player = BattleTestBuilder.Creature(playerSpecies, level, playerMove).WithAbility(null);
            opponent = BattleTestBuilder.Creature(opponentSpecies, level, opponentMove).WithAbility(null);
            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(opponent),
                weather, seed);
            return engine;
        }

        /// <summary>Pikachu outruns Rattata, so Pikachu moves first.</summary>
        [Test]
        public void FasterCreature_MovesFirst()
        {
            var engine = Fight(11, TestData.Pikachu, "thunder-shock", TestData.Rattata, "tackle", out _, out _);
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(FirstMover(stream), Is.EqualTo(BattleSide.Player));
        }

        /// <summary>Priority overrides Speed: Quick Attack from the slower side still lands first.</summary>
        [Test]
        public void Priority_OverridesSpeed()
        {
            var engine = Fight(12, TestData.Rattata, "quick-attack", TestData.Pikachu, "thunder-shock",
                out var player, out var opponent);

            Assert.That(player.Stats[(int)StatKind.Speed], Is.LessThan(opponent.Stats[(int)StatKind.Speed]),
                "The fixture must have the priority user genuinely slower for this to prove anything.");

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(FirstMover(stream), Is.EqualTo(BattleSide.Player));
        }

        /// <summary>Paralysis halves Speed, which is enough to hand the turn to a slower creature.</summary>
        [Test]
        public void Paralysis_LosesTheSpeedRace()
        {
            // Growl on both sides: nobody faints, so the test can keep sampling turns.
            var engine = Fight(13, TestData.Pikachu, "growl", TestData.Rattata, "growl",
                out var player, out _);

            // Unparalysed, Pikachu leads.
            var clean = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(FirstMover(clean), Is.EqualTo(BattleSide.Player));

            player.Status = StatusCondition.Paralysis;

            // Paralysis can also stop the move outright, so look at the first turn that
            // actually produced two declarations.
            BattleSide? mover = null;
            for (var turn = 0; turn < 12 && mover == null; turn++)
            {
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                if (stream.OfType<MoveDeclaredEvent>().Count == 2) mover = FirstMover(stream);
            }

            Assert.That(mover, Is.EqualTo(BattleSide.Opponent),
                "A paralysed Pikachu at half Speed is slower than a healthy Rattata.");
        }

        /// <summary>A switch always resolves before a move, so the incoming creature eats the hit.</summary>
        [Test]
        public void Switching_ResolvesBeforeMoves()
        {
            var engine = BattleTestBuilder.Engine();
            var lead = BattleTestBuilder.Creature(TestData.Squirtle, 50, "tackle").WithAbility(null);
            var bench = BattleTestBuilder.Creature(TestData.Geodude, 50, "tackle").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Pikachu, 50, "thunder-shock").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(lead, bench),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 500);

            // Drain the intro so the two opening send-outs do not land in the turn stream.
            engine.DrainPendingEvents();
            var stream = engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));

            var sentOut = stream.OfType<CreatureSentOutEvent>();
            var declared = stream.OfType<MoveDeclaredEvent>();

            Assert.That(sentOut.Count, Is.EqualTo(1));
            Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(bench));

            // The Electric attack that followed the switch hit Geodude, which is immune.
            Assert.That(declared.Count, Is.EqualTo(1), "Only the opponent should have moved.");
            Assert.That(bench.CurrentHp, Is.EqualTo(bench.MaxHp), "Electric does nothing to a Ground type.");
        }

        /// <summary>Switching wipes stat stages and volatiles.</summary>
        [Test]
        public void Switching_ResetsStagesAndVolatiles()
        {
            var engine = BattleTestBuilder.Engine();
            var lead = BattleTestBuilder.Creature(TestData.Machop, 50, "swords-dance").WithAbility(null);
            var bench = BattleTestBuilder.Creature(TestData.Squirtle, 50, "tackle").WithAbility(null);
            // Protect on the other side, because a Growl would legitimately re-apply to the
            // incoming creature after the switch and mask the reset this test is checking.
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 50, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(lead, bench),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 909);

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(engine.State.StatStagesOf(BattleSide.Player)[(int)StatKind.Attack], Is.GreaterThan(0));

            engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));

            var stages = engine.State.StatStagesOf(BattleSide.Player);
            for (var i = 0; i < stages.Count; i++)
                Assert.That(stages[i], Is.EqualTo(0), $"Stage {(StatKind)i} survived a switch.");
            Assert.That(engine.State.VolatilesOf(BattleSide.Player), Is.EqualTo(VolatileFlags.None));
        }

        /// <summary>
        /// An exact Speed tie is decided by the RNG: the same seed always produces the same
        /// winner, and across seeds both sides win sometimes.
        /// </summary>
        [Test]
        public void SpeedTie_IsBrokenBySeededRandom()
        {
            var playerFirstCount = 0;
            var results = new List<BattleSide?>();

            for (var seed = 0; seed < 40; seed++)
            {
                var engine = Fight(seed, TestData.Machop, "tackle", TestData.Machop, "tackle", out var p, out var o);
                Assert.That(p.Stats[(int)StatKind.Speed], Is.EqualTo(o.Stats[(int)StatKind.Speed]));

                var mover = FirstMover(engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0)));
                results.Add(mover);
                if (mover == BattleSide.Player) playerFirstCount++;
            }

            Assert.That(playerFirstCount, Is.GreaterThan(4), "The tie-break must not always favour one side.");
            Assert.That(playerFirstCount, Is.LessThan(36), "The tie-break must not always favour one side.");

            // Re-running the same seeds must reproduce exactly the same winners.
            for (var seed = 0; seed < 40; seed++)
            {
                var engine = Fight(seed, TestData.Machop, "tackle", TestData.Machop, "tackle", out _, out _);
                var mover = FirstMover(engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0)));
                Assert.That(mover, Is.EqualTo(results[seed]), $"Seed {seed} broke the tie differently on replay.");
            }
        }

        /// <summary>Swift Swim doubles Speed in rain; Chlorophyll does the same in sun.</summary>
        [Test]
        public void WeatherAbilities_DoubleSpeed()
        {
            var clear = Fight(21, TestData.Poliwag, "water-gun", TestData.Machop, "tackle", out var dry, out _);
            dry.AbilityId = "swift-swim";
            var drySpeed = clear.EffectiveSpeed(BattleSide.Player);

            var rain = Fight(21, TestData.Poliwag, "water-gun", TestData.Machop, "tackle", out var wet, out _,
                weather: Weather.Rain);
            wet.AbilityId = "swift-swim";
            var wetSpeed = rain.EffectiveSpeed(BattleSide.Player);

            Assert.That(wetSpeed, Is.EqualTo(drySpeed * 2f).Within(0.001f));
        }
    }
}
