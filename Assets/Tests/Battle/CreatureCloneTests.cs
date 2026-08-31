using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// A copied creature is the SAME creature, and a re-derived one is not.
    ///
    /// <b>This is the test that was missing when PvP shipped.</b> Battle mode hands each
    /// machine its own team from the player profile and the other team through a trainer
    /// registry — and that registry rebuilt its creatures by calling
    /// <see cref="CreatureFactory.Create"/> again with a different seed. Re-derivation only
    /// reproduces what the seed produced, so the copies came back with different IVs,
    /// different stats, and neither the moves the player had been taught nor the stars they
    /// had paid for.
    ///
    /// In a single-player battle that is invisible. In a lockstep match it is fatal: the two
    /// engines then disagree about every creature on the field, the desync fingerprint catches
    /// it on the first turn, and the match is stopped — which the result screen reports as a
    /// defeat. Both players chose a move and were immediately told they had lost.
    /// </summary>
    public sealed class CreatureCloneTests
    {
        private static CreatureInstance Taught()
        {
            var creature = BattleTestBuilder.Creature(TestData.Charmander, 30, "ember", "scratch");

            // The two things a re-derivation cannot know about: a moveset the player chose,
            // and stats somebody paid to raise.
            creature.Stats[(int)StatKind.Attack] += 37;
            creature.MaxHp += 21;
            creature.CurrentHp = creature.MaxHp;
            creature.Nickname = "불꽃이";
            return creature;
        }

        [Test]
        public void Clone_IsIdenticalInEveryFieldTheEngineReads()
        {
            var source = Taught();
            var copy = CreatureFactory.Clone(source);

            Assert.That(copy.SpeciesId, Is.EqualTo(source.SpeciesId));
            Assert.That(copy.Level, Is.EqualTo(source.Level));
            Assert.That(copy.MaxHp, Is.EqualTo(source.MaxHp), "Max HP is in the desync fingerprint.");
            Assert.That(copy.CurrentHp, Is.EqualTo(source.CurrentHp), "So is current HP.");
            Assert.That(copy.Stats, Is.EqualTo(source.Stats), "Stats decide damage on both machines.");
            Assert.That(copy.Ivs, Is.EqualTo(source.Ivs));
            Assert.That(copy.AbilityId, Is.EqualTo(source.AbilityId));
            Assert.That(copy.Nickname, Is.EqualTo(source.Nickname));
            Assert.That(copy.InstanceId, Is.EqualTo(source.InstanceId));

            Assert.That(copy.Moves.Count, Is.EqualTo(source.Moves.Count),
                "A team that lost its taught moveset is a different team.");
            for (var i = 0; i < source.Moves.Count; i++)
                Assert.That(copy.Moves[i].MoveId, Is.EqualTo(source.Moves[i].MoveId));
        }

        /// <summary>
        /// The other half of the contract. Identical is not enough — it must also be a
        /// separate object, or the second battle of a session opens against a party that the
        /// first battle already knocked out.
        /// </summary>
        [Test]
        public void Clone_SharesNothingMutableWithItsSource()
        {
            var source = Taught();
            var copy = CreatureFactory.Clone(source);

            Assert.That(copy, Is.Not.SameAs(source));
            Assert.That(copy.Stats, Is.Not.SameAs(source.Stats));
            Assert.That(copy.Ivs, Is.Not.SameAs(source.Ivs));
            Assert.That(copy.Moves, Is.Not.SameAs(source.Moves));

            copy.CurrentHp = 0;
            copy.Stats[(int)StatKind.Attack] = 1;
            copy.Moves.Clear();

            Assert.That(source.CurrentHp, Is.GreaterThan(0), "Fainting the copy fainted the original.");
            Assert.That(source.Stats[(int)StatKind.Attack], Is.Not.EqualTo(1));
            Assert.That(source.Moves.Count, Is.GreaterThan(0));
        }

        /// <summary>
        /// The specific mistake, stated as a test: two seeds are two different creatures.
        ///
        /// <b>Derived IVs, deliberately.</b> The shared fixture builds with <c>perfectIvs</c>,
        /// under which the seed genuinely does not touch the stats — so a version of this test
        /// written on top of it would pass while proving nothing. Battle mode builds parties
        /// with derived IVs, which is the case that matters and the case that broke.
        /// </summary>
        [Test]
        public void TwoSeeds_AreTwoDifferentCreatures()
        {
            // Engine() is what installs the registries CreatureFactory reads.
            BattleTestBuilder.Engine();

            var first = CreatureFactory.Create(TestData.Charmander, 30, 12345,
                BattleTestBuilder.SpeciesRegistry, BattleTestBuilder.MoveRegistry);
            var second = CreatureFactory.Create(TestData.Charmander, 30, 67890,
                BattleTestBuilder.SpeciesRegistry, BattleTestBuilder.MoveRegistry);

            Assert.That(second.Ivs, Is.Not.EqualTo(first.Ivs),
                "Two seeds produced the same IVs, so re-deriving a party would have been safe. " +
                "It was not: this is why both machines in a PvP match have to COPY a team " +
                "rather than rebuild it.");
            Assert.That(second.MaxHp != first.MaxHp || !SameStats(second.Stats, first.Stats), Is.True,
                "Different IVs did not reach the stats, which would mean the desync had some " +
                "other cause than the one CreatureFactory.Clone was written for.");
        }

        private static bool SameStats(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
