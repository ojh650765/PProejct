using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// What happens when a player lets the turn clock run out.
    ///
    /// The clock itself is a UI concern and is not tested here — what is tested is the thing
    /// it submits on the player's behalf, because that is the part the engine and the network
    /// both have to agree about. A lapsed clock cannot mean "send nothing": two machines step
    /// together and neither resolves a turn until it holds both actions, so a side that sends
    /// nothing does not lose a turn, it strands the match.
    ///
    /// So it sends <see cref="BattleAction.Pass"/>, and these are the properties that has to
    /// have.
    /// </summary>
    public sealed class TurnClockTests
    {
        private const int Seed = 20260831;

        private static IList<CreatureInstance> Attackers() => BattleTestBuilder.Party(
            BattleTestBuilder.Creature(TestData.Charmander, 20, "ember", "scratch"),
            BattleTestBuilder.Creature(TestData.Squirtle, 20, "water-gun", "tackle"));

        private static IList<CreatureInstance> Defenders() => BattleTestBuilder.Party(
            BattleTestBuilder.Creature(TestData.Oddish, 20, "absorb", "poison-powder"),
            BattleTestBuilder.Creature(TestData.Geodude, 20, "rock-throw", "tackle"));

        private static BattleEngine Fresh()
        {
            var engine = BattleTestBuilder.Engine();
            engine.Begin(BattleKind.Trainer, Attackers(), Defenders(), Weather.Clear, Seed);
            engine.DrainPendingEvents();
            return engine;
        }

        [Test]
        public void PassingSide_DoesNotAttack()
        {
            var engine = Fresh();
            var before = engine.State.ActiveOf(BattleSide.Opponent).CurrentHp;

            engine.ResolveTurn(BattleAction.Pass(BattleSide.Player),
                               BattleAction.Pass(BattleSide.Opponent));

            Assert.That(engine.State.ActiveOf(BattleSide.Opponent).CurrentHp, Is.EqualTo(before),
                "A side that passed still dealt damage. Pass must resolve to nothing at all.");
        }

        [Test]
        public void PassingSide_StillTakesTheOtherSidesTurn()
        {
            var engine = Fresh();
            var before = engine.State.ActiveOf(BattleSide.Player).CurrentHp;

            engine.ResolveTurn(BattleAction.Pass(BattleSide.Player),
                               BattleAction.UseMove(BattleSide.Opponent, 0));

            Assert.That(engine.State.ActiveOf(BattleSide.Player).CurrentHp, Is.LessThan(before),
                "Passing sheltered the passing side. Losing the turn has to cost something, or " +
                "running the clock out would be a defensive tactic rather than a forfeit.");
        }

        [Test]
        public void BothSidesPassing_StillProducesEvents()
        {
            var engine = Fresh();

            var stream = engine.ResolveTurn(BattleAction.Pass(BattleSide.Player),
                                            BattleAction.Pass(BattleSide.Opponent));

            Assert.That(stream.Count, Is.GreaterThan(0),
                "A turn in which both sides passed produced no events. BattlePresenter treats " +
                "an empty stream as a battle it cannot advance and aborts on it — so two " +
                "players who both stepped away would end the match instead of losing a turn.");
        }

        /// <summary>
        /// The trap this whole action exists to avoid.
        ///
        /// Three separate files re-stamp an action onto a side, and every one of them ended
        /// with <c>default: return BattleAction.Run(side)</c>. A Pass that fell through any of
        /// them would arrive as a flee attempt — which in a wild battle can actually end the
        /// fight, and does it on ONE machine only, because only the side whose clock lapsed
        /// sends the action across.
        /// </summary>
        [Test]
        public void PassArrivingOnTheWrongSide_IsStillAPass()
        {
            var engine = Fresh();

            // Stamped Player, handed in as the opponent's — exactly what crossing the socket
            // does, since the wire carries a kind and the receiver stamps it itself.
            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0),
                               BattleAction.Pass(BattleSide.Player));

            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress),
                "Re-stamping a Pass ended the battle, which means it became a Run on the way " +
                "through.");
        }

        [Test]
        public void APassedTurn_IsIdenticalOnBothMachines()
        {
            static string Play()
            {
                var engine = Fresh();
                var stream = new List<BattleEvent>();

                stream.AddRange(engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0),
                                                   BattleAction.Pass(BattleSide.Opponent)));
                stream.AddRange(engine.ResolveTurn(BattleAction.Pass(BattleSide.Player),
                                                   BattleAction.UseMove(BattleSide.Opponent, 0)));
                stream.AddRange(engine.ResolveTurn(BattleAction.Pass(BattleSide.Player),
                                                   BattleAction.Pass(BattleSide.Opponent)));
                return stream.Signature();
            }

            Assert.That(Play(), Is.EqualTo(Play()),
                "Two machines fed the same actions, one of them a lapsed clock, produced " +
                "different battles.");
        }

        /// <summary>
        /// A passing side must not be handed one by the AI either. Nothing generates a Pass but
        /// the clock, and an AI that could choose it would be an AI that can stall a battle.
        /// </summary>
        [Test]
        public void TheAi_NeverChoosesToPass()
        {
            var engine = Fresh();

            foreach (var legal in engine.LegalActions(BattleSide.Opponent))
            {
                Assert.That(legal.Type, Is.Not.EqualTo(BattleAction.Kind.Pass),
                    "Pass was offered as a legal action. It is what is left when nobody chose, " +
                    "not something anybody may choose.");
            }
        }
    }
}
