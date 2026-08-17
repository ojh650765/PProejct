using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Participation experience: every player creature that stood on the field against
    /// the CURRENT opponent creature splits the award when it goes down, and the slate
    /// wipes only when the opponent's active changes.
    ///
    /// These tests pin the fix for a bookkeeping bug where the participant set was
    /// cleared on every PLAYER send-out — so a creature that softened the target up and
    /// pivoted out earned nothing, and only whoever was out at the KO was ever paid.
    /// </summary>
    public sealed class ExperienceShareTests
    {
        /// <summary>
        /// A → B, and B lands the KO: both were on the field against the victim, so both
        /// split the award — and the divisor matches the number of creatures actually
        /// paid.
        /// </summary>
        [Test]
        public void SwitchedOutParticipant_StillEarnsItsShare()
        {
            // Wild policy: the opponent never switches, so the participant slate cannot
            // be reset by an AI pivot mid-test.
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));

            var a = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop").WithAbility(null);
            var b = BattleTestBuilder.Creature(TestData.Pikachu, 40, "thunder-shock").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(a, b),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 9101);
            engine.DrainPendingEvents();

            var aExpBefore = a.Experience;

            // Turn 1: A pivots out. Turn 2: B lands the KO.
            engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.PlayerVictory));

            var gains = stream.OfType<ExperienceGainedEvent>();
            Assert.That(gains.Count, Is.EqualTo(2),
                "Both the creature that switched out and the one that finished must be paid.");

            var ids = new[] { gains[0].InstanceId, gains[1].InstanceId };
            Assert.That(ids, Is.EquivalentTo(new[] { a.InstanceId, b.InstanceId }));

            BattleTestBuilder.SpeciesRegistry.TryGet(TestData.Rattata, out var rattata);
            var expected = StatMath.ExperienceFromDefeat(rattata.BaseExperience, 5, participantCount: 2, isTrainerBattle: false);
            Assert.That(gains[0].Amount, Is.EqualTo(expected));
            Assert.That(gains[1].Amount, Is.EqualTo(expected));
            Assert.That(a.Experience, Is.EqualTo(aExpBefore + expected),
                "The benched participant's total must actually move, not just be reported.");
        }

        /// <summary>
        /// The slate is per opponent creature: once the first foe falls and its
        /// replacement comes out, only creatures that face the NEW foe split its award.
        /// </summary>
        [Test]
        public void ParticipantSlate_ClearsWhenTheOpponentReplaces()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));

            var a = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop").WithAbility(null);
            var b = BattleTestBuilder.Creature(TestData.Pikachu, 40, "thunder-shock").WithAbility(null);
            var foeLead = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle").WithAbility(null);
            var foeBench = BattleTestBuilder.Creature(TestData.Pidgey, 5, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(a, b),
                BattleTestBuilder.Party(foeLead, foeBench),
                Weather.Clear, seed: 9102);
            engine.DrainPendingEvents();

            // Turn 1: A pivots out, so both A and B have faced the lead.
            engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));

            // Turn 2: B knocks the lead out. Both participants split; the replacement is
            // sent out at the end of the same turn, which wipes the slate.
            var koTurn = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            var leadGains = koTurn.OfType<ExperienceGainedEvent>();

            Assert.That(foeLead.IsFainted, Is.True);
            Assert.That(leadGains.Count, Is.EqualTo(2), "Both A and B faced the lead.");

            BattleTestBuilder.SpeciesRegistry.TryGet(TestData.Rattata, out var rattata);
            var leadShare = StatMath.ExperienceFromDefeat(rattata.BaseExperience, 5, participantCount: 2, isTrainerBattle: true);
            Assert.That(leadGains[0].Amount, Is.EqualTo(leadShare));
            Assert.That(leadGains[1].Amount, Is.EqualTo(leadShare));

            // Turn 3: B alone faces the replacement and knocks it out — the award is
            // undivided and A, who never met this foe, gets nothing.
            var benchTurn = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            var benchGains = benchTurn.OfType<ExperienceGainedEvent>();

            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.PlayerVictory));
            Assert.That(benchGains.Count, Is.EqualTo(1),
                "Only the creature that faced the replacement may be paid for it.");
            Assert.That(benchGains[0].InstanceId, Is.EqualTo(b.InstanceId));

            BattleTestBuilder.SpeciesRegistry.TryGet(TestData.Pidgey, out var pidgey);
            var fullAward = StatMath.ExperienceFromDefeat(pidgey.BaseExperience, 5, participantCount: 1, isTrainerBattle: true);
            Assert.That(benchGains[0].Amount, Is.EqualTo(fullAward));
        }

        /// <summary>
        /// A participant that has since fainted is skipped, and the divisor skips it too:
        /// the count that splits the award always equals the number of creatures paid.
        /// </summary>
        [Test]
        public void FaintedParticipant_IsNeitherPaidNorCounted()
        {
            var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Wild));

            var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
            var closer = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(doomed, closer),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 9103);
            engine.DrainPendingEvents();

            // The level 5 lead goes down to the level 30 foe; the engine auto-replaces it
            // with the closer, which then finishes the fight.
            var events = new System.Collections.Generic.List<BattleEvent>();
            var turns = 0;
            while (engine.State.Outcome == BattleOutcome.InProgress && turns < 50)
            {
                events.AddRange(engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0)));
                turns++;
            }

            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.PlayerVictory));
            Assert.That(doomed.IsFainted, Is.True, "The setup expects the lead to fall first.");

            var gains = events.OfType<ExperienceGainedEvent>();
            Assert.That(gains.Count, Is.EqualTo(1), "A fainted participant earns nothing.");
            Assert.That(gains[0].InstanceId, Is.EqualTo(closer.InstanceId));

            BattleTestBuilder.SpeciesRegistry.TryGet(TestData.Rattata, out var rattata);
            Assert.That(gains[0].Amount,
                Is.EqualTo(StatMath.ExperienceFromDefeat(rattata.BaseExperience, 30, participantCount: 1, isTrainerBattle: false)),
                "The award must be divided by the paid participants only, not by the fallen.");
        }
    }
}
