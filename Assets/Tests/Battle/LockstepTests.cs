using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// PvP is two copies of the engine stepped in lockstep, and these are the properties that
    /// have to hold or it is not a shared battle at all.
    ///
    /// The seam is <see cref="BattleEngine.ResolveTurn(BattleAction, BattleAction?)"/>: hand
    /// the opponent's action in and the AI is never consulted. Before it existed, a PvP match
    /// found a real opponent, fetched their real team, agreed a shared seed — and then played
    /// the AI, which is what these tests exist to stop coming back.
    ///
    /// The subtle one is <see cref="MirroredSides_Diverge"/>. It asserts that doing the
    /// obvious thing is WRONG, because the obvious thing is very tempting: let each client be
    /// the Player side so that "my team" is always in the same place. Two engines arranged as
    /// mirror images consume their generators in opposite order, so the same seed yields two
    /// different battles and the players disagree about who won. The rule that falls out —
    /// both machines simulate the same assignment, and player 1 mirrors only the presentation
    /// — is not obvious enough to leave as a comment.
    /// </summary>
    public sealed class LockstepTests
    {
        private const int Seed = 20260827;

        private static IList<CreatureInstance> TeamOne() => BattleTestBuilder.Party(
            BattleTestBuilder.Creature(TestData.Charmander, 20, "ember", "scratch", "growl"),
            BattleTestBuilder.Creature(TestData.Squirtle, 20, "water-gun", "tackle"));

        private static IList<CreatureInstance> TeamTwo() => BattleTestBuilder.Party(
            BattleTestBuilder.Creature(TestData.Oddish, 20, "absorb", "poison-powder"),
            BattleTestBuilder.Creature(TestData.Geodude, 20, "rock-throw", "tackle"));

        /// <summary>
        /// The pair of choices for each turn: what player 0 does, and what player 1 does.
        /// Both machines are fed exactly this, and nothing else passes between them.
        /// </summary>
        private static (int zero, int one)[] Script() => new[]
        {
            (0, 0), (1, 1), (0, 0), (2, 1), (0, 0), (1, 0), (0, 1), (0, 0),
        };

        private static BattleEngine Canonical()
        {
            var engine = BattleTestBuilder.Engine();
            engine.Begin(BattleKind.Trainer, TeamOne(), TeamTwo(), Weather.Clear, Seed);
            return engine;
        }

        /// <summary>Runs a whole battle canonically and returns its full event signature.</summary>
        private static string RunCanonical(out long draws)
        {
            var engine = Canonical();
            var stream = new List<BattleEvent>(engine.DrainPendingEvents());

            foreach (var (zero, one) in Script())
            {
                if (engine.State.Outcome != BattleOutcome.InProgress) break;
                stream.AddRange(engine.ResolveTurn(
                    BattleAction.UseMove(BattleSide.Player, zero),
                    BattleAction.UseMove(BattleSide.Opponent, one)));
            }

            draws = engine.Random.DrawCount;
            return stream.Signature();
        }

        [Test]
        public void BothMachinesSimulatingCanonically_ProduceTheSameBattle()
        {
            var first = RunCanonical(out var firstDraws);
            var second = RunCanonical(out var secondDraws);

            Assert.That(second, Is.EqualTo(first),
                "Two machines fed the same seed and the same pair of actions produced different " +
                "battles. In a real match the two players would disagree about who won.");
            Assert.That(secondDraws, Is.EqualTo(firstDraws),
                "The generators were consumed a different number of times, which is the earliest " +
                "signal of a desync and the one worth exchanging every turn.");
            Assert.That(first.Length, Is.GreaterThan(0), "The battle produced no events at all.");
        }

        [Test]
        public void MirroredSides_Diverge()
        {
            var canonical = RunCanonical(out _);

            // The tempting arrangement: player 1 puts THEIR team on the Player side so that
            // "mine" is always the near side, and swaps the actions to match. Every action is
            // the same action, every creature is the same creature, the seed is the same seed.
            var mirrored = BattleTestBuilder.Engine();
            mirrored.Begin(BattleKind.Trainer, TeamTwo(), TeamOne(), Weather.Clear, Seed);
            var stream = new List<BattleEvent>(mirrored.DrainPendingEvents());

            foreach (var (zero, one) in Script())
            {
                if (mirrored.State.Outcome != BattleOutcome.InProgress) break;
                stream.AddRange(mirrored.ResolveTurn(
                    BattleAction.UseMove(BattleSide.Player, one),
                    BattleAction.UseMove(BattleSide.Opponent, zero)));
            }

            Assert.That(stream.Signature(), Is.Not.EqualTo(canonical),
                "A mirrored simulation matched the canonical one. If that is genuinely true " +
                "for every seed then the presentation-mirroring rule could be dropped — but " +
                "far more likely this script stopped reaching the code that draws per side, " +
                "and the test has quietly stopped protecting anything.");
        }

        [Test]
        public void SuppliedAction_IsUsedInsteadOfTheAi()
        {
            // Two runs identical in every respect except which move the opponent is TOLD to
            // use. If the supplied action were being ignored in favour of the AI, both runs
            // would be the same battle.
            string Run(int opponentMove)
            {
                var engine = Canonical();
                var stream = new List<BattleEvent>(engine.DrainPendingEvents());
                for (var turn = 0; turn < 4 && engine.State.Outcome == BattleOutcome.InProgress; turn++)
                {
                    stream.AddRange(engine.ResolveTurn(
                        BattleAction.UseMove(BattleSide.Player, 0),
                        BattleAction.UseMove(BattleSide.Opponent, opponentMove)));
                }
                return stream.Signature();
            }

            Assert.That(Run(1), Is.Not.EqualTo(Run(0)),
                "Telling the opponent to use a different move changed nothing, so the supplied " +
                "action is being dropped and the AI is still playing the far side.");
        }

        [Test]
        public void WithoutASuppliedAction_TheAiStillPlays()
        {
            // The old single-argument call has to behave exactly as it always did, because
            // every other battle in the game — story, wild, trainer, autoplay — uses it.
            var engine = Canonical();
            engine.DrainPendingEvents();

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(stream.Count, Is.GreaterThan(0),
                "A turn with no supplied opponent action produced nothing, so the AI was not asked.");
        }

        // ---- the opponent's forced switch ------------------------------------------------

        /// <summary>A battle whose first turn knocks the OPPONENT's lead out.</summary>
        private static BattleEngine StageOpponentFaint(bool defer, out CreatureInstance bench)
        {
            bench = BattleTestBuilder.Creature(TestData.Geodude, 30, "rock-throw").WithAbility(null);
            var engine = BattleTestBuilder.Engine();
            engine.DeferOpponentReplacement = defer;

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(
                    BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null)),
                BattleTestBuilder.Party(
                    BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null),
                    bench),
                Weather.Clear, 5005);
            engine.DrainPendingEvents();

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0),
                               BattleAction.UseMove(BattleSide.Opponent, 0));
            return engine;
        }

        [Test]
        public void DeferredOpponent_DoesNotAutoReplace()
        {
            var engine = StageOpponentFaint(defer: true, out _);

            Assert.That(engine.State.ActiveOf(BattleSide.Opponent).IsFainted, Is.True,
                "The opponent's lead should have been knocked out.");
            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress),
                "A healthy bench member means the battle is not over.");
        }

        [Test]
        public void DeferredOpponent_WithNoAnswerYet_LeavesTheSlotEmpty()
        {
            var engine = StageOpponentFaint(defer: true, out _);
            var fainted = engine.State.ActiveOf(BattleSide.Opponent);

            // The far player has not chosen yet. Filling the slot locally would both rob them
            // of the choice and put the two machines into different states.
            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0), null);

            Assert.That(engine.State.ActiveOf(BattleSide.Opponent), Is.SameAs(fainted),
                "The engine picked a replacement for the remote player rather than waiting.");
        }

        [Test]
        public void DeferredOpponent_SwitchArrives_FieldsTheirChoice()
        {
            var engine = StageOpponentFaint(defer: true, out var bench);

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0),
                               BattleAction.SwitchTo(BattleSide.Opponent, 1));

            Assert.That(engine.State.ActiveOf(BattleSide.Opponent), Is.SameAs(bench),
                "The side fielded somebody other than the creature the remote player named.");
        }

        [Test]
        public void WithoutTheFlag_TheOpponentAutoReplacesAsBefore()
        {
            // Every non-PvP battle in the game relies on this, so it is asserted rather than
            // assumed: with the flag off the engine fills the slot inside the same turn.
            var engine = StageOpponentFaint(defer: false, out _);

            Assert.That(engine.State.ActiveOf(BattleSide.Opponent).IsFainted, Is.False,
                "With deferral off the opponent must auto-replace exactly as it always has.");
        }
    }
}
