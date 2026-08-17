using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// <see cref="BattleEngine.DeferPlayerReplacement"/>: with the flag on, a player faint
    /// leaves the battle waiting for a human instead of auto-replacing, and the next
    /// ResolveTurn is a free replacement turn — the player's switch resolves, the opponent
    /// does nothing, no upkeep runs and the turn counter stands still. With the flag off
    /// (the default) nothing here applies and the classic auto-replace path is untouched.
    /// </summary>
    public sealed class DeferredReplacementTests
    {
        /// <summary>
        /// A wild battle whose first turn knocks out the player's lead. Wild kind on
        /// purpose: it is the one that normally offers Capture and Run, so it exercises
        /// the rule that a fainted active is offered switches ONLY.
        /// </summary>
        private static BattleEngine StageFaint(out CreatureInstance doomed, out CreatureInstance bench,
            int seed = 5005, string benchAbility = null, bool defer = true)
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));
            engine.DeferPlayerReplacement = defer;

            doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
            bench = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(benchAbility);
            var killer = BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(doomed, bench),
                BattleTestBuilder.Party(killer),
                Weather.Clear, seed);
            engine.DrainPendingEvents();

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            return engine;
        }

        [Test]
        public void FaintWithBench_LeavesTheBattleInProgressAndWaiting()
        {
            var engine = StageFaint(out var doomed, out _);

            Assert.That(doomed.IsFainted, Is.True, "The level 5 lead should have been knocked out.");
            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress),
                "A healthy bench member means the battle is not over.");
            Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(doomed),
                "Under deferral the engine must NOT auto-replace the player's side.");
        }

        [Test]
        public void FaintedActive_IsOfferedSwitchesOnly()
        {
            var engine = StageFaint(out _, out _);

            var legal = engine.LegalActions(BattleSide.Player);
            Assert.That(legal.Count, Is.EqualTo(1));
            Assert.That(legal[0].Type, Is.EqualTo(BattleAction.Kind.Switch),
                "No moves, and in a wild battle no Capture or Run either — a replacement turn refuses them all.");
            Assert.That(legal[0].PartyIndex, Is.EqualTo(1));
        }

        [Test]
        public void ReplacementTurn_IsFree()
        {
            var engine = StageFaint(out _, out var bench);

            var killer = engine.State.ActiveOf(BattleSide.Opponent);
            var foeHpBefore = killer.CurrentHp;
            var turnBefore = engine.State.TurnNumber;

            var stream = engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));

            Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(bench));

            var sentOut = stream.OfType<CreatureSentOutEvent>();
            Assert.That(sentOut.Count, Is.EqualTo(1));
            Assert.That(sentOut[0].IsReplacement, Is.True, "The presenter plays this as a replacement, not an opener.");
            Assert.That(sentOut[0].Side, Is.EqualTo(BattleSide.Player));

            // Free means free: the opponent did not act, nothing took damage, no upkeep
            // ticked, and the turn counter did not move.
            Assert.That(killer.CurrentHp, Is.EqualTo(foeHpBefore), "The opponent must not act on a replacement turn.");
            Assert.That(stream.OfType<MoveDeclaredEvent>().Count, Is.EqualTo(0));
            Assert.That(stream.OfType<MoveExecutedEvent>().Count, Is.EqualTo(0));
            Assert.That(stream.OfType<DamageDealtEvent>().Count, Is.EqualTo(0));
            Assert.That(engine.State.TurnNumber, Is.EqualTo(turnBefore),
                "A replacement turn is an interjection, not a numbered exchange.");
            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress));

            // The battle then continues as normal turns again.
            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(engine.State.TurnNumber, Is.EqualTo(turnBefore + 1));
        }

        /// <summary>
        /// Anything that is not a legal switch degrades to the first healthy member with a
        /// warning, because the alternative is a stranded battle. This also proves the
        /// engine is safe if a driver mistakenly asks the AI and forwards a Move here.
        /// </summary>
        [Test]
        public void ReplacementTurn_FallsBackOnAnyIllegalAction()
        {
            var illegalActions = new[]
            {
                BattleAction.UseMove(BattleSide.Player, 0),        // not a switch at all
                BattleAction.SwitchTo(BattleSide.Player, 0),       // the fainted active itself
                BattleAction.SwitchTo(BattleSide.Player, 99),      // out of range
                BattleAction.Run(BattleSide.Player),               // refused during replacement
            };

            foreach (var action in illegalActions)
            {
                var engine = StageFaint(out _, out var bench);

                System.Collections.Generic.IReadOnlyList<BattleEvent> stream = null;
                Assert.DoesNotThrow(() => stream = engine.ResolveTurn(action));

                Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(bench),
                    $"{action.Type} must degrade to sending out the first healthy member.");
                Assert.That(stream.OfType<MessageEvent>()
                        .Exists(m => m.Text.Contains("sent out instead")), Is.True,
                    $"{action.Type} must be answered with a warning, not silence.");
                Assert.That(stream.OfType<DamageDealtEvent>().Count, Is.EqualTo(0),
                    "The fallback replacement turn is still free.");
                Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress));
            }
        }

        /// <summary>The incoming creature's entry ability fires exactly as on any send-out.</summary>
        [Test]
        public void ReplacementTurn_FiresTheEntryAbility()
        {
            var engine = StageFaint(out _, out _, benchAbility: AbilityIds.Intimidate);

            var stream = engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));

            Assert.That(stream.OfType<AbilityTriggeredEvent>()
                    .Exists(a => a.AbilityId == AbilityIds.Intimidate && a.Side == BattleSide.Player), Is.True,
                "Intimidate must announce itself on the deferred send-out.");
            Assert.That(stream.OfType<StatStageChangedEvent>()
                    .Exists(s => s.Target == BattleSide.Opponent && s.Stat == StatKind.Attack && s.Delta == -1), Is.True,
                "Intimidate must lower the opponent's Attack just as on a normal switch.");
        }

        /// <summary>Deferral defers a choice; it must not defer a loss that has no choice left.</summary>
        [Test]
        public void FaintWithNoBench_StillEndsTheBattle()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));
            engine.DeferPlayerReplacement = true;

            var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
            var killer = BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(doomed),
                BattleTestBuilder.Party(killer),
                Weather.Clear, seed: 5006);

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.PlayerDefeat));
        }

        /// <summary>The opponent's side always auto-replaces, deferral or not.</summary>
        [Test]
        public void OpponentReplacement_IsUnaffectedByTheFlag()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));
            engine.DeferPlayerReplacement = true;

            var strong = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop").WithAbility(null);
            var foeLead = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle").WithAbility(null);
            var foeBench = BattleTestBuilder.Creature(TestData.Pidgey, 5, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(strong),
                BattleTestBuilder.Party(foeLead, foeBench),
                Weather.Clear, seed: 5007);
            engine.DrainPendingEvents();

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(foeLead.IsFainted, Is.True);
            Assert.That(stream.OfType<CreatureSentOutEvent>()
                    .Exists(e => e.Side == BattleSide.Opponent && e.IsReplacement), Is.True,
                "The AI's side must keep replacing itself immediately.");
        }

        /// <summary>Flag off: the pre-existing auto-replace behaviour, byte for byte.</summary>
        [Test]
        public void FlagOff_AutoReplacesExactlyAsBefore()
        {
            var engine = StageFaint(out var doomed, out var bench, defer: false);

            Assert.That(doomed.IsFainted, Is.True);
            Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(bench),
                "Without deferral the engine replaces the fainted active itself, as every headless path expects.");
        }
    }
}
