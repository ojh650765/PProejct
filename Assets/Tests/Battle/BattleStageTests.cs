using System;
using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// The overworld seam. Every test here is ultimately about one rule: the resolve
    /// callback fires exactly once, whatever happens, because the player is frozen mid
    /// transition until it does.
    /// </summary>
    public sealed class BattleStageTests
    {
        private FakeSpeciesRegistry _species;
        private FakeMoveRegistry _moves;

        [SetUp]
        public void SetUp()
        {
            ServiceHub.Reset();
            _species = TestData.Species();
            _moves = TestData.Moves();
        }

        [TearDown]
        public void TearDown() => ServiceHub.Reset();

        private void RegisterDataLayer()
        {
            ServiceHub.Register<ISpeciesRegistry>(_species);
            ServiceHub.Register<IMoveRegistry>(_moves);
            ServiceHub.Register<ITypeChart>(new FakeTypeChart());
        }

        private static EncounterRequest WildRequest(int seed = 77) => new EncounterRequest
        {
            Kind = BattleKind.Wild,
            WildSpeciesId = TestData.Pidgey,
            WildLevel = 6,
            Weather = Weather.Clear,
            Seed = seed,
        };

        /// <summary>With no presenter listening, the stage plays the battle out and reports.</summary>
        [Test]
        public void UnclaimedBattle_ResolvesOnItsOwn()
        {
            RegisterDataLayer();
            var stage = new BattleStage();

            var calls = 0;
            EncounterResult result = null;

            stage.BeginEncounter(WildRequest(), r => { calls++; result = r; });

            Assert.That(calls, Is.EqualTo(1), "The callback must fire exactly once.");
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Outcome, Is.Not.EqualTo(BattleOutcome.InProgress));
            Assert.That(stage.IsBattleActive, Is.False);
            Assert.That(result.SpeciesSeen, Does.Contain(TestData.Pidgey));
        }

        /// <summary>A presenter that subscribes claims the battle and drives it itself.</summary>
        [Test]
        public void ClaimedBattle_WaitsForSubmittedActions()
        {
            RegisterDataLayer();
            var stage = new BattleStage();

            var staged = 0;
            stage.BattleStaged += _ => staged++;

            var calls = 0;
            EncounterResult result = null;
            stage.BeginEncounter(WildRequest(), r => { calls++; result = r; });

            Assert.That(staged, Is.EqualTo(1));
            Assert.That(stage.IsBattleActive, Is.True, "A claimed battle must wait rather than auto-resolve.");
            Assert.That(calls, Is.EqualTo(0));

            var turns = 0;
            while (stage.IsBattleActive && turns < 300)
            {
                stage.SubmitAction(BattleAction.UseMove(BattleSide.Player, 0));
                turns++;
            }

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.Not.EqualTo(BattleOutcome.InProgress));
        }

        /// <summary>Every event the engine produced reaches the subscriber, intro included.</summary>
        [Test]
        public void EventsProduced_CarriesTheWholeStream()
        {
            RegisterDataLayer();
            var stage = new BattleStage();

            var seen = new List<BattleEvent>();
            stage.EventsProduced += stream => seen.AddRange(stream);

            stage.BeginEncounter(WildRequest(), _ => { });

            Assert.That(seen.Count, Is.GreaterThan(0));
            Assert.That(seen[0], Is.InstanceOf<BattleStartedEvent>(), "The intro must not be swallowed.");
            Assert.That(seen[seen.Count - 1], Is.InstanceOf<BattleEndedEvent>());
        }

        /// <summary>A null request is still an answered request.</summary>
        [Test]
        public void NullRequest_StillResolves()
        {
            RegisterDataLayer();
            var stage = new BattleStage();

            var calls = 0;
            EncounterResult result = null;
            stage.BeginEncounter(null, r => { calls++; result = r; });

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.EqualTo(BattleOutcome.Fled));
        }

        /// <summary>A missing data layer aborts as a flee rather than throwing into the flow.</summary>
        [Test]
        public void MissingRegistries_ResolveAsAFlee()
        {
            var stage = new BattleStage();

            var calls = 0;
            EncounterResult result = null;

            Assert.DoesNotThrow(() => stage.BeginEncounter(WildRequest(), r => { calls++; result = r; }));

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.EqualTo(BattleOutcome.Fled));
            Assert.That(stage.LastFailureReason, Is.Not.Null.And.Not.Empty);
        }

        /// <summary>An unknown species aborts as a flee instead of letting the factory throw.</summary>
        [Test]
        public void UnbuildableParty_ResolvesAsAFlee()
        {
            RegisterDataLayer();
            var stage = new BattleStage();

            var request = WildRequest();
            request.WildSpeciesId = 987654;

            var calls = 0;
            EncounterResult result = null;
            Assert.DoesNotThrow(() => stage.BeginEncounter(request, r => { calls++; result = r; }));

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.EqualTo(BattleOutcome.Fled));
        }

        /// <summary>Aborting mid-battle releases the waiting flow exactly once.</summary>
        [Test]
        public void Abort_ResolvesOnceAndOnlyWhileActive()
        {
            RegisterDataLayer();
            var stage = new BattleStage();
            stage.BattleStaged += _ => { };

            var calls = 0;
            stage.BeginEncounter(WildRequest(), _ => calls++);

            Assert.That(stage.IsBattleActive, Is.True);

            stage.Abort("torn down");
            Assert.That(calls, Is.EqualTo(1));

            stage.Abort("torn down again");
            Assert.That(calls, Is.EqualTo(1), "Aborting a finished battle must not resolve it twice.");
        }

        /// <summary>Submitting into a dead stage is a no-op, not a crash.</summary>
        [Test]
        public void SubmitAction_IsSafeWhenNoBattleIsRunning()
        {
            var stage = new BattleStage();
            Assert.That(stage.SubmitAction(BattleAction.Run(BattleSide.Player)).Count, Is.EqualTo(0));
        }

        /// <summary>A second encounter while one is live is refused, but still answered.</summary>
        [Test]
        public void ConcurrentEncounter_IsRefusedButAnswered()
        {
            RegisterDataLayer();
            var stage = new BattleStage();
            stage.BattleStaged += _ => { };

            var first = 0;
            var second = 0;
            stage.BeginEncounter(WildRequest(1), _ => first++);
            stage.BeginEncounter(WildRequest(2), _ => second++);

            Assert.That(first, Is.EqualTo(0), "The live battle must not be cancelled by the second request.");
            Assert.That(second, Is.EqualTo(1), "The refused request must still get an answer.");
        }

        /// <summary>The player's own party is used when a profile is registered.</summary>
        [Test]
        public void PlayerParty_ComesFromTheProfileWhenThereIsOne()
        {
            RegisterDataLayer();

            var mine = CreatureFactory.Create(TestData.Machop, 30, 1, _species, _moves, perfectIvs: true);
            ServiceHub.Register<IPlayerProfile>(new StubProfile(mine));

            var stage = new BattleStage();
            stage.BattleStaged += _ => { };
            stage.BeginEncounter(WildRequest(), _ => { });

            Assert.That(stage.Engine.State.ActiveOf(BattleSide.Player), Is.SameAs(mine));
        }

        /// <summary>With no profile at all, a fallback creature keeps the loop runnable.</summary>
        [Test]
        public void PlayerParty_FallsBackWhenThereIsNoProfile()
        {
            RegisterDataLayer();

            var stage = new BattleStage();
            stage.BattleStaged += _ => { };
            stage.BeginEncounter(WildRequest(), _ => { });

            Assert.That(stage.Engine.State.PartyOf(BattleSide.Player).Count, Is.EqualTo(1));
            Assert.That(stage.Engine.State.ActiveOf(BattleSide.Player), Is.Not.Null);
        }

        /// <summary>A trainer battle draws its party and its prize from the trainer registry.</summary>
        [Test]
        public void TrainerBattle_UsesTheRegistryPartyAndReward()
        {
            RegisterDataLayer();

            var registry = new StubTrainers(_species, _moves);
            ServiceHub.Register<ITrainerRegistry>(registry);
            ServiceHub.Register<IPlayerProfile>(new StubProfile(
                CreatureFactory.Create(TestData.Machop, 45, 1, _species, _moves, perfectIvs: true)));

            var stage = new BattleStage();

            EncounterResult result = null;
            stage.BeginEncounter(new EncounterRequest
            {
                Kind = BattleKind.Trainer,
                TrainerId = "hiker-vance",
                Weather = Weather.Clear,
                Seed = 5150,
            }, r => result = r);

            Assert.That(registry.BuildCalls, Is.EqualTo(1));
            Assert.That(result, Is.Not.Null);

            if (result.Outcome == BattleOutcome.PlayerVictory)
                Assert.That(result.MoneyDelta, Is.EqualTo(StubTrainers.Reward),
                    "The authored reward must win over the engine's level estimate.");
            else
                Assert.That(result.MoneyDelta, Is.EqualTo(0));
        }

        /// <summary>An unknown trainer id still produces a runnable battle.</summary>
        [Test]
        public void TrainerBattle_FallsBackForAnUnknownId()
        {
            RegisterDataLayer();

            var calls = 0;
            EncounterResult result = null;

            var stage = new BattleStage();
            stage.BeginEncounter(new EncounterRequest
            {
                Kind = BattleKind.Trainer,
                TrainerId = "nobody-in-particular",
                WildSpeciesId = TestData.Geodude,
                WildLevel = 10,
                Seed = 606,
            }, r => { calls++; result = r; });

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.Not.EqualTo(BattleOutcome.InProgress));
        }

        /// <summary>The request's seed reaches the engine, so the same encounter replays exactly.</summary>
        [Test]
        public void EncounterSeed_ReproducesTheBattle()
        {
            RegisterDataLayer();

            string RunOnce(int seed)
            {
                var events = new List<BattleEvent>();
                var stage = new BattleStage();
                stage.EventsProduced += stream => events.AddRange(stream);
                stage.BeginEncounter(WildRequest(seed), _ => { });
                return events.Signature();
            }

            Assert.That(RunOnce(31337), Is.EqualTo(RunOnce(31337)));
            Assert.That(RunOnce(31337), Is.Not.EqualTo(RunOnce(31338)));
        }

        /// <summary>A capture is reported back to the overworld so the creature can be kept.</summary>
        [Test]
        public void Capture_IsReportedInTheResult()
        {
            RegisterDataLayer();
            ServiceHub.Register<IPlayerProfile>(new StubProfile(
                CreatureFactory.Create(TestData.Machop, 40, 1, _species, _moves, perfectIvs: true)));

            var stage = new BattleStage();
            stage.BattleStaged += _ => { };

            EncounterResult result = null;
            stage.BeginEncounter(WildRequest(2024), r => result = r);

            // Throw balls until one holds; a level 6 Pidgey is a very catchable target.
            var attempts = 0;
            while (stage.IsBattleActive && attempts < 60)
            {
                stage.SubmitAction(BattleAction.Capture(BattleSide.Player, "ultra-ball"));
                attempts++;
            }

            Assert.That(result, Is.Not.Null);
            if (result.Outcome == BattleOutcome.Captured)
                Assert.That(result.CapturedCreature, Is.Not.Null, "A capture must hand the creature back.");
        }

        /// <summary>
        /// The dex only learns what the player actually saw. A trainer's reserve that
        /// never entered the field must not be reported — the stage used to hand the whole
        /// opposing party to the overworld, which let a battle abandoned after one turn
        /// fill in entries for creatures the player never laid eyes on.
        /// </summary>
        [Test]
        public void SpeciesSeen_OnlyReportsScoutedOpponents()
        {
            RegisterDataLayer();
            ServiceHub.Register<ITrainerRegistry>(new ReserveTrainers(_species, _moves));
            ServiceHub.Register<IPlayerProfile>(new StubProfile(
                CreatureFactory.Create(TestData.Machop, 40, 1, _species, _moves, perfectIvs: true)));

            var stage = new BattleStage();
            stage.BattleStaged += _ => { };

            // A Rookie never pivots, so the lead cannot dodge its scripted knockout by
            // switching out on the first turn and putting the reserve in front instead.
            stage.TrainerDifficulty = AiDifficulty.Rookie;

            EncounterResult result = null;
            stage.BeginEncounter(new EncounterRequest
            {
                Kind = BattleKind.Trainer,
                TrainerId = "reserve-holder",
                Weather = Weather.Clear,
                Seed = 4242,
            }, r => result = r);

            // One turn: the level 40 Machop knocks out the level 5 lead. The reserve is
            // sent out as its replacement but has not yet acted when the battle is torn
            // down, so the player has met the lead and only glimpsed the reserve's entry.
            stage.SubmitAction(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(stage.IsBattleActive, Is.True, "The reserve should keep the battle alive.");

            stage.Abort("scouting test teardown");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.SpeciesSeen, Does.Contain(TestData.Rattata),
                "The lead fainted in front of the player and must be reported.");
            Assert.That(result.SpeciesSeen, Does.Not.Contain(TestData.Pidgey),
                "The reserve never showed the player anything and must not be reported.");
        }

        // ---- Stubs -------------------------------------------------------------------

        private sealed class StubProfile : IPlayerProfile
        {
            private readonly List<CreatureInstance> _party;

            public StubProfile(params CreatureInstance[] party) => _party = new List<CreatureInstance>(party);

            public string TrainerName => "Tester";
            public int Money => 0;
            public IReadOnlyList<CreatureInstance> Party => _party;
            public IReadOnlyList<CreatureInstance> Storage => Array.Empty<CreatureInstance>();
            public IReadOnlyCollection<int> SeenSpecies => Array.Empty<int>();
            public IReadOnlyCollection<int> CaughtSpecies => Array.Empty<int>();
            public IReadOnlyDictionary<string, int> Inventory => new Dictionary<string, int>();

            public bool TryAddToParty(CreatureInstance creature) { _party.Add(creature); return true; }
            public void AddItem(string itemId, int count) { }
            public bool ConsumeItem(string itemId, int count = 1) => true;
            public void MarkSeen(int speciesId) { }
            public void MarkCaught(int speciesId) { }
        }

        private sealed class StubTrainers : ITrainerRegistry
        {
            public const int Reward = 640;

            private readonly FakeSpeciesRegistry _species;
            private readonly FakeMoveRegistry _moves;

            public StubTrainers(FakeSpeciesRegistry species, FakeMoveRegistry moves)
            {
                _species = species; _moves = moves;
            }

            public int BuildCalls { get; private set; }

            public bool TryGetProfile(string trainerId, out TrainerProfile profile)
            {
                profile = new TrainerProfile
                {
                    TrainerId = trainerId,
                    DisplayName = "Hiker Vance",
                    Reward = Reward,
                };
                return true;
            }

            public IReadOnlyList<CreatureInstance> BuildParty(string trainerId, int levelOffset = 0)
            {
                BuildCalls++;
                // A fresh list every call, as the contract requires — the engine mutates it.
                return new List<CreatureInstance>
                {
                    CreatureFactory.Create(TestData.Geodude, 12 + levelOffset, 1, _species, _moves),
                    CreatureFactory.Create(TestData.Machop, 12 + levelOffset, 2, _species, _moves),
                };
            }
        }

        /// <summary>
        /// A frail lead in front of a reserve, for the scouting test: the lead falls in one
        /// hit and the reserve enters but never gets to act. Deliberately species with no
        /// survive-lethal abilities, so the one-turn knockout cannot be saved by a Sturdy.
        /// </summary>
        private sealed class ReserveTrainers : ITrainerRegistry
        {
            private readonly FakeSpeciesRegistry _species;
            private readonly FakeMoveRegistry _moves;

            public ReserveTrainers(FakeSpeciesRegistry species, FakeMoveRegistry moves)
            {
                _species = species; _moves = moves;
            }

            public bool TryGetProfile(string trainerId, out TrainerProfile profile)
            {
                profile = new TrainerProfile { TrainerId = trainerId, DisplayName = "Reserve Holder", Reward = 100 };
                return true;
            }

            public IReadOnlyList<CreatureInstance> BuildParty(string trainerId, int levelOffset = 0)
            {
                return new List<CreatureInstance>
                {
                    CreatureFactory.Create(TestData.Rattata, 5, 1, _species, _moves),
                    CreatureFactory.Create(TestData.Pidgey, 5, 2, _species, _moves),
                };
            }
        }
    }
}
