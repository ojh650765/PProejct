using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// Pins the damage formula against numbers worked out by hand.
    ///
    /// Every expectation below is derived from the documented evaluation order in
    /// <see cref="DamageCalculator"/>: base → weather → crit → roll → STAB → type → burn.
    /// If one of these fails after a refactor, the formula changed, not the test.
    /// </summary>
    public sealed class DamageFormulaTests
    {
        private FakeSpeciesRegistry _species;
        private FakeMoveRegistry _moves;
        private FakeTypeChart _chart;

        [SetUp]
        public void SetUp()
        {
            _species = TestData.Species();
            _moves = TestData.Moves();
            _chart = new FakeTypeChart();
        }

        /// <summary>
        /// Level 10, Attack 30, Defence 20, 40 power physical, neutral, no STAB, top roll.
        /// (2*10/5+2)=6 → 6*40*30/20=360 → 360/50+2 = 9.
        /// </summary>
        [Test]
        public void BaseDamage_MatchesHandComputedValue()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(9));
        }

        /// <summary>The lowest of the sixteen rolls: floor(9 * 85 / 100) = 7.</summary>
        [Test]
        public void MinimumRoll_TruncatesTowardZero()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            ctx.RollPercent = DamageCalculator.MinRoll;
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(7));
        }

        /// <summary>A Normal move from a Normal type: floor(9 * 1.5) = 13.</summary>
        [Test]
        public void Stab_MultipliesByOneAndAHalf()
        {
            var ctx = Context(TestData.Rattata, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(13));
            Assert.That(ctx.Stab, Is.EqualTo(1.5f));
        }

        /// <summary>Fighting into Normal with no STAB: 9 * 2 = 18.</summary>
        [Test]
        public void SuperEffective_DoublesDamage()
        {
            // A 40-power Fighting move from a Normal type, so STAB stays out of the way.
            var ctx = Context(TestData.Rattata, TestData.Rattata, "tackle", level: 10, attack: 30, defense: 20);
            ctx.MoveType = ElementType.Fighting;
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(18));
            Assert.That(ctx.TypeMultiplier, Is.EqualTo(2f));
        }

        /// <summary>A crit applies before the roll: floor(9 * 1.5) = 13.</summary>
        [Test]
        public void CriticalHit_MultipliesByOneAndAHalf()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            ctx.Critical = true;
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(13));
        }

        /// <summary>Burn halves physical output last of all: floor(9 * 0.5) = 4.</summary>
        [Test]
        public void Burn_HalvesPhysicalDamage()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            ctx.Attacker.Status = StatusCondition.Burn;
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(4));
        }

        /// <summary>Burn does not touch special output.</summary>
        [Test]
        public void Burn_LeavesSpecialDamageAlone()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "water-gun", level: 10, attack: 30, defense: 20);
            var clean = DamageCalculator.Compute(ctx, _chart);

            ctx.ResetModifiers();
            ctx.Attacker.Status = StatusCondition.Burn;
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(clean));
        }

        /// <summary>Rain pushes Water and smothers Fire; sun does the reverse.</summary>
        [Test]
        public void Weather_ScalesMatchingMoveTypes()
        {
            Assert.That(DamageCalculator.WeatherMultiplier(Weather.Rain, ElementType.Water), Is.EqualTo(1.5f));
            Assert.That(DamageCalculator.WeatherMultiplier(Weather.Rain, ElementType.Fire), Is.EqualTo(0.5f));
            Assert.That(DamageCalculator.WeatherMultiplier(Weather.Sun, ElementType.Fire), Is.EqualTo(1.5f));
            Assert.That(DamageCalculator.WeatherMultiplier(Weather.Sun, ElementType.Water), Is.EqualTo(0.5f));
            Assert.That(DamageCalculator.WeatherMultiplier(Weather.Sandstorm, ElementType.Water), Is.EqualTo(1f));
        }

        /// <summary>Rain on Water Gun: base 9 → floor(9 * 1.5) = 13.</summary>
        [Test]
        public void Weather_AppliesBeforeTheRoll()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "water-gun", level: 10, attack: 30, defense: 20);
            ctx.Weather = Weather.Rain;
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(13));
        }

        /// <summary>A crit refuses the target's boosts but still honours its drops.</summary>
        [Test]
        public void CriticalHit_IgnoresTargetDefensiveBoosts()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            ctx.DefenderState.Stages[(int)StatKind.Defense] = 2;

            var boostedNormal = DamageCalculator.DefensiveStat(ctx, physical: true);
            ctx.Critical = true;
            var boostedCrit = DamageCalculator.DefensiveStat(ctx, physical: true);

            Assert.That(boostedNormal, Is.EqualTo(40));  // 20 * (2+2)/2
            Assert.That(boostedCrit, Is.EqualTo(20));    // boost ignored

            ctx.DefenderState.Stages[(int)StatKind.Defense] = -2;
            Assert.That(DamageCalculator.DefensiveStat(ctx, physical: true), Is.EqualTo(10)); // drop respected
        }

        /// <summary>A crit refuses the attacker's own drops but still honours its boosts.</summary>
        [Test]
        public void CriticalHit_IgnoresAttackerOffensiveDrops()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "tackle", level: 10, attack: 30, defense: 20);
            ctx.AttackerState.Stages[(int)StatKind.Attack] = -2;

            Assert.That(DamageCalculator.OffensiveStat(ctx, physical: true), Is.EqualTo(15)); // 30 * 2/4
            ctx.Critical = true;
            Assert.That(DamageCalculator.OffensiveStat(ctx, physical: true), Is.EqualTo(30)); // drop ignored

            ctx.AttackerState.Stages[(int)StatKind.Attack] = 2;
            Assert.That(DamageCalculator.OffensiveStat(ctx, physical: true), Is.EqualTo(60)); // boost kept
        }

        /// <summary>The base crit rate is 1/24, and CritStageBonus sharpens it.</summary>
        [Test]
        public void CritDenominator_FollowsMoveBonus()
        {
            Assert.That(DamageCalculator.CritDenominator(Move("tackle")), Is.EqualTo(24));
            Assert.That(DamageCalculator.CritDenominator(Move("karate-chop")), Is.EqualTo(8));
        }

        /// <summary>Damage is never zero against a target the move can affect at all.</summary>
        [Test]
        public void ResistedHit_StillDealsAtLeastOne()
        {
            var ctx = Context(TestData.Machop, TestData.Geodude, "tackle", level: 2, attack: 5, defense: 200);
            var damage = DamageCalculator.Compute(ctx, _chart);
            Assert.That(ctx.TypeMultiplier, Is.EqualTo(0.5f));
            Assert.That(damage, Is.GreaterThanOrEqualTo(1));
        }

        /// <summary>Special moves read SpAttack and SpDefense, not the physical pair.</summary>
        [Test]
        public void SpecialMoves_UseSpecialStats()
        {
            var ctx = Context(TestData.Machop, TestData.Machop, "water-gun", level: 10, attack: 30, defense: 20);
            ctx.Attacker.Stats[(int)StatKind.SpAttack] = 60;
            ctx.Defender.Stats[(int)StatKind.SpDefense] = 20;

            // 6 * 40 * 60 / 20 = 720 → 720/50 + 2 = 16.
            Assert.That(DamageCalculator.Compute(ctx, _chart), Is.EqualTo(16));
        }

        private MoveData Move(string id)
        {
            _moves.TryGet(id, out var move);
            return move;
        }

        /// <summary>
        /// Builds a damage context with explicit stats. Stats are set directly rather than
        /// derived so the arithmetic in each test stays checkable on paper.
        /// </summary>
        private DamageContext Context(int attackerSpeciesId, int defenderSpeciesId, string moveId,
            int level, int attack, int defense)
        {
            _species.TryGet(attackerSpeciesId, out var attackerSpecies);
            _species.TryGet(defenderSpeciesId, out var defenderSpecies);
            var move = Move(moveId);

            var attacker = new CreatureInstance
            {
                SpeciesId = attackerSpeciesId,
                Level = level,
                Stats = new[] { 100, attack, attack, attack, attack, attack },
                MaxHp = 100,
                CurrentHp = 100,
            };

            var defender = new CreatureInstance
            {
                SpeciesId = defenderSpeciesId,
                Level = level,
                Stats = new[] { 100, defense, defense, defense, defense, defense },
                MaxHp = 100,
                CurrentHp = 100,
            };

            return new DamageContext
            {
                Move = move,
                MoveType = move.Type,
                Attacker = attacker,
                Defender = defender,
                AttackerSpecies = attackerSpecies,
                DefenderSpecies = defenderSpecies,
                AttackerState = new BattleSideState { Side = BattleSide.Player },
                DefenderState = new BattleSideState { Side = BattleSide.Opponent },
                AttackerSide = BattleSide.Player,
                Weather = Weather.Clear,
                RollPercent = DamageCalculator.MaxRoll,
            };
        }
    }
}
