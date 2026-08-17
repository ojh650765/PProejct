using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// How a move actually resolves inside a turn: how many times it lands, what it emits
    /// per landing, and which of its riders survive the target dying.
    ///
    /// These paths are the ones a data author reaches for without touching the engine, so
    /// they are exercised through <see cref="BattleEngine.ResolveTurn"/> against moves the
    /// tests register themselves — a rider that silently does nothing has to fail here
    /// rather than ship looking like a feature.
    /// </summary>
    public sealed class MoveResolutionTests
    {
        /// <summary>Double Slap hits two to five times, and every landing is its own beat.</summary>
        [Test]
        public void MultiHit_ReportsEveryHitAndStaysInsideItsBounds()
        {
            var hitCountsSeen = new HashSet<int>();

            for (var seed = 0; seed < 30; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                // A weak attacker into a fat Rock type, so no roll of five can knock it out
                // and cut the sequence short.
                var player = BattleTestBuilder.Creature(TestData.Poliwag, 25, "double-slap").WithAbility(null);
                var foe = BattleTestBuilder.Creature(TestData.Geodude, 45, "growl").WithAbility(null);

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(player),
                    BattleTestBuilder.Party(foe),
                    Weather.Clear, seed);

                engine.DrainPendingEvents();
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

                // Double Slap is 85% accurate; a miss emits one beat and no hits at all.
                if (stream.OfType<MoveMissedEvent>().Count > 0) continue;

                var hits = stream.OfType<MoveExecutedEvent>()
                    .FindAll(e => e.Attacker == BattleSide.Player && e.MoveId == "double-slap");

                Assert.That(hits.Count, Is.InRange(2, 5),
                    $"Seed {seed} landed {hits.Count} hits, outside Double Slap's authored 2-5.");

                for (var i = 0; i < hits.Count; i++)
                {
                    Assert.That(hits[i].HitIndex, Is.EqualTo(i), "Hit indices must run 0..n-1 in order.");
                    Assert.That(hits[i].HitCount, Is.EqualTo(hits.Count),
                        "Every beat of one sequence must announce the same total.");
                }

                var damage = stream.OfType<DamageDealtEvent>()
                    .FindAll(d => d.Target == BattleSide.Opponent && d.IndirectSourceId == null);
                Assert.That(damage.Count, Is.EqualTo(hits.Count), "One damage beat per hit, no more and no fewer.");

                hitCountsSeen.Add(hits.Count);
            }

            Assert.That(hitCountsSeen.Count, Is.GreaterThan(1),
                "Thirty seeds produced a single hit count — the multi-hit roll is not being taken.");
        }

        /// <summary>The sequence stops on the hit that lands the knockout.</summary>
        [Test]
        public void MultiHit_StopsTheMomentTheTargetFaints()
        {
            for (var seed = 0; seed < 30; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                var player = BattleTestBuilder.Creature(TestData.Poliwag, 45, "double-slap").WithAbility(null);
                var foe = BattleTestBuilder.Creature(TestData.Oddish, 5, "growl").WithAbility(null);

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(player),
                    BattleTestBuilder.Party(foe),
                    Weather.Clear, seed);

                engine.DrainPendingEvents();
                foe.CurrentHp = 1;

                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                if (!foe.IsFainted) continue;

                var hits = stream.OfType<MoveExecutedEvent>()
                    .FindAll(e => e.Attacker == BattleSide.Player && e.MoveId == "double-slap");

                Assert.That(hits.Count, Is.EqualTo(1),
                    "The first hit was lethal, so nothing after it should have been swung.");
                Assert.That(hits[0].HitCount, Is.InRange(2, 5),
                    "The announced total is still the rolled one — the presenter is told what was intended.");
                Assert.That(stream.OfType<DamageDealtEvent>()
                    .FindAll(d => d.Target == BattleSide.Opponent && d.IndirectSourceId == null).Count,
                    Is.EqualTo(1), "A corpse must not be dealt damage again.");
                return;
            }

            Assert.Fail("No seed knocked the one-HP target out, so the mid-sequence faint was never reached.");
        }

        /// <summary>
        /// A rider aimed at the user is the attacker's, not the target's. The secondary gate
        /// used to require a live defender for every rider, so a self-boost authored on an
        /// attacking move vanished on exactly the hit that earned it.
        /// </summary>
        [Test]
        public void SelfTargetedRider_StillLandsOnTheKillingBlow()
        {
            var engine = BattleTestBuilder.Engine();

            // The Power-Up Punch shape: damages the target, boosts the user.
            BattleTestBuilder.MoveRegistry.Add(new MoveData
            {
                Id = "power-up-punch",
                NameEn = "Power-Up Punch",
                Type = ElementType.Fighting,
                Category = MoveCategory.Physical,
                Power = 40,
                Accuracy = 0,
                PowerPoints = 20,
                TargetsSelf = true,
                StatChanges = new[] { new StatStageChange { Stat = StatKind.Attack, Stages = 1 } },
            });

            var player = BattleTestBuilder.Creature(TestData.Machop, 45, "power-up-punch").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 5, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 5150);

            engine.DrainPendingEvents();
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(foe.IsFainted, Is.True, "A level 5 Rattata should not have survived a STAB Fighting hit.");
            Assert.That(engine.State.StatStagesOf(BattleSide.Player)[(int)StatKind.Attack], Is.EqualTo(1),
                "The user's own boost was thrown away because the target died to the same move.");
            Assert.That(stream.OfType<StatStageChangedEvent>()
                .Exists(e => e.Target == BattleSide.Player && e.Stat == StatKind.Attack), Is.True,
                "The boost must be announced, not applied silently.");
        }

        /// <summary>A flinch costs the target its turn, and expires with that turn.</summary>
        [Test]
        public void Flinch_CostsTheTargetItsTurn()
        {
            for (var seed = 0; seed < 40; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                // Rattata outruns Machop, so Bite always resolves while the target still has
                // a turn ahead of it — which is the only situation a flinch can interrupt.
                var player = BattleTestBuilder.Creature(TestData.Rattata, 40, "bite").WithAbility(null);
                var foe = BattleTestBuilder.Creature(TestData.Machop, 40, "tackle").WithAbility(null);

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(player),
                    BattleTestBuilder.Party(foe),
                    Weather.Clear, seed);

                engine.DrainPendingEvents();
                var attackerHpBefore = player.CurrentHp;
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

                if (!stream.OfType<VolatileChangedEvent>()
                        .Exists(v => v.Target == BattleSide.Opponent && (v.Added & VolatileFlags.Flinched) != 0))
                    continue;

                Assert.That(stream.OfType<MoveDeclaredEvent>().Exists(d => d.Side == BattleSide.Opponent), Is.False,
                    "A flinched creature must not get its move off.");
                Assert.That(stream.OfType<MessageEvent>().Exists(m => m.Text.Contains("flinched")), Is.True,
                    "The player has to be told why the opponent did nothing.");
                Assert.That(player.CurrentHp, Is.EqualTo(attackerHpBefore),
                    "The interrupted attack must not have landed either.");
                Assert.That(engine.State.VolatilesOf(BattleSide.Opponent) & VolatileFlags.Flinched,
                    Is.EqualTo(VolatileFlags.None), "A flinch expires with the turn it cost.");
                return;
            }

            Assert.Fail("A 30% flinch never fired across forty seeds.");
        }

        /// <summary>
        /// Charging, Recharging and Substitute are not implemented. Authoring one has to be a
        /// visible refusal rather than a flag nothing ever reads or clears.
        /// </summary>
        [Test]
        public void UnimplementedVolatile_IsRefusedRatherThanQuietlySet()
        {
            var engine = BattleTestBuilder.Engine();

            BattleTestBuilder.MoveRegistry.Add(new MoveData
            {
                Id = "charge-up",
                NameEn = "Charge Up",
                Type = ElementType.Normal,
                Category = MoveCategory.Status,
                Power = 0,
                Accuracy = 0,
                PowerPoints = 5,
                MakesContact = false,
                TargetsSelf = true,
                InflictsVolatile = VolatileFlags.Charging,
            });

            var player = BattleTestBuilder.Creature(TestData.Gastly, 30, "charge-up").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Machop, 30, "growl").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 4711);

            engine.DrainPendingEvents();
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(engine.State.VolatilesOf(BattleSide.Player) & VolatileFlags.Charging,
                Is.EqualTo(VolatileFlags.None),
                "Nothing reads or clears Charging, so setting it would flag the creature for the whole battle.");
            Assert.That(stream.OfType<VolatileChangedEvent>()
                .Exists(v => (v.Added & VolatileFlags.Charging) != 0), Is.False,
                "…and it must not be announced as though it had taken hold.");
            Assert.That(stream.OfType<MessageEvent>().Exists(m => m.Text == "But nothing happened!"), Is.True,
                "A data author reaching for an unimplemented volatile has to see the refusal.");
        }

        /// <summary>
        /// The submitted action is the player's whatever side it claims. Nothing upstream
        /// validates it, and an action stamped Opponent used to hand the opponent both turns.
        /// </summary>
        [Test]
        public void SubmittedAction_IsNormalisedOntoThePlayersSide()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "karate-chop").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 8080);

            engine.DrainPendingEvents();
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Opponent, 0));

            var declared = stream.OfType<MoveDeclaredEvent>();
            Assert.That(declared.FindAll(d => d.Side == BattleSide.Player).Count, Is.EqualTo(1),
                "The player's action was dropped on the floor.");
            Assert.That(declared.FindAll(d => d.Side == BattleSide.Opponent).Count, Is.EqualTo(1),
                "The opponent acted more than once.");
            Assert.That(declared.Exists(d => d.Side == BattleSide.Player && d.MoveId == "karate-chop"), Is.True,
                "The move index has to survive the re-stamping.");
        }
    }
}
