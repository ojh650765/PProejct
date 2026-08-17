using System;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// The opponent policy. These check that it plays sensibly rather than randomly: it
    /// takes an available knockout, prefers its best type matchup, and pivots out of a
    /// losing position instead of standing there.
    /// </summary>
    public sealed class BattleAiTests
    {
        /// <summary>Offered a lethal move and a weak one, the AI takes the kill.</summary>
        [Test]
        public void Ai_TakesAnAvailableKnockout()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Ace));

            var player = BattleTestBuilder.Creature(TestData.Charmander, 20, "growl").WithAbility(null);
            // Water Gun kills the Fire type outright; Growl would do nothing at all.
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 20, "growl", "water-gun").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 4001);

            engine.DrainPendingEvents();
            player.CurrentHp = 3;

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            var declared = stream.OfType<MoveDeclaredEvent>().Find(d => d.Side == BattleSide.Opponent);

            Assert.That(declared, Is.Not.Null);
            Assert.That(declared.MoveId, Is.EqualTo("water-gun"), "The AI passed up a guaranteed knockout.");
        }

        /// <summary>Offered two attacks, the AI picks the one with the better type matchup.</summary>
        [Test]
        public void Ai_PrefersTheBetterTypeMatchup()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Ace));

            var player = BattleTestBuilder.Creature(TestData.Charmander, 30, "growl").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Poliwag, 30, "tackle", "water-gun").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 4002);

            engine.DrainPendingEvents();
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            var declared = stream.OfType<MoveDeclaredEvent>().Find(d => d.Side == BattleSide.Opponent);

            Assert.That(declared.MoveId, Is.EqualTo("water-gun"), "Water beats Normal into a Fire type.");
        }

        /// <summary>Facing a knockout with a resistant creature on the bench, the AI pivots.</summary>
        [Test]
        public void Ai_SwitchesOutOfALosingPosition()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Standard));

            var player = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
            var doomed = BattleTestBuilder.Creature(TestData.Charmander, 30, "ember").WithAbility(null);
            var resistant = BattleTestBuilder.Creature(TestData.Poliwag, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(doomed, resistant),
                Weather.Clear, seed: 4003);

            engine.DrainPendingEvents();
            doomed.CurrentHp = Math.Max(1, doomed.MaxHp / 8);

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(stream.OfType<CreatureWithdrawnEvent>().Exists(e => e.Side == BattleSide.Opponent), Is.True,
                "A Charmander one hit from death should have made way for the Water type.");
            Assert.That(engine.State.ActiveOf(BattleSide.Opponent), Is.SameAs(resistant));
        }

        /// <summary>A healthy creature with a good attack stays in.</summary>
        [Test]
        public void Ai_DoesNotSwitchFromAWinningPosition()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Standard));

            var player = BattleTestBuilder.Creature(TestData.Charmander, 30, "growl").WithAbility(null);
            var winner = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
            var bench = BattleTestBuilder.Creature(TestData.Poliwag, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(winner, bench),
                Weather.Clear, seed: 4004);

            engine.DrainPendingEvents();

            for (var turn = 0; turn < 3; turn++)
            {
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                Assert.That(stream.OfType<CreatureWithdrawnEvent>().Count, Is.EqualTo(0),
                    "A healthy Squirtle beating a Charmander has no reason to leave.");
            }
        }

        /// <summary>The wild policy never switches, however badly it is doing.</summary>
        [Test]
        public void WildPolicy_NeverSwitches()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));

            var player = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
            var doomed = BattleTestBuilder.Creature(TestData.Charmander, 30, "ember").WithAbility(null);
            var resistant = BattleTestBuilder.Creature(TestData.Poliwag, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(doomed, resistant),
                Weather.Clear, seed: 4005);

            engine.DrainPendingEvents();
            doomed.CurrentHp = Math.Max(1, doomed.MaxHp / 8);

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(stream.OfType<CreatureWithdrawnEvent>().Count, Is.EqualTo(0));
        }

        /// <summary>The AI's replacement choice favours the best matchup, not just slot order.</summary>
        [Test]
        public void Ai_ChoosesAFavourableReplacement()
        {
            // Rookie, because a switching policy would pull the doomed creature out before
            // it faints — and it is the faint-replacement choice this test is about.
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Rookie));

            var player = BattleTestBuilder.Creature(TestData.Pikachu, 40, "thunder-shock").WithAbility(null);
            var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
            // Poliwag is Water and would be electrocuted; Geodude is Ground and immune.
            var badChoice = BattleTestBuilder.Creature(TestData.Poliwag, 30, "water-gun").WithAbility(null);
            var goodChoice = BattleTestBuilder.Creature(TestData.Geodude, 30, "rock-throw").WithAbility(null);

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(doomed, badChoice, goodChoice),
                Weather.Clear, seed: 4006);

            engine.DrainPendingEvents();
            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(doomed.IsFainted, Is.True, "The level 5 Zubat should have been knocked out.");
            Assert.That(engine.State.ActiveOf(BattleSide.Opponent), Is.SameAs(goodChoice),
                "The AI should send out the Ground type that is immune to Electric.");
        }

        /// <summary>Escape odds work without Run Away, given a fast enough runner.</summary>
        [Test]
        public void Running_SucceedsAgainstASlowOpponentWithoutRunAway()
        {
            var escapes = 0;

            for (var seed = 0; seed < 20; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                var player = BattleTestBuilder.Creature(TestData.Pikachu, 30, "growl").WithAbility(null);
                var foe = BattleTestBuilder.Creature(TestData.Geodude, 30, "growl").WithAbility(null);

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(player),
                    BattleTestBuilder.Party(foe),
                    Weather.Clear, seed);

                for (var attempt = 0; attempt < 4 && engine.State.Outcome == BattleOutcome.InProgress; attempt++)
                    engine.ResolveTurn(BattleAction.Run(BattleSide.Player));

                if (engine.State.Outcome == BattleOutcome.Fled) escapes++;
            }

            Assert.That(escapes, Is.EqualTo(20),
                "A Pikachu should always outrun a Geodude within four attempts.");
        }

        /// <summary>
        /// Being faster may never make escaping harder.
        ///
        /// The odds used to be taken modulo 256, so a runner past twice its blocker's Speed
        /// wrapped back round towards zero — Speed 68 against Speed 26 came out at 42% while
        /// a Speed 20 runner got 50%. The escape roll is the first draw of the battle, so a
        /// given seed hands every variant below the identical roll and the comparison is
        /// exact rather than statistical.
        /// </summary>
        [Test]
        public void EscapeOdds_NeverGetWorseAsTheRunnerGetsFaster()
        {
            const int Seeds = 40;
            const int BlockerSpeed = 26;
            var speeds = new[] { 10, 20, 26, 40, 52, 68, 120, 400 };
            var escapes = new int[speeds.Length];

            for (var i = 0; i < speeds.Length; i++)
            {
                for (var seed = 0; seed < Seeds; seed++)
                {
                    var engine = BattleTestBuilder.Engine();
                    var runner = BattleTestBuilder.Creature(TestData.Machop, 50, "growl")
                        .WithAbility(null).WithStats(140, 60, 60, 60, 60, speeds[i]);
                    var blocker = BattleTestBuilder.Creature(TestData.Machop, 50, "growl")
                        .WithAbility(null).WithStats(140, 60, 60, 60, 60, BlockerSpeed);

                    engine.Begin(BattleKind.Wild,
                        BattleTestBuilder.Party(runner),
                        BattleTestBuilder.Party(blocker),
                        Weather.Clear, seed);

                    engine.ResolveTurn(BattleAction.Run(BattleSide.Player));
                    if (engine.State.Outcome == BattleOutcome.Fled) escapes[i]++;
                }
            }

            for (var i = 1; i < speeds.Length; i++)
                Assert.That(escapes[i], Is.GreaterThanOrEqualTo(escapes[i - 1]),
                    $"Speed {speeds[i]} escaped {escapes[i]} times but Speed {speeds[i - 1]} escaped {escapes[i - 1]}.");

            // 68 against 26 is 334 before the attempt bonus is even added: outrunning the
            // byte range has to mean outrunning the fight, not wrapping back to nothing.
            Assert.That(escapes[Array.IndexOf(speeds, 68)], Is.EqualTo(Seeds),
                "A better than 2:1 Speed advantage must be a guaranteed escape.");
            Assert.That(escapes[0], Is.LessThan(Seeds),
                "…while a runner slower than its blocker must still sometimes be caught.");
        }

        /// <summary>Legal actions are gated correctly per battle kind and per side.</summary>
        [Test]
        public void LegalActions_AreGatedCorrectly()
        {
            var engine = BattleTestBuilder.Engine();
            var lead = BattleTestBuilder.Creature(TestData.Squirtle, 20, "water-gun", "tackle").WithAbility(null);
            var bench = BattleTestBuilder.Creature(TestData.Pikachu, 20, "thunder-shock").WithAbility(null);
            var fainted = BattleTestBuilder.Creature(TestData.Rattata, 20, "tackle").WithAbility(null);
            fainted.CurrentHp = 0;

            var foe = BattleTestBuilder.Creature(TestData.Machop, 20, "karate-chop").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(lead, bench, fainted),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 4007);

            var player = engine.LegalActions(BattleSide.Player);

            var moves = 0;
            var switches = 0;
            var captures = 0;
            var runs = 0;
            for (var i = 0; i < player.Count; i++)
            {
                switch (player[i].Type)
                {
                    case BattleAction.Kind.Move: moves++; break;
                    case BattleAction.Kind.Switch: switches++; break;
                    case BattleAction.Kind.Capture: captures++; break;
                    case BattleAction.Kind.Run: runs++; break;
                }
            }

            Assert.That(moves, Is.EqualTo(2));
            Assert.That(switches, Is.EqualTo(1), "The fainted party member must not be offered.");
            Assert.That(captures, Is.EqualTo(1));
            Assert.That(runs, Is.EqualTo(1));

            // The opponent gets no capture or escape affordances.
            var opponent = engine.LegalActions(BattleSide.Opponent);
            for (var i = 0; i < opponent.Count; i++)
                Assert.That(opponent[i].Type, Is.Not.EqualTo(BattleAction.Kind.Capture)
                    .And.Not.EqualTo(BattleAction.Kind.Run));
        }
    }
}
