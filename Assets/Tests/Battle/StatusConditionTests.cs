using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Every non-volatile status and every volatile, checked for its effect and for the
    /// way it expires. Statuses are applied directly and both sides are given a harmless
    /// status move, so the only HP movement in the stream is the one under test.
    /// </summary>
    public sealed class StatusConditionTests
    {
        private BattleEngine _engine;
        private CreatureInstance _player;
        private CreatureInstance _opponent;

        /// <summary>
        /// Sets up a battle where neither side can deal move damage: both hold Growl, so
        /// end-of-turn arithmetic is the only thing moving the HP bars.
        /// </summary>
        private void Quiet(int seed = 1234, Weather weather = Weather.Clear,
            int playerSpecies = TestData.Machop, int opponentSpecies = TestData.Machop)
        {
            _engine = BattleTestBuilder.Engine();
            // Abilities are cleared so each test measures the mechanic and not, say, the
            // No Guard the factory may have rolled onto a Machop.
            _player = BattleTestBuilder.Creature(playerSpecies, 50, "growl").WithAbility(null);
            _opponent = BattleTestBuilder.Creature(opponentSpecies, 50, "growl").WithAbility(null);
            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                weather, seed);
        }

        private IReadOnlyList<BattleEvent> Turn() => _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

        [Test]
        public void Burn_ChipsOneSixteenthEachTurn()
        {
            Quiet();
            _player.Status = StatusCondition.Burn;
            var expected = _player.MaxHp - _player.MaxHp / 16;

            Turn();

            Assert.That(_player.CurrentHp, Is.EqualTo(expected));
        }

        [Test]
        public void Poison_ChipsOneEighthEachTurn()
        {
            Quiet();
            _player.Status = StatusCondition.Poison;
            var expected = _player.MaxHp - _player.MaxHp / 8;

            Turn();

            Assert.That(_player.CurrentHp, Is.EqualTo(expected));
        }

        /// <summary>Bad poison escalates: 1/16, then 2/16, then 3/16 of max HP.</summary>
        [Test]
        public void BadPoison_Escalates()
        {
            Quiet();
            _player.Status = StatusCondition.BadPoison;
            _player.StatusCounter = 0;
            var max = _player.MaxHp;

            Turn();
            var afterFirst = max - max * 1 / 16;
            Assert.That(_player.CurrentHp, Is.EqualTo(afterFirst));

            Turn();
            var afterSecond = afterFirst - max * 2 / 16;
            Assert.That(_player.CurrentHp, Is.EqualTo(afterSecond));

            Turn();
            var afterThird = afterSecond - max * 3 / 16;
            Assert.That(_player.CurrentHp, Is.EqualTo(afterThird));
        }

        /// <summary>Paralysis halves Speed, which is enough to flip a turn order.</summary>
        [Test]
        public void Paralysis_HalvesSpeed()
        {
            Quiet(playerSpecies: TestData.Pikachu);
            var clean = _engine.EffectiveSpeed(BattleSide.Player);

            _player.Status = StatusCondition.Paralysis;
            var paralysed = _engine.EffectiveSpeed(BattleSide.Player);

            Assert.That(paralysed, Is.EqualTo(clean * 0.5f).Within(0.001f));
        }

        /// <summary>Paralysis stops the creature outright some of the time.</summary>
        [Test]
        public void Paralysis_SometimesPreventsActing()
        {
            Quiet(seed: 77);
            _player.Status = StatusCondition.Paralysis;

            var blocked = 0;
            for (var i = 0; i < 60; i++)
            {
                var stream = Turn();
                foreach (var evt in stream)
                    if (evt is MessageEvent message && message.Text.Contains("paralysed")) blocked++;
            }

            Assert.That(blocked, Is.GreaterThan(0), "A 25% full stop over 60 turns should fire at least once.");
            Assert.That(blocked, Is.LessThan(45), "…but it must not fire almost every turn.");
        }

        /// <summary>Sleep costs one to three turns and then wears off on its own.</summary>
        [Test]
        public void Sleep_CostsOneToThreeTurnsThenExpires()
        {
            for (var seed = 0; seed < 16; seed++)
            {
                Quiet(seed: seed);
                _engine.TryApplyStatus(BattleSide.Player, StatusCondition.Sleep, "test");
                Assert.That(_player.Status, Is.EqualTo(StatusCondition.Sleep));

                var missedTurns = 0;
                for (var turn = 1; turn <= 6; turn++)
                {
                    var stream = Turn();
                    if (!stream.OfType<MoveDeclaredEvent>().Exists(e => e.Side == BattleSide.Player)) missedTurns++;
                    if (_player.Status == StatusCondition.None) break;
                }

                Assert.That(_player.Status, Is.EqualTo(StatusCondition.None), $"Seed {seed} never woke up.");
                Assert.That(missedTurns, Is.InRange(1, 3),
                    $"Seed {seed} slept through {missedTurns} turns, outside the 1-3 range.");
            }
        }

        /// <summary>A sleeping creature does not get to declare a move.</summary>
        [Test]
        public void Sleep_PreventsActing()
        {
            Quiet(seed: 5);
            _player.Status = StatusCondition.Sleep;
            _player.StatusCounter = 3;

            var stream = Turn();

            Assert.That(stream.OfType<MoveDeclaredEvent>().Exists(e => e.Side == BattleSide.Player), Is.False);
        }

        /// <summary>A frozen creature is stuck until it thaws, and it always thaws eventually.</summary>
        [Test]
        public void Freeze_BlocksActingUntilItThaws()
        {
            Quiet(seed: 31);
            _player.Status = StatusCondition.Freeze;

            var thawedOn = -1;
            for (var turn = 1; turn <= 60 && thawedOn < 0; turn++)
            {
                Turn();
                if (_player.Status == StatusCondition.None) thawedOn = turn;
            }

            Assert.That(thawedOn, Is.GreaterThan(0), "A 20% thaw chance must resolve within 60 turns.");
        }

        /// <summary>Only one non-volatile status at a time.</summary>
        [Test]
        public void Status_DoesNotOverwriteAnExistingOne()
        {
            Quiet();
            Assert.That(_engine.TryApplyStatus(BattleSide.Player, StatusCondition.Burn, "test"), Is.True);
            Assert.That(_engine.TryApplyStatus(BattleSide.Player, StatusCondition.Paralysis, "test"), Is.False);
            Assert.That(_player.Status, Is.EqualTo(StatusCondition.Burn));
        }

        /// <summary>Type immunities to status: Fire never burns, Electric never paralyses.</summary>
        [Test]
        public void Status_RespectsTypeImmunities()
        {
            Quiet(playerSpecies: TestData.Charmander);
            Assert.That(_engine.TryApplyStatus(BattleSide.Player, StatusCondition.Burn, "test"), Is.False);
            Assert.That(_player.Status, Is.EqualTo(StatusCondition.None));

            Quiet(playerSpecies: TestData.Pikachu);
            Assert.That(_engine.TryApplyStatus(BattleSide.Player, StatusCondition.Paralysis, "test"), Is.False);

            Quiet(playerSpecies: TestData.Zubat);
            Assert.That(_engine.TryApplyStatus(BattleSide.Player, StatusCondition.Poison, "test"), Is.False);
        }

        /// <summary>A status inflicted by a move is announced with its before and after values.</summary>
        [Test]
        public void Status_EmitsStatusChangedEvent()
        {
            _engine = BattleTestBuilder.Engine();
            _player = BattleTestBuilder.Creature(TestData.Pikachu, 50, "thunder-wave").WithAbility(null);
            _opponent = BattleTestBuilder.Creature(TestData.Machop, 50, "growl").WithAbility(null);
            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                Weather.Clear, seed: 616);

            StatusChangedEvent change = null;
            for (var turn = 0; turn < 8 && change == null; turn++)
            {
                var stream = _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                var changes = stream.OfType<StatusChangedEvent>();
                if (changes.Count > 0) change = changes[0];
            }

            Assert.That(change, Is.Not.Null, "Thunder Wave at 90% accuracy should land within eight turns.");
            Assert.That(change.Target, Is.EqualTo(BattleSide.Opponent));
            Assert.That(change.Previous, Is.EqualTo(StatusCondition.None));
            Assert.That(change.Current, Is.EqualTo(StatusCondition.Paralysis));
        }

        /// <summary>Leech seed drains an eighth of the seeded creature's HP into its seeder.</summary>
        [Test]
        public void LeechSeed_DrainsToTheSeeder()
        {
            _engine = BattleTestBuilder.Engine();
            _player = BattleTestBuilder.Creature(TestData.Bulbasaur, 50, "leech-seed");
            _opponent = BattleTestBuilder.Creature(TestData.Machop, 50, "growl");
            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                Weather.Clear, seed: 808);

            for (var attempt = 0; attempt < 8 && (_engine.State.VolatilesOf(BattleSide.Opponent) & VolatileFlags.LeechSeeded) == 0; attempt++)
                _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(_engine.State.VolatilesOf(BattleSide.Opponent) & VolatileFlags.LeechSeeded,
                Is.EqualTo(VolatileFlags.LeechSeeded), "Leech Seed at 90% accuracy should land within eight tries.");

            // Only now open a gap for the drain to heal into, so the transfer is not clipped.
            _player.CurrentHp = _player.MaxHp / 2;
            var opponentBefore = _opponent.CurrentHp;
            var playerBefore = _player.CurrentHp;
            _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            var drained = opponentBefore - _opponent.CurrentHp;
            Assert.That(drained, Is.EqualTo(_opponent.MaxHp / 8));
            Assert.That(_player.CurrentHp - playerBefore, Is.EqualTo(drained));
        }

        /// <summary>A Grass type cannot be seeded.</summary>
        [Test]
        public void LeechSeed_DoesNotAffectGrassTypes()
        {
            _engine = BattleTestBuilder.Engine();
            _player = BattleTestBuilder.Creature(TestData.Bulbasaur, 50, "leech-seed");
            _opponent = BattleTestBuilder.Creature(TestData.Oddish, 50, "growl");
            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                Weather.Clear, seed: 12);

            for (var i = 0; i < 10; i++) _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(_engine.State.VolatilesOf(BattleSide.Opponent) & VolatileFlags.LeechSeeded,
                Is.EqualTo(VolatileFlags.None));
        }

        /// <summary>Confusion expires after one to four turns, and can make the user hit itself.</summary>
        [Test]
        public void Confusion_ExpiresAndSometimesSelfHits()
        {
            var selfHits = 0;
            var expiries = 0;

            for (var seed = 0; seed < 24; seed++)
            {
                _engine = BattleTestBuilder.Engine();
                _player = BattleTestBuilder.Creature(TestData.Machop, 50, "growl");
                _opponent = BattleTestBuilder.Creature(TestData.Gastly, 50, "confuse-ray");
                _engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(_player),
                    BattleTestBuilder.Party(_opponent),
                    Weather.Clear, seed);

                for (var turn = 0; turn < 8; turn++)
                {
                    var stream = _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                    foreach (var evt in stream)
                    {
                        if (evt is MessageEvent m && m.Text == "It hurt itself in its confusion!") selfHits++;
                        if (evt is VolatileChangedEvent v && (v.Removed & VolatileFlags.Confused) != 0) expiries++;
                    }
                }
            }

            Assert.That(selfHits, Is.GreaterThan(0), "Confusion should produce self-hits across 24 seeds.");
            Assert.That(expiries, Is.GreaterThan(0), "Confusion must expire, not persist forever.");
        }

        /// <summary>Sandstorm chips everything that is not Rock, Ground or Steel.</summary>
        [Test]
        public void Sandstorm_ChipsNonImmuneTypes()
        {
            Quiet(weather: Weather.Sandstorm, playerSpecies: TestData.Machop, opponentSpecies: TestData.Geodude);
            var playerExpected = _player.MaxHp - _player.MaxHp / 16;
            var opponentBefore = _opponent.CurrentHp;

            Turn();

            Assert.That(_player.CurrentHp, Is.EqualTo(playerExpected));
            Assert.That(_opponent.CurrentHp, Is.EqualTo(opponentBefore), "Geodude is Rock/Ground and takes no sand.");
        }

        /// <summary>Hail chips everything that is not Ice.</summary>
        [Test]
        public void Hail_ChipsNonIceTypes()
        {
            Quiet(weather: Weather.Hail);
            var expected = _player.MaxHp - _player.MaxHp / 16;

            Turn();

            Assert.That(_player.CurrentHp, Is.EqualTo(expected));
        }

        /// <summary>Protect stops the incoming move outright.</summary>
        [Test]
        public void Protect_BlocksTheIncomingMove()
        {
            _engine = BattleTestBuilder.Engine();
            _player = BattleTestBuilder.Creature(TestData.Squirtle, 50, "protect");
            _opponent = BattleTestBuilder.Creature(TestData.Machop, 50, "karate-chop");
            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                Weather.Clear, seed: 300);

            var before = _player.CurrentHp;
            _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(_player.CurrentHp, Is.EqualTo(before));
        }

        /// <summary>Consecutive Protects decay, so spamming it eventually fails.</summary>
        [Test]
        public void Protect_DecaysOnConsecutiveUse()
        {
            _engine = BattleTestBuilder.Engine();
            _player = BattleTestBuilder.Creature(TestData.Squirtle, 50, "protect");
            _opponent = BattleTestBuilder.Creature(TestData.Machop, 50, "karate-chop");
            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                Weather.Clear, seed: 77);

            var failures = 0;
            for (var turn = 0; turn < 8; turn++)
            {
                var stream = _engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                foreach (var evt in stream)
                    if (evt is MessageEvent m && m.Text == "But it failed!") failures++;
            }

            Assert.That(failures, Is.GreaterThan(0), "Eight consecutive Protects must not all succeed.");
        }
    }
}
