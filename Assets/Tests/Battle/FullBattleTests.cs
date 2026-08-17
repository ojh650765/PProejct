using System;
using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Whole battles, played to a definite outcome. These are the tests that catch the
    /// failures unit tests cannot: a turn loop that never terminates, an event emitted
    /// after the battle ended, or a null reference on a faint replacement.
    /// </summary>
    public sealed class FullBattleTests
    {
        private const int TurnCap = 400;

        /// <summary>Plays a battle to completion, always attacking with the first usable move.</summary>
        private static BattleOutcome Play(BattleEngine engine, out int turns, out List<BattleEvent> allEvents)
        {
            allEvents = new List<BattleEvent>(engine.DrainPendingEvents());
            turns = 0;

            while (engine.State.Outcome == BattleOutcome.InProgress && turns < TurnCap)
            {
                var legal = engine.LegalActions(BattleSide.Player);
                var action = BattleAction.UseMove(BattleSide.Player, 0);
                for (var i = 0; i < legal.Count; i++)
                {
                    if (legal[i].Type != BattleAction.Kind.Move) continue;
                    action = legal[i];
                    break;
                }

                allEvents.AddRange(engine.ResolveTurn(action));
                turns++;
            }

            return engine.State.Outcome;
        }

        [Test]
        public void WildBattle_ReachesAnOutcomeWithoutThrowing()
        {
            for (var seed = 0; seed < 25; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                var player = BattleTestBuilder.Creature(TestData.Charmander, 12, "ember", "scratch");
                var wild = BattleTestBuilder.Creature(TestData.Oddish, 10, "absorb", "poison-powder");

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(player),
                    BattleTestBuilder.Party(wild),
                    Weather.Clear, seed);

                var outcome = Play(engine, out var turns, out _);

                Assert.That(outcome, Is.Not.EqualTo(BattleOutcome.InProgress), $"Seed {seed} never finished.");
                Assert.That(turns, Is.LessThan(TurnCap), $"Seed {seed} hit the turn cap.");
            }
        }

        [Test]
        public void TrainerBattle_WithFullPartiesReachesAnOutcome()
        {
            for (var seed = 100; seed < 115; seed++)
            {
                var engine = BattleTestBuilder.Engine();

                var party = BattleTestBuilder.Party(
                    BattleTestBuilder.Creature(TestData.Charmander, 16, "ember", "scratch", "growl"),
                    BattleTestBuilder.Creature(TestData.Squirtle, 16, "water-gun", "tackle"),
                    BattleTestBuilder.Creature(TestData.Pikachu, 16, "thunder-shock", "quick-attack"));

                var foes = BattleTestBuilder.Party(
                    BattleTestBuilder.Creature(TestData.Geodude, 16, "rock-throw", "tackle"),
                    BattleTestBuilder.Creature(TestData.Machop, 16, "karate-chop", "swords-dance"),
                    BattleTestBuilder.Creature(TestData.Gastly, 16, "confusion", "toxic"));

                engine.SetOpponentTrainer("hiker-vance");
                engine.Begin(BattleKind.Trainer, party, foes, Weather.Clear, seed);

                var outcome = Play(engine, out var turns, out var events);

                Assert.That(outcome, Is.EqualTo(BattleOutcome.PlayerVictory).Or.EqualTo(BattleOutcome.PlayerDefeat));
                Assert.That(turns, Is.LessThan(TurnCap));

                // Exactly one terminating event, and it is the last one.
                var endings = events.OfType<BattleEndedEvent>();
                Assert.That(endings.Count, Is.EqualTo(1), $"Seed {seed} ended {endings.Count} times.");
                Assert.That(events[events.Count - 1], Is.InstanceOf<BattleEndedEvent>());
            }
        }

        /// <summary>A fainted creature is replaced and the replacement is announced.</summary>
        [Test]
        public void Fainting_SendsOutAReplacement()
        {
            var engine = BattleTestBuilder.Engine();

            var strong = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop");
            var foeLead = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle");
            var foeBench = BattleTestBuilder.Creature(TestData.Pidgey, 5, "tackle");

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(strong),
                BattleTestBuilder.Party(foeLead, foeBench),
                Weather.Clear, seed: 31337);

            var outcome = Play(engine, out _, out var events);

            Assert.That(outcome, Is.EqualTo(BattleOutcome.PlayerVictory));

            var faints = events.OfType<CreatureFaintedEvent>();
            Assert.That(faints.Count, Is.EqualTo(2), "Both opposing creatures should have fainted.");

            var replacements = events.OfType<CreatureSentOutEvent>();
            Assert.That(replacements.Exists(e => e.IsReplacement && e.Side == BattleSide.Opponent), Is.True,
                "The second opposing creature must be announced as a replacement.");
        }

        /// <summary>
        /// When both sides fall on the same turn, neither replacement's entry ability may
        /// fire over a body.
        ///
        /// The opponent's replacement used to arrive first and immediately announce its
        /// Intimidate at the player's fainted creature, where TryChangeStage refused it — so
        /// the player was told about an ability that then did nothing, and the creature that
        /// arrived a moment later walked in un-intimidated.
        /// </summary>
        [Test]
        public void DoubleKnockout_FiresEntryAbilitiesOnlyOnceBothSidesHaveArrived()
        {
            // Rookie, because a switching policy would pull the lead out before it dies and
            // there would be no double knockout to order.
            for (var seed = 0; seed < 20; seed++)
            {
                ServiceHub.Reset();

                var engine = BattleTestBuilder.Engine(new BattleAi(AiDifficulty.Rookie));

                // Take Down from one HP: the hit kills the target, the recoil kills the user.
                var doomed = BattleTestBuilder.Creature(TestData.Geodude, 30, "take-down").WithAbility(null);
                var incoming = BattleTestBuilder.Creature(TestData.Machop, 30, "karate-chop").WithAbility(null);
                var foeLead = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle").WithAbility(null);
                var foeBench = BattleTestBuilder.Creature(TestData.Pidgey, 20, "tackle")
                    .WithAbility(AbilityIds.Intimidate);

                engine.Begin(BattleKind.Trainer,
                    BattleTestBuilder.Party(doomed, incoming),
                    BattleTestBuilder.Party(foeLead, foeBench),
                    Weather.Clear, seed);

                engine.DrainPendingEvents();
                doomed.CurrentHp = 1;

                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                if (!doomed.IsFainted || !foeLead.IsFainted) continue;

                Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(incoming));
                Assert.That(engine.State.ActiveOf(BattleSide.Opponent), Is.SameAs(foeBench));
                Assert.That(stream.OfType<AbilityTriggeredEvent>()
                    .Exists(e => e.AbilityId == AbilityIds.Intimidate), Is.True,
                    "The replacement's Intimidate should still be announced.");
                Assert.That(engine.State.StatStagesOf(BattleSide.Player)[(int)StatKind.Attack], Is.EqualTo(-1),
                    "Intimidate was announced over a fainted body and then quietly refused.");
                return;
            }

            Assert.Fail("No seed produced a double knockout, so the ordering was never exercised.");
        }

        /// <summary>
        /// With no <see cref="IReplacementChooser"/> registered, the engine keeps picking the
        /// first healthy member itself — the path every headless battle and test relies on.
        /// </summary>
        [Test]
        public void ForcedSwitch_FallsBackToFirstHealthyWithoutAChooser()
        {
            ServiceHub.Reset();

            var engine = BattleTestBuilder.Engine();
            var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
            var first = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
            var second = BattleTestBuilder.Creature(TestData.Geodude, 30, "rock-throw").WithAbility(null);
            var killer = BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null);

            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(doomed, first, second),
                BattleTestBuilder.Party(killer),
                Weather.Clear, seed: 7001);

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(doomed.IsFainted, Is.True, "The level 5 Zubat should have been knocked out.");
            Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(first),
                "Without a chooser the engine sends out the first healthy member.");
        }

        /// <summary>A registered chooser gets the decision, and its index is honoured.</summary>
        [Test]
        public void ForcedSwitch_HonoursARegisteredChooser()
        {
            ServiceHub.Reset();
            // Deliberately the last legal index, so passing cannot be luck.
            var chooser = new StubChooser(legal => legal[legal.Count - 1]);
            ServiceHub.Register<IReplacementChooser>(chooser);

            try
            {
                var engine = BattleTestBuilder.Engine();
                var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
                var skipped = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
                var wanted = BattleTestBuilder.Creature(TestData.Geodude, 30, "rock-throw").WithAbility(null);
                var killer = BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null);

                engine.Begin(BattleKind.Trainer,
                    BattleTestBuilder.Party(doomed, skipped, wanted),
                    BattleTestBuilder.Party(killer),
                    Weather.Clear, seed: 7002);

                engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

                Assert.That(doomed.IsFainted, Is.True);
                Assert.That(chooser.Calls, Is.EqualTo(1), "The chooser must be consulted exactly once.");
                Assert.That(chooser.LastSide, Is.EqualTo(BattleSide.Player),
                    "The AI chooses for its own side; the chooser is for the player only.");
                Assert.That(chooser.SawBadArguments, Is.False, "The engine passed a null or empty legal set.");
                Assert.That(chooser.LastLegalIndices, Is.EquivalentTo(new[] { 1, 2 }),
                    "Only healthy, non-active members in party order are offered.");
                Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(wanted));
            }
            finally
            {
                ServiceHub.Reset();
            }
        }

        /// <summary>An out-of-range answer degrades to the first legal index rather than corrupting the battle.</summary>
        [Test]
        public void ForcedSwitch_DegradesWhenTheChooserReturnsGarbage()
        {
            foreach (var badAnswer in new[] { -1, 0, 99 })
            {
                ServiceHub.Reset();
                // 0 is the fainted active, so it is illegal too — not merely out of bounds.
                ServiceHub.Register<IReplacementChooser>(new StubChooser(_ => badAnswer));

                try
                {
                    var engine = BattleTestBuilder.Engine();
                    var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
                    var expected = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
                    var other = BattleTestBuilder.Creature(TestData.Geodude, 30, "rock-throw").WithAbility(null);
                    var killer = BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null);

                    engine.Begin(BattleKind.Trainer,
                        BattleTestBuilder.Party(doomed, expected, other),
                        BattleTestBuilder.Party(killer),
                        Weather.Clear, seed: 7003);

                    Assert.DoesNotThrow(() => engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0)));

                    Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(expected),
                        $"A chooser returning {badAnswer} must fall back to the first legal index.");
                }
                finally
                {
                    ServiceHub.Reset();
                }
            }
        }

        /// <summary>A chooser that throws must not take the battle down with it.</summary>
        [Test]
        public void ForcedSwitch_SurvivesAThrowingChooser()
        {
            ServiceHub.Reset();
            ServiceHub.Register<IReplacementChooser>(new StubChooser(_ => throw new InvalidOperationException("boom")));

            try
            {
                var engine = BattleTestBuilder.Engine();
                var doomed = BattleTestBuilder.Creature(TestData.Zubat, 5, "tackle").WithAbility(null);
                var expected = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
                var killer = BattleTestBuilder.Creature(TestData.Machop, 45, "karate-chop").WithAbility(null);

                engine.Begin(BattleKind.Trainer,
                    BattleTestBuilder.Party(doomed, expected),
                    BattleTestBuilder.Party(killer),
                    Weather.Clear, seed: 7004);

                Assert.DoesNotThrow(() => engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0)));
                Assert.That(engine.State.ActiveOf(BattleSide.Player), Is.SameAs(expected));
            }
            finally
            {
                ServiceHub.Reset();
            }
        }

        /// <summary>Records what the engine asked, so the tests can assert on the contract.</summary>
        private sealed class StubChooser : IReplacementChooser
        {
            private readonly Func<IReadOnlyList<int>, int> _answer;

            public StubChooser(Func<IReadOnlyList<int>, int> answer) => _answer = answer;

            public int Calls { get; private set; }
            public BattleSide LastSide { get; private set; }
            public IReadOnlyList<int> LastLegalIndices { get; private set; }

            /// <summary>True if the engine ever broke its side of the contract.</summary>
            public bool SawBadArguments { get; private set; }

            public int ChooseReplacement(BattleSide side, IBattleStateView state, IReadOnlyList<int> legalIndices)
            {
                Calls++;
                LastSide = side;
                LastLegalIndices = legalIndices;

                // Recorded rather than asserted here: the engine catches exceptions from a
                // chooser, so an assertion failure raised inside this call would be
                // swallowed and the test would pass on the fallback path.
                if (state == null || legalIndices == null || legalIndices.Count == 0) SawBadArguments = true;

                return _answer(legalIndices);
            }
        }

        /// <summary>Winning banks experience, and enough of it levels the winner up.</summary>
        [Test]
        public void Victory_AwardsExperienceAndLevels()
        {
            var engine = BattleTestBuilder.Engine();

            // Quick Attack so the level 5 Machop finishes the weakened Pidgey before the
            // much higher level Pidgey gets to swing back.
            var player = BattleTestBuilder.Creature(TestData.Machop, 5, "quick-attack");
            var wild = BattleTestBuilder.Creature(TestData.Pidgey, 30, "tackle");
            wild.CurrentHp = 1;

            var levelBefore = player.Level;

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(wild),
                Weather.Clear, seed: 2468);

            var outcome = Play(engine, out _, out var events);

            Assert.That(outcome, Is.EqualTo(BattleOutcome.PlayerVictory));

            var gains = events.OfType<ExperienceGainedEvent>();
            Assert.That(gains.Count, Is.EqualTo(1));
            Assert.That(gains[0].InstanceId, Is.EqualTo(player.InstanceId));
            Assert.That(gains[0].Amount, Is.GreaterThan(0));
            Assert.That(gains[0].NewTotal, Is.EqualTo(player.Experience));
            Assert.That(gains[0].NewLevel, Is.EqualTo(player.Level));
            Assert.That(player.Level, Is.GreaterThan(levelBefore), "A level 30 Pidgey should level a level 5 Machop.");
            Assert.That(gains[0].LeveledUp, Is.True);
        }

        /// <summary>
        /// Experience is shared by everything that faced the creature that fell, not banked
        /// by whoever happened to land the last blow.
        ///
        /// The participant set used to be wiped and reseeded on every player send-out, so it
        /// never held more than one id: the split in AwardExperience always divided by one
        /// and the creature that did the early work got nothing at all.
        /// </summary>
        [Test]
        public void Experience_IsSplitAcrossEveryParticipant()
        {
            var engine = BattleTestBuilder.Engine();

            // Growl on the opener so it puts in turns without ending the fight, and the
            // finisher only arrives on the last one. Two different species, because the
            // fixture derives an instance id from species and level — two Machops would
            // share one, and the participant lookup would match either of them.
            var opener = BattleTestBuilder.Creature(TestData.Squirtle, 30, "growl").WithAbility(null);
            var finisher = BattleTestBuilder.Creature(TestData.Machop, 30, "karate-chop").WithAbility(null);
            var wild = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle").WithAbility(null);

            Assert.That(opener.InstanceId, Is.Not.EqualTo(finisher.InstanceId),
                "The two party members have to be distinguishable for this test to mean anything.");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(opener, finisher),
                BattleTestBuilder.Party(wild),
                Weather.Clear, seed: 9090);

            engine.DrainPendingEvents();

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            engine.ResolveTurn(BattleAction.SwitchTo(BattleSide.Player, 1));
            var kill = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(wild.IsFainted, Is.True, "A level 30 Machop should have finished a level 5 Rattata.");
            Assert.That(finisher.IsFainted, Is.False);
            Assert.That(opener.IsFainted, Is.False, "The opener has to survive to be able to collect.");

            var gains = kill.OfType<ExperienceGainedEvent>();
            Assert.That(gains.Count, Is.EqualTo(2), "Both creatures were on the field against that opponent.");
            Assert.That(gains.Exists(g => g.InstanceId == opener.InstanceId), Is.True,
                "The creature that did the early work was cut out of the award.");
            Assert.That(gains.Exists(g => g.InstanceId == finisher.InstanceId), Is.True);

            BattleTestBuilder.SpeciesRegistry.TryGet(TestData.Rattata, out var beaten);
            var shared = StatMath.ExperienceFromDefeat(beaten.BaseExperience, wild.Level, 2, false);
            var undivided = StatMath.ExperienceFromDefeat(beaten.BaseExperience, wild.Level, 1, false);

            Assert.That(gains[0].Amount, Is.EqualTo(shared));
            Assert.That(gains[1].Amount, Is.EqualTo(shared));
            Assert.That(shared, Is.LessThan(undivided), "A two-way split has to actually be smaller than a solo one.");
        }

        /// <summary>
        /// A species row with fewer than six base stats must not take the battle down. The
        /// BaseStat helper exists to tolerate hand-authored data, and RecomputeStats used to
        /// index straight past it — from inside a mid-battle level-up, of all places.
        /// </summary>
        [Test]
        public void RecomputeStats_ToleratesAShortBaseStatRow()
        {
            var species = TestData.Species();
            var moves = TestData.Moves();

            var creature = CreatureFactory.Create(TestData.Machop, 10, 1, species, moves, perfectIvs: true);
            var truncated = LookUp(species, TestData.Machop);
            truncated.BaseStats = new[] { 70, 80 };

            creature.Level = 20;
            Assert.DoesNotThrow(() => CreatureFactory.RecomputeStats(creature, truncated));

            for (var i = 0; i < StatKinds.BaseCount; i++)
                Assert.That(creature.Stats[i], Is.GreaterThan(0),
                    $"Stat {i} came out unusable after a short base stat row.");
        }

        /// <summary>A reused engine must not report the last battle's trainer in a wild encounter.</summary>
        [Test]
        public void TrainerId_DoesNotSurviveIntoTheNextBattle()
        {
            var engine = BattleTestBuilder.Engine();

            engine.SetOpponentTrainer("hiker-vance");
            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(BattleTestBuilder.Creature(TestData.Machop, 20, "karate-chop")),
                BattleTestBuilder.Party(BattleTestBuilder.Creature(TestData.Rattata, 20, "tackle")),
                Weather.Clear, seed: 4200);

            var trainerIntro = engine.DrainPendingEvents().OfType<BattleStartedEvent>()[0];
            Assert.That(trainerIntro.OpponentTrainerId, Is.EqualTo("hiker-vance"));

            // The same engine again, with nobody named this time.
            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(BattleTestBuilder.Creature(TestData.Machop, 20, "karate-chop")),
                BattleTestBuilder.Party(BattleTestBuilder.Creature(TestData.Pidgey, 8, "tackle")),
                Weather.Clear, seed: 4201);

            var wildIntro = engine.DrainPendingEvents().OfType<BattleStartedEvent>()[0];
            Assert.That(wildIntro.OpponentTrainerId, Is.Null,
                "The previous trainer's id leaked into a wild encounter.");
        }

        /// <summary>A level-up recomputes stats rather than leaving the authored ones.</summary>
        [Test]
        public void LevelUp_RecomputesStats()
        {
            var species = TestData.Species();
            var moves = TestData.Moves();

            var creature = CreatureFactory.Create(TestData.Machop, 10, 1, species, moves, perfectIvs: true);
            var beforeMaxHp = creature.MaxHp;
            var beforeAttack = creature.Stats[(int)StatKind.Attack];

            creature.Level = 20;
            CreatureFactory.RecomputeStats(creature, LookUp(species, TestData.Machop));

            Assert.That(creature.MaxHp, Is.GreaterThan(beforeMaxHp));
            Assert.That(creature.Stats[(int)StatKind.Attack], Is.GreaterThan(beforeAttack));
            Assert.That(creature.CurrentHp, Is.LessThanOrEqualTo(creature.MaxHp));
        }

        /// <summary>Trainer battles pay out; wild ones do not.</summary>
        [Test]
        public void PrizeMoney_OnlyComesFromTrainers()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop");
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle");

            engine.SetOpponentTrainer("youngster-joey");
            engine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 13);

            Play(engine, out _, out var events);
            var ending = events.OfType<BattleEndedEvent>()[0];
            Assert.That(ending.MoneyEarned, Is.GreaterThan(0));

            var wildEngine = BattleTestBuilder.Engine();
            var wildPlayer = BattleTestBuilder.Creature(TestData.Machop, 40, "karate-chop");
            var wildFoe = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle");
            wildEngine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(wildPlayer),
                BattleTestBuilder.Party(wildFoe),
                Weather.Clear, seed: 13);

            Play(wildEngine, out _, out var wildEvents);
            Assert.That(wildEvents.OfType<BattleEndedEvent>()[0].MoneyEarned, Is.EqualTo(0));
        }

        /// <summary>Running out of PP falls back to Struggle rather than stalling the battle.</summary>
        [Test]
        public void NoPpLeft_FallsBackToStruggle()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Rattata, 30, "tackle").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Gastly, 30, "confusion").WithAbility("levitate");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 606);

            // Drain the move so only Struggle is left.
            var slot = player.Moves[0];
            slot.CurrentPp = 0;
            player.Moves[0] = slot;

            var legal = engine.LegalActions(BattleSide.Player);
            Assert.That(legal.Count, Is.GreaterThan(0));

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, -1));
            var declared = stream.OfType<MoveDeclaredEvent>();

            Assert.That(declared.Exists(d => d.Side == BattleSide.Player && d.MoveId == "struggle"), Is.True);
            Assert.That(foe.CurrentHp, Is.LessThan(foe.MaxHp),
                "Struggle is typeless and must damage a Ghost that Normal moves cannot touch.");
        }

        /// <summary>Fleeing a wild battle ends it; fleeing a trainer battle does not.</summary>
        [Test]
        public void Running_WorksOnlyInWildBattles()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Rattata, 30, "tackle").WithAbility("run-away");
            var foe = BattleTestBuilder.Creature(TestData.Geodude, 5, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 21);

            engine.ResolveTurn(BattleAction.Run(BattleSide.Player));
            Assert.That(engine.State.Outcome, Is.EqualTo(BattleOutcome.Fled));

            var trainerEngine = BattleTestBuilder.Engine();
            var trainerPlayer = BattleTestBuilder.Creature(TestData.Rattata, 30, "tackle").WithAbility("run-away");
            var trainerFoe = BattleTestBuilder.Creature(TestData.Geodude, 5, "tackle").WithAbility(null);
            trainerEngine.Begin(BattleKind.Trainer,
                BattleTestBuilder.Party(trainerPlayer),
                BattleTestBuilder.Party(trainerFoe),
                Weather.Clear, seed: 21);

            trainerEngine.ResolveTurn(BattleAction.Run(BattleSide.Player));
            Assert.That(trainerEngine.State.Outcome, Is.EqualTo(BattleOutcome.InProgress));
        }

        /// <summary>Every event carries the turn it belongs to, and turns never go backwards.</summary>
        [Test]
        public void EventStream_IsOrderedAndTurnStamped()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Charmander, 14, "ember", "scratch");
            var wild = BattleTestBuilder.Creature(TestData.Oddish, 14, "absorb", "sleep-powder");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(wild),
                Weather.Clear, seed: 999);

            Play(engine, out _, out var events);

            var lastTurn = 0;
            for (var i = 0; i < events.Count; i++)
            {
                Assert.That(events[i].Turn, Is.GreaterThanOrEqualTo(lastTurn), "Events went back in time.");
                Assert.That(events[i].SuggestedDuration, Is.GreaterThanOrEqualTo(0f));
                lastTurn = events[i].Turn;
            }

            Assert.That(events[0], Is.InstanceOf<BattleStartedEvent>());
        }

        /// <summary>
        /// The difficulty knob must actually reach the decisions. Comparing win rates would
        /// be a coin-flip assertion, so this checks the stronger, non-statistical claim: at
        /// the same seed the two policies play visibly different battles.
        /// </summary>
        [Test]
        public void AiDifficulty_ChangesPlay()
        {
            var divergences = 0;

            for (var seed = 0; seed < 20; seed++)
            {
                var ace = RunAgainst(new BattleAi(AiDifficulty.Ace), seed);
                var wild = RunAgainst(new BattleAi(AiDifficulty.Wild), seed);
                if (ace != wild) divergences++;
            }

            Assert.That(divergences, Is.GreaterThan(0),
                "Ace and Wild policies produced byte-identical battles at every seed — the knob is not wired up.");
        }

        /// <summary>Every difficulty must be able to play a battle through without throwing.</summary>
        [Test]
        public void EveryDifficulty_CompletesABattle()
        {
            foreach (AiDifficulty difficulty in Enum.GetValues(typeof(AiDifficulty)))
            {
                for (var seed = 0; seed < 5; seed++)
                {
                    var engine = BattleTestBuilder.Engine(new BattleAi(difficulty));
                    Stage(engine, seed);
                    var outcome = Play(engine, out var turns, out _);

                    Assert.That(outcome, Is.Not.EqualTo(BattleOutcome.InProgress),
                        $"{difficulty} at seed {seed} never finished.");
                    Assert.That(turns, Is.LessThan(TurnCap));
                }
            }
        }

        private static string RunAgainst(BattleAi ai, int seed)
        {
            var engine = BattleTestBuilder.Engine(ai);
            Stage(engine, seed);
            Play(engine, out _, out var events);
            return events.Signature();
        }

        private static void Stage(BattleEngine engine, int seed)
        {
            var player = BattleTestBuilder.Party(
                BattleTestBuilder.Creature(TestData.Pidgey, 16, "tackle", "sand-attack"),
                BattleTestBuilder.Creature(TestData.Rattata, 16, "tackle", "quick-attack"));

            var foes = BattleTestBuilder.Party(
                BattleTestBuilder.Creature(TestData.Geodude, 16, "rock-throw", "tackle", "protect"),
                BattleTestBuilder.Creature(TestData.Machop, 16, "karate-chop", "swords-dance"));

            engine.Begin(BattleKind.Trainer, player, foes, Weather.Clear, seed);
        }

        private static SpeciesData LookUp(FakeSpeciesRegistry registry, int id)
        {
            registry.TryGet(id, out var species);
            return species;
        }
    }
}
