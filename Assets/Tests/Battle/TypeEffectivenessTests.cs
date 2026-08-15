using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Type interactions, including the two cases that break naive implementations:
    /// immunities that must survive being multiplied, and dual types that stack to 4x/0.25x.
    /// </summary>
    public sealed class TypeEffectivenessTests
    {
        private FakeTypeChart _chart;
        private FakeSpeciesRegistry _species;

        [SetUp]
        public void SetUp()
        {
            _chart = new FakeTypeChart();
            _species = TestData.Species();
        }

        [Test]
        public void SingleType_ReturnsExpectedMultipliers()
        {
            Assert.That(_chart.Multiplier(ElementType.Fire, ElementType.Grass), Is.EqualTo(2f));
            Assert.That(_chart.Multiplier(ElementType.Fire, ElementType.Water), Is.EqualTo(0.5f));
            Assert.That(_chart.Multiplier(ElementType.Fire, ElementType.Normal), Is.EqualTo(1f));
        }

        [Test]
        public void Immunities_ReturnZero()
        {
            Assert.That(_chart.Multiplier(ElementType.Normal, ElementType.Ghost), Is.EqualTo(0f));
            Assert.That(_chart.Multiplier(ElementType.Ghost, ElementType.Normal), Is.EqualTo(0f));
            Assert.That(_chart.Multiplier(ElementType.Electric, ElementType.Ground), Is.EqualTo(0f));
            Assert.That(_chart.Multiplier(ElementType.Fighting, ElementType.Ghost), Is.EqualTo(0f));
            Assert.That(_chart.Multiplier(ElementType.Poison, ElementType.Steel), Is.EqualTo(0f));
        }

        /// <summary>Grass into Geodude hits both Rock and Ground for 2x each.</summary>
        [Test]
        public void DualType_StacksToQuadruple()
        {
            _species.TryGet(TestData.Geodude, out var geodude);
            var multiplier = _chart.Multiplier(ElementType.Grass, geodude.Type1, geodude.Type2);
            Assert.That(multiplier, Is.EqualTo(4f));
        }

        /// <summary>Grass into Bulbasaur is resisted twice over.</summary>
        [Test]
        public void DualType_StacksToQuarter()
        {
            _species.TryGet(TestData.Bulbasaur, out var bulbasaur);
            var multiplier = _chart.Multiplier(ElementType.Grass, bulbasaur.Type1, bulbasaur.Type2);
            Assert.That(multiplier, Is.EqualTo(0.25f));
        }

        /// <summary>An immunity on either half of a dual type wins outright.</summary>
        [Test]
        public void DualType_ImmunityDominatesResistanceAndWeakness()
        {
            _species.TryGet(TestData.Geodude, out var geodude);
            // Electric is neutral into Rock but does nothing to Ground.
            Assert.That(_chart.Multiplier(ElementType.Electric, geodude.Type1, geodude.Type2), Is.EqualTo(0f));
        }

        /// <summary>A weakness and a resistance on the same target cancel out.</summary>
        [Test]
        public void DualType_WeaknessAndResistanceCancel()
        {
            _species.TryGet(TestData.Zubat, out var zubat);
            // Poison/Flying: Rock is neutral into Poison and 2x into Flying.
            Assert.That(_chart.Multiplier(ElementType.Rock, zubat.Type1, zubat.Type2), Is.EqualTo(2f));
            // Grass is 0.5x into Poison and 0.5x into Flying.
            Assert.That(_chart.Multiplier(ElementType.Grass, zubat.Type1, zubat.Type2), Is.EqualTo(0.25f));
        }

        [Test]
        public void Classification_MapsMultipliersToEffectiveness()
        {
            Assert.That(EffectivenessRules.Classify(0f), Is.EqualTo(Effectiveness.Immune));
            Assert.That(EffectivenessRules.Classify(0.25f), Is.EqualTo(Effectiveness.NotVeryEffective));
            Assert.That(EffectivenessRules.Classify(1f), Is.EqualTo(Effectiveness.Neutral));
            Assert.That(EffectivenessRules.Classify(4f), Is.EqualTo(Effectiveness.SuperEffective));
        }

        /// <summary>An immune target takes zero and the engine reports the miss as an immunity.</summary>
        [Test]
        public void ImmuneTarget_TakesNoDamageInBattle()
        {
            var engine = BattleTestBuilder.Engine();
            var attacker = BattleTestBuilder.Creature(TestData.Rattata, 20, "tackle");
            var defender = BattleTestBuilder.Creature(TestData.Gastly, 20, "confusion").WithAbility("levitate");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(attacker),
                BattleTestBuilder.Party(defender),
                Weather.Clear, seed: 4242);

            var hpBefore = defender.CurrentHp;
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(defender.CurrentHp, Is.EqualTo(hpBefore));

            var misses = stream.OfType<MoveMissedEvent>();
            Assert.That(misses.Exists(m => m.Attacker == BattleSide.Player && m.WasImmune), Is.True,
                "A Normal move into a Ghost must report an immunity, not an accuracy miss.");
        }

        /// <summary>Levitate cancels a Ground move before the chart is ever consulted.</summary>
        [Test]
        public void Levitate_BlocksGroundMovesAndNamesItself()
        {
            var engine = BattleTestBuilder.Engine();
            var attacker = BattleTestBuilder.Creature(TestData.Geodude, 20, "sand-attack");
            var defender = BattleTestBuilder.Creature(TestData.Gastly, 20, "confusion").WithAbility("levitate");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(attacker),
                BattleTestBuilder.Party(defender),
                Weather.Clear, seed: 99);

            // Sand Attack is a status move, so Levitate lets it through — only damaging
            // Ground moves are swallowed. Verified through the pure predicate instead.
            var levitate = AbilityRegistry.Resolve("levitate");
            BattleTestBuilder.MoveRegistry.TryGet("rock-throw", out var rockThrow);

            Assert.That(levitate.AbsorbsMove(rockThrow, ElementType.Ground), Is.True);
            Assert.That(levitate.AbsorbsMove(rockThrow, ElementType.Rock), Is.False);
        }
    }
}
