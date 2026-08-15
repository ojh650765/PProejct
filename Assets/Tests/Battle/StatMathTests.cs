using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Derived stats, the experience curve and the creature factory. Stats must be derived
    /// from base values, IVs and level rather than authored, so these pin the arithmetic.
    /// </summary>
    public sealed class StatMathTests
    {
        private FakeSpeciesRegistry _species;
        private FakeMoveRegistry _moves;

        [SetUp]
        public void SetUp()
        {
            _species = TestData.Species();
            _moves = TestData.Moves();
        }

        /// <summary>HP: ((2*base + iv) * level)/100 + level + 10.</summary>
        [Test]
        public void HpFormula_MatchesHandComputedValues()
        {
            // Machop base HP 70, perfect IV, level 50: ((140+31)*50)/100 + 50 + 10 = 85 + 60.
            Assert.That(StatMath.ComputeHp(70, 31, 50), Is.EqualTo(145));
            // Level 1 floors to the +11 minimum.
            Assert.That(StatMath.ComputeHp(70, 31, 1), Is.EqualTo((171 * 1) / 100 + 11));
            Assert.That(StatMath.ComputeHp(35, 0, 100), Is.EqualTo(180));
        }

        /// <summary>Other stats: ((2*base + iv) * level)/100 + 5.</summary>
        [Test]
        public void StatFormula_MatchesHandComputedValues()
        {
            // Pikachu base Speed 90, perfect IV, level 50: ((180+31)*50)/100 + 5 = 105 + 5.
            Assert.That(StatMath.ComputeStat(90, 31, 50), Is.EqualTo(110));
            Assert.That(StatMath.ComputeStat(80, 31, 50), Is.EqualTo(100));
            Assert.That(StatMath.ComputeStat(50, 0, 100), Is.EqualTo(105));
        }

        [Test]
        public void Stats_GrowMonotonicallyWithLevelAndIvs()
        {
            var previous = 0;
            for (var level = 1; level <= 100; level++)
            {
                var value = StatMath.ComputeStat(70, 31, level);
                Assert.That(value, Is.GreaterThanOrEqualTo(previous));
                previous = value;
            }

            Assert.That(StatMath.ComputeStat(70, 31, 50), Is.GreaterThan(StatMath.ComputeStat(70, 0, 50)));
        }

        [Test]
        public void ExperienceCurve_IsCubicAndInvertible()
        {
            Assert.That(StatMath.ExperienceForLevel(1), Is.EqualTo(0));
            Assert.That(StatMath.ExperienceForLevel(10), Is.EqualTo(1000));
            Assert.That(StatMath.ExperienceForLevel(50), Is.EqualTo(125000));

            for (var level = 1; level <= 100; level++)
                Assert.That(StatMath.LevelForExperience(StatMath.ExperienceForLevel(level)), Is.EqualTo(level));

            Assert.That(StatMath.LevelForExperience(999), Is.EqualTo(9));
        }

        [Test]
        public void ExperienceAward_ScalesWithLevelAndSplitsAcrossParticipants()
        {
            var solo = StatMath.ExperienceFromDefeat(64, 20, 1, isTrainerBattle: false);
            var shared = StatMath.ExperienceFromDefeat(64, 20, 2, isTrainerBattle: false);
            var trainer = StatMath.ExperienceFromDefeat(64, 20, 1, isTrainerBattle: true);
            var higher = StatMath.ExperienceFromDefeat(64, 40, 1, isTrainerBattle: false);

            Assert.That(solo, Is.EqualTo(64 * 20 / 7));
            Assert.That(shared, Is.EqualTo(solo / 2));
            Assert.That(trainer, Is.EqualTo(solo * 3 / 2));
            Assert.That(higher, Is.GreaterThan(solo));
            Assert.That(StatMath.ExperienceFromDefeat(0, 20, 1, false), Is.EqualTo(0));
        }

        /// <summary>The factory derives stats rather than trusting whatever was on the object.</summary>
        [Test]
        public void Factory_DerivesStatsAndFullHp()
        {
            var machop = CreatureFactory.Create(TestData.Machop, 50, seed: 1, _species, _moves, perfectIvs: true);

            Assert.That(machop.MaxHp, Is.EqualTo(StatMath.ComputeHp(70, 31, 50)));
            Assert.That(machop.CurrentHp, Is.EqualTo(machop.MaxHp));
            Assert.That(machop.Stats[(int)StatKind.Attack], Is.EqualTo(StatMath.ComputeStat(80, 31, 50)));
            Assert.That(machop.Stats[(int)StatKind.Speed], Is.EqualTo(StatMath.ComputeStat(35, 31, 50)));
            Assert.That(machop.Experience, Is.EqualTo(StatMath.ExperienceForLevel(50)));
        }

        [Test]
        public void Factory_FillsMovesAndAbilityFromTheDex()
        {
            var pikachu = CreatureFactory.Create(TestData.Pikachu, 20, seed: 2, _species, _moves);

            Assert.That(pikachu.Moves.Count, Is.InRange(1, 4));
            Assert.That(pikachu.Moves.TrueForAll(m => m.CurrentPp == m.MaxPp && m.MaxPp > 0), Is.True);
            Assert.That(pikachu.AbilityId, Is.EqualTo("static").Or.EqualTo("lightning-rod"));
        }

        [Test]
        public void Factory_ClampsLevelAndRollsIvsInRange()
        {
            var overLevelled = CreatureFactory.Create(TestData.Rattata, 500, seed: 3, _species, _moves);
            var underLevelled = CreatureFactory.Create(TestData.Rattata, -4, seed: 3, _species, _moves);

            Assert.That(overLevelled.Level, Is.EqualTo(StatMath.MaxLevel));
            Assert.That(underLevelled.Level, Is.EqualTo(1));

            var rolled = CreatureFactory.Create(TestData.Rattata, 20, seed: 5, _species, _moves);
            for (var i = 0; i < StatKinds.BaseCount; i++)
                Assert.That(rolled.Ivs[i], Is.InRange(0, CreatureFactory.MaxIv));
        }

        [Test]
        public void Factory_ThrowsOnAnUnknownSpecies()
        {
            Assert.Throws<System.InvalidOperationException>(
                () => CreatureFactory.Create(999999, 5, seed: 1, _species, _moves));
        }

        /// <summary>A level-up keeps the damage already taken rather than quietly healing.</summary>
        [Test]
        public void RecomputeStats_PreservesDamageTaken()
        {
            _species.TryGet(TestData.Machop, out var machopSpecies);
            var machop = CreatureFactory.Create(TestData.Machop, 10, seed: 1, _species, _moves, perfectIvs: true);

            machop.CurrentHp = machop.MaxHp - 7;
            var oldMax = machop.MaxHp;

            machop.Level = 11;
            CreatureFactory.RecomputeStats(machop, machopSpecies);

            var gained = machop.MaxHp - oldMax;
            Assert.That(machop.MaxHp - machop.CurrentHp, Is.EqualTo(7),
                $"The level-up gained {gained} HP and should have carried the 7 points of damage across.");
        }

        /// <summary>A fainted creature stays fainted through a stat recompute.</summary>
        [Test]
        public void RecomputeStats_LeavesAFaintedCreatureFainted()
        {
            _species.TryGet(TestData.Machop, out var machopSpecies);
            var machop = CreatureFactory.Create(TestData.Machop, 10, seed: 1, _species, _moves);

            machop.CurrentHp = 0;
            machop.Level = 20;
            CreatureFactory.RecomputeStats(machop, machopSpecies);

            Assert.That(machop.CurrentHp, Is.EqualTo(0));
            Assert.That(machop.IsFainted, Is.True);
        }

        [Test]
        public void RestorePp_RefillsEverySlot()
        {
            var creature = CreatureFactory.Create(TestData.Pikachu, 20, seed: 7, _species, _moves);

            for (var i = 0; i < creature.Moves.Count; i++)
            {
                var slot = creature.Moves[i];
                slot.CurrentPp = 0;
                creature.Moves[i] = slot;
            }

            CreatureFactory.RestorePp(creature);

            Assert.That(creature.Moves.TrueForAll(m => m.CurrentPp == m.MaxPp), Is.True);
        }
    }
}
