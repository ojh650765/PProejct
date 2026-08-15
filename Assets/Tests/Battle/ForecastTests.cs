using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// The tactical layer calls ForecastMove for every move of every party member on every
    /// state change. It must therefore be RNG-free, side-effect-free and honest about its
    /// bounds — these tests hold it to all three.
    /// </summary>
    public sealed class ForecastTests
    {
        private BattleEngine _engine;
        private CreatureInstance _player;
        private CreatureInstance _opponent;

        [SetUp]
        public void SetUp()
        {
            _engine = BattleTestBuilder.Engine();
            _player = BattleTestBuilder.Creature(TestData.Charmander, 25, "ember", "scratch", "growl").WithAbility(null);
            _opponent = BattleTestBuilder.Creature(TestData.Oddish, 25, "absorb", "sleep-powder").WithAbility(null);

            _engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(_player),
                BattleTestBuilder.Party(_opponent),
                Weather.Clear, seed: 1000);
        }

        /// <summary>Calling it twice returns exactly the same numbers.</summary>
        [Test]
        public void Forecast_IsRepeatable()
        {
            var first = _engine.ForecastMove(BattleSide.Player, 0);
            for (var i = 0; i < 20; i++)
            {
                var again = _engine.ForecastMove(BattleSide.Player, 0);
                Assert.That(again.MinFraction, Is.EqualTo(first.MinFraction));
                Assert.That(again.MaxFraction, Is.EqualTo(first.MaxFraction));
                Assert.That(again.ExpectedFraction, Is.EqualTo(first.ExpectedFraction));
                Assert.That(again.HitChance, Is.EqualTo(first.HitChance));
                Assert.That(again.TypeMultiplier, Is.EqualTo(first.TypeMultiplier));
                Assert.That(again.TurnsToKo, Is.EqualTo(first.TurnsToKo));
            }
        }

        /// <summary>It draws no random numbers and moves no HP.</summary>
        [Test]
        public void Forecast_TouchesNeitherStateNorRandom()
        {
            var drawsBefore = _engine.Random.DrawCount;
            var playerHp = _player.CurrentHp;
            var opponentHp = _opponent.CurrentHp;
            var stageBefore = _engine.State.StatStagesOf(BattleSide.Opponent)[(int)StatKind.Defense];

            for (var move = 0; move < 3; move++)
            {
                _engine.ForecastMove(BattleSide.Player, move);
                _engine.ForecastMove(BattleSide.Opponent, move);
            }

            Assert.That(_engine.Random.DrawCount, Is.EqualTo(drawsBefore), "The forecast consumed RNG draws.");
            Assert.That(_player.CurrentHp, Is.EqualTo(playerHp));
            Assert.That(_opponent.CurrentHp, Is.EqualTo(opponentHp));
            Assert.That(_engine.State.StatStagesOf(BattleSide.Opponent)[(int)StatKind.Defense], Is.EqualTo(stageBefore));
            Assert.That(_player.Moves[0].CurrentPp, Is.EqualTo(_player.Moves[0].MaxPp), "The forecast spent PP.");
        }

        /// <summary>Min ≤ expected ≤ max, and all three are real fractions of max HP.</summary>
        [Test]
        public void Forecast_BoundsAreOrdered()
        {
            var forecast = _engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(forecast.MinFraction, Is.LessThanOrEqualTo(forecast.ExpectedFraction));
            Assert.That(forecast.ExpectedFraction, Is.LessThanOrEqualTo(forecast.MaxFraction));
            Assert.That(forecast.MinFraction, Is.GreaterThan(0f));
            Assert.That(forecast.HitChance, Is.InRange(0f, 1f));
        }

        /// <summary>The reported bounds bracket what actually happens in battle.</summary>
        [Test]
        public void Forecast_BracketsRealDamage()
        {
            var forecast = _engine.ForecastMove(BattleSide.Player, 0);
            var maxHp = _opponent.MaxHp;

            // Reasonable slack for the crit that MaxFraction already accounts for.
            var lowerBound = (int)(forecast.MinFraction * maxHp) - 1;
            var upperBound = (int)(forecast.MaxFraction * maxHp) + 1;

            for (var seed = 0; seed < 40; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                var attacker = BattleTestBuilder.Creature(TestData.Charmander, 25, "ember").WithAbility(null);
                var target = BattleTestBuilder.Creature(TestData.Oddish, 25, "sleep-powder").WithAbility(null);

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(attacker),
                    BattleTestBuilder.Party(target),
                    Weather.Clear, seed);

                var before = target.CurrentHp;
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

                foreach (var evt in stream)
                {
                    if (!(evt is DamageDealtEvent damage)) continue;
                    if (damage.Target != BattleSide.Opponent || damage.IndirectSourceId != null) continue;

                    Assert.That(damage.Amount, Is.InRange(lowerBound, upperBound),
                        $"Seed {seed} dealt {damage.Amount}, outside the forecast range [{lowerBound}, {upperBound}].");
                }

                Assert.That(before, Is.GreaterThan(0));
            }
        }

        /// <summary>The type multiplier is reported, not just folded into the damage.</summary>
        [Test]
        public void Forecast_ReportsTypeMultiplierAndEffectiveness()
        {
            // Fire into Grass/Poison is 2x from the Grass half and neutral from Poison.
            var forecast = _engine.ForecastMove(BattleSide.Player, 0);
            Assert.That(forecast.TypeMultiplier, Is.EqualTo(2f));
            Assert.That(forecast.Effectiveness, Is.EqualTo(Effectiveness.SuperEffective));

            // Scratch is Normal, which Oddish neither resists nor is weak to.
            var neutral = _engine.ForecastMove(BattleSide.Player, 1);
            Assert.That(neutral.TypeMultiplier, Is.EqualTo(1f));
            Assert.That(neutral.Effectiveness, Is.EqualTo(Effectiveness.Neutral));
        }

        /// <summary>A status move forecasts as zero damage but keeps its honest hit chance.</summary>
        [Test]
        public void Forecast_StatusMoveReportsNoDamage()
        {
            var forecast = _engine.ForecastMove(BattleSide.Player, 2);

            Assert.That(forecast.MinFraction, Is.EqualTo(0f));
            Assert.That(forecast.MaxFraction, Is.EqualTo(0f));
            Assert.That(forecast.ExpectedFraction, Is.EqualTo(0f));
            Assert.That(forecast.TurnsToKo, Is.EqualTo(int.MaxValue));
            Assert.That(forecast.HitChance, Is.EqualTo(1f), "Growl has 100 accuracy.");
        }

        /// <summary>An immune target forecasts as zero across the board.</summary>
        [Test]
        public void Forecast_ImmuneTargetIsZero()
        {
            var engine = BattleTestBuilder.Engine();
            var attacker = BattleTestBuilder.Creature(TestData.Rattata, 25, "tackle").WithAbility(null);
            var ghost = BattleTestBuilder.Creature(TestData.Gastly, 25, "confusion").WithAbility("levitate");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(attacker),
                BattleTestBuilder.Party(ghost),
                Weather.Clear, seed: 1);

            var forecast = engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(forecast.TypeMultiplier, Is.EqualTo(0f));
            Assert.That(forecast.Effectiveness, Is.EqualTo(Effectiveness.Immune));
            Assert.That(forecast.ExpectedFraction, Is.EqualTo(0f));
            Assert.That(forecast.TurnsToKo, Is.EqualTo(int.MaxValue));
        }

        /// <summary>An ability that swallows the move reports zero, not a neutral multiplier.</summary>
        [Test]
        public void Forecast_HonoursAbsorbingAbilities()
        {
            var engine = BattleTestBuilder.Engine();
            var attacker = BattleTestBuilder.Creature(TestData.Pikachu, 25, "thunder-shock").WithAbility(null);
            var rod = BattleTestBuilder.Creature(TestData.Pikachu, 25, "quick-attack").WithAbility("lightning-rod");

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(attacker),
                BattleTestBuilder.Party(rod),
                Weather.Clear, seed: 2);

            var forecast = engine.ForecastMove(BattleSide.Player, 0);
            Assert.That(forecast.TypeMultiplier, Is.EqualTo(0f));
            Assert.That(forecast.ExpectedFraction, Is.EqualTo(0f));
        }

        /// <summary>Turns to KO counts down as the target is worn away.</summary>
        [Test]
        public void Forecast_TurnsToKoFallsWithTargetHp()
        {
            var full = _engine.ForecastMove(BattleSide.Player, 0);
            Assert.That(full.TurnsToKo, Is.GreaterThan(0));
            Assert.That(full.TurnsToKo, Is.LessThan(int.MaxValue));

            _opponent.CurrentHp = 1;
            var nearlyDead = _engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(nearlyDead.TurnsToKo, Is.EqualTo(1));
            Assert.That(nearlyDead.TurnsToKo, Is.LessThanOrEqualTo(full.TurnsToKo));
        }

        /// <summary>Weather feeds through to the forecast the same way it feeds through to damage.</summary>
        [Test]
        public void Forecast_ReflectsWeather()
        {
            var clear = _engine.ForecastMove(BattleSide.Player, 0);
            _engine.SetWeather(Weather.Sun);
            var sunny = _engine.ForecastMove(BattleSide.Player, 0);
            _engine.SetWeather(Weather.Rain);
            var rainy = _engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(sunny.ExpectedFraction, Is.GreaterThan(clear.ExpectedFraction));
            Assert.That(rainy.ExpectedFraction, Is.LessThan(clear.ExpectedFraction));
        }

        /// <summary>An out-of-range index is a query, not a crash.</summary>
        [Test]
        public void Forecast_ToleratesAnInvalidIndex()
        {
            Assert.DoesNotThrow(() => _engine.ForecastMove(BattleSide.Player, 99));
            Assert.DoesNotThrow(() => _engine.ForecastMove(BattleSide.Player, -1));
        }
    }
}
