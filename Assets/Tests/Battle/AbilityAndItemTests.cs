using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// The ability hook system and the item table. Each ability is checked through the
    /// hook the engine actually calls, so a hook that is never invoked fails here rather
    /// than shipping as a silently dead feature.
    /// </summary>
    public sealed class AbilityAndItemTests
    {
        /// <summary>Every ability the slice roster carries must be implemented, not silently inert.</summary>
        [Test]
        public void RosterAbilities_AreAllImplemented()
        {
            var required = new[]
            {
                AbilityIds.Overgrow, AbilityIds.Blaze, AbilityIds.Torrent, AbilityIds.Chlorophyll,
                AbilityIds.Static, AbilityIds.LightningRod, AbilityIds.Intimidate, AbilityIds.Levitate,
                AbilityIds.Sturdy, AbilityIds.SwiftSwim, AbilityIds.RainDish, AbilityIds.Guts,
                AbilityIds.RunAway, AbilityIds.KeenEye, AbilityIds.InnerFocus, AbilityIds.NoGuard,
                AbilityIds.SandVeil, AbilityIds.RockHead,
            };

            foreach (var id in required)
                Assert.That(AbilityRegistry.IsImplemented(id), Is.True, $"Ability '{id}' has no implementation.");
        }

        /// <summary>An unknown ability resolves to an inert stub rather than throwing mid-battle.</summary>
        [Test]
        public void UnknownAbility_ResolvesToAnInertStub()
        {
            var ability = AbilityRegistry.Resolve("definitely-not-a-real-ability");
            Assert.That(ability, Is.Not.Null);
            Assert.That(ability.AbsorbsMove(null, ElementType.Fire), Is.False);
            Assert.That(AbilityRegistry.Resolve(null), Is.Not.Null);
        }

        /// <summary>Overgrow lifts Grass power by half once its holder is below a third of its HP.</summary>
        [Test]
        public void Overgrow_BoostsOnlyWhenLowOnHp()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Bulbasaur, 30, "vine-whip").WithAbility(AbilityIds.Overgrow);
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 1);

            var healthy = engine.ForecastMove(BattleSide.Player, 0);

            player.CurrentHp = player.MaxHp / 4;
            var pinched = engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(pinched.ExpectedFraction, Is.GreaterThan(healthy.ExpectedFraction * 1.3f));
        }

        /// <summary>Intimidate drops the opposing Attack the moment its holder arrives.</summary>
        [Test]
        public void Intimidate_FiresOnEntry()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 20, "tackle").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 20, "tackle").WithAbility(AbilityIds.Intimidate);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 2);

            var intro = engine.DrainPendingEvents();

            Assert.That(engine.State.StatStagesOf(BattleSide.Player)[(int)StatKind.Attack], Is.EqualTo(-1));
            Assert.That(intro.OfType<AbilityTriggeredEvent>().Exists(e => e.AbilityId == AbilityIds.Intimidate), Is.True);
        }

        /// <summary>Sturdy leaves its holder on one HP, but only from full health.</summary>
        [Test]
        public void Sturdy_SurvivesALethalHitFromFullHp()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 50, "karate-chop").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Geodude, 5, "tackle").WithAbility(AbilityIds.Sturdy);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 3);

            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(foe.CurrentHp, Is.EqualTo(1), "Sturdy should have left it on exactly one HP.");
            Assert.That(stream.OfType<AbilityTriggeredEvent>().Exists(e => e.AbilityId == AbilityIds.Sturdy), Is.True);

            // A second hit, no longer from full HP, goes through.
            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
            Assert.That(foe.IsFainted, Is.True);
        }

        /// <summary>Rock Head refuses recoil; without it, Take Down hurts the user.</summary>
        [Test]
        public void RockHead_CancelsRecoil()
        {
            Assert.That(RecoilTaken(AbilityIds.RockHead), Is.EqualTo(0));
            Assert.That(RecoilTaken(null), Is.GreaterThan(0));
        }

        private static int RecoilTaken(string abilityId)
        {
            var engine = BattleTestBuilder.Engine();
            // Growl on the other side: it neither damages nor blocks, so the only HP the
            // user loses is its own recoil.
            var player = BattleTestBuilder.Creature(TestData.Geodude, 30, "take-down").WithAbility(abilityId);
            var foe = BattleTestBuilder.Creature(TestData.Machop, 30, "growl").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 4);

            // Take Down is 85% accurate, so keep swinging until one lands.
            for (var turn = 0; turn < 12; turn++)
            {
                player.CurrentHp = player.MaxHp;
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

                var landed = stream.OfType<DamageDealtEvent>()
                    .Exists(d => d.Target == BattleSide.Opponent && d.IndirectSourceId == null);
                if (landed) return Recoil(stream);
            }

            return -1;
        }

        private static int Recoil(IReadOnlyList<BattleEvent> stream)
        {
            var total = 0;
            foreach (var evt in stream)
                if (evt is DamageDealtEvent damage && damage.Target == BattleSide.Player &&
                    damage.IndirectSourceId == "take-down") total += damage.Amount;
            return total;
        }

        /// <summary>Guts trades the burn penalty for raw Attack.</summary>
        [Test]
        public void Guts_BoostsAttackWhenStatused()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "karate-chop").WithAbility(AbilityIds.Guts);
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 5);

            var healthy = engine.ForecastMove(BattleSide.Player, 0);
            player.Status = StatusCondition.Burn;
            var burned = engine.ForecastMove(BattleSide.Player, 0);

            Assert.That(burned.ExpectedFraction, Is.GreaterThan(healthy.ExpectedFraction),
                "Guts should make a burned physical attacker hit harder, not softer.");
        }

        /// <summary>No Guard makes even a low-accuracy move certain, in both directions.</summary>
        [Test]
        public void NoGuard_MakesEveryMoveLand()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "take-down").WithAbility(AbilityIds.NoGuard);
            var foe = BattleTestBuilder.Creature(TestData.Rattata, 30, "tackle").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 6);

            BattleTestBuilder.MoveRegistry.TryGet("take-down", out var takeDown);
            engine.TryChangeStage(BattleSide.Opponent, StatKind.Evasion, 6, "test");

            Assert.That(engine.HitChance(BattleSide.Player, BattleSide.Opponent, takeDown), Is.EqualTo(1f));
            Assert.That(engine.HitChance(BattleSide.Opponent, BattleSide.Player, takeDown), Is.EqualTo(1f));
        }

        /// <summary>Rain Dish tops its holder up every turn it rains.</summary>
        [Test]
        public void RainDish_HealsInRain()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(AbilityIds.RainDish);
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Rain, seed: 7);

            player.CurrentHp = player.MaxHp / 2;
            var before = player.CurrentHp;

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(player.CurrentHp, Is.EqualTo(before + player.MaxHp / 16));
        }

        /// <summary>Static shocks a contact attacker some of the time.</summary>
        [Test]
        public void Static_ParalysesContactAttackers()
        {
            var paralysed = 0;

            for (var seed = 0; seed < 30; seed++)
            {
                var engine = BattleTestBuilder.Engine();
                var player = BattleTestBuilder.Creature(TestData.Machop, 20, "tackle").WithAbility(null);
                var foe = BattleTestBuilder.Creature(TestData.Pikachu, 40, "growl").WithAbility(AbilityIds.Static);

                engine.Begin(BattleKind.Wild,
                    BattleTestBuilder.Party(player),
                    BattleTestBuilder.Party(foe),
                    Weather.Clear, seed);

                engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                if (player.Status == StatusCondition.Paralysis) paralysed++;
            }

            Assert.That(paralysed, Is.GreaterThan(0), "A 30% contact ability should fire across 30 seeds.");
            Assert.That(paralysed, Is.LessThan(30), "…but it must not fire every single time.");
        }

        /// <summary>Sand Veil grants sandstorm immunity and blunts incoming accuracy.</summary>
        [Test]
        public void SandVeil_HidesItsHolderInASandstorm()
        {
            var engine = BattleTestBuilder.Engine();
            // Growl on both sides so the only HP movement is the sandstorm's own chip.
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "growl").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Machop, 30, "growl").WithAbility(AbilityIds.SandVeil);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Sandstorm, seed: 8);

            BattleTestBuilder.MoveRegistry.TryGet("take-down", out var takeDown);
            Assert.That(engine.HitChance(BattleSide.Player, BattleSide.Opponent, takeDown),
                Is.EqualTo(0.85f * 0.8f).Within(1e-4f));
            Assert.That(engine.HitChance(BattleSide.Opponent, BattleSide.Player, takeDown),
                Is.EqualTo(0.85f).Within(1e-4f), "Sand Veil helps its holder dodge, not its holder aim.");

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(foe.CurrentHp, Is.EqualTo(foe.MaxHp), "Sand Veil holders take no sandstorm chip.");
            Assert.That(player.CurrentHp, Is.EqualTo(player.MaxHp - player.MaxHp / 16));
        }

        // ---- Items -------------------------------------------------------------------

        /// <summary>A berry fires the moment its holder drops past the threshold.</summary>
        [Test]
        public void OranBerry_TriggersAtLowHpAndIsConsumed()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Geodude, 30, "protect")
                .WithAbility(null).WithHeldItem("oran-berry");
            // Growl, because a real attack would knock the two-HP holder out before the
            // end-of-turn berry check ever runs.
            var foe = BattleTestBuilder.Creature(TestData.Machop, 30, "growl").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 9);

            player.CurrentHp = 2; // already under the half-HP threshold

            var fired = false;
            for (var turn = 0; turn < 6 && !fired; turn++)
            {
                var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));
                fired = stream.OfType<ItemUsedEvent>().Exists(e => e.ItemId == "oran-berry");
            }

            Assert.That(fired, Is.True, "The berry never fired despite the holder being under the threshold.");
            Assert.That(player.HeldItemId, Is.Null, "A berry must be consumed when it fires.");
        }

        /// <summary>Leftovers heal every turn and are never used up.</summary>
        [Test]
        public void Leftovers_HealEveryTurnAndPersist()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect")
                .WithAbility(null).WithHeldItem("leftovers");
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 10);

            player.CurrentHp = player.MaxHp / 3;
            var before = player.CurrentHp;

            engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(player.CurrentHp, Is.EqualTo(before + player.MaxHp / 16));
            Assert.That(player.HeldItemId, Is.EqualTo("leftovers"));
        }

        /// <summary>A status berry cures the condition it covers as soon as it lands.</summary>
        [Test]
        public void CheriBerry_CuresParalysisImmediately()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "protect")
                .WithAbility(null).WithHeldItem("cheri-berry");
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 11);

            Assert.That(engine.TryApplyStatus(BattleSide.Player, StatusCondition.Paralysis, "test"), Is.True);
            Assert.That(player.Status, Is.EqualTo(StatusCondition.None), "The berry should have cured it at once.");
            Assert.That(player.HeldItemId, Is.Null);
        }

        /// <summary>
        /// A Lum Berry answers a Confuse Ray. Confusion used to be applied without any held
        /// item check at all, so the berry's cures-confusion branch was reachable only when
        /// some unrelated status happened to land on an already-confused holder.
        /// </summary>
        [Test]
        public void LumBerry_CuresConfusionTheTurnItLands()
        {
            var engine = BattleTestBuilder.Engine();
            // Growl rather than Protect: a successful Protect would turn the Confuse Ray away
            // and there would be nothing for the berry to cure.
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "growl")
                .WithAbility(null).WithHeldItem("lum-berry");
            var foe = BattleTestBuilder.Creature(TestData.Gastly, 30, "confuse-ray").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 14);

            engine.DrainPendingEvents();
            var stream = engine.ResolveTurn(BattleAction.UseMove(BattleSide.Player, 0));

            Assert.That(stream.OfType<ItemUsedEvent>().Exists(e => e.ItemId == "lum-berry"), Is.True,
                "The berry never fired against a confusion it is supposed to cure.");
            Assert.That(engine.State.VolatilesOf(BattleSide.Player) & VolatileFlags.Confused,
                Is.EqualTo(VolatileFlags.None), "The berry fired but the confusion stayed.");
            Assert.That(player.HeldItemId, Is.Null, "A berry must be consumed when it fires.");
        }

        /// <summary>
        /// An item used on a benched member must not claim the active creature's HP bar.
        ///
        /// <see cref="HealedEvent"/> and <see cref="StatusChangedEvent"/> carry only a side, so
        /// one raised for the bench is indistinguishable from one raised for the creature on
        /// the field — a guaranteed desync for a revive. Until they can name the recipient the
        /// engine says so in words instead of lying with a beat.
        /// </summary>
        [Test]
        public void BenchedItems_ReachTheRightMemberWithoutFakingTheActiveBar()
        {
            var engine = BattleTestBuilder.Engine();
            var lead = BattleTestBuilder.Creature(TestData.Machop, 30, "protect").WithAbility(null);
            var bench = BattleTestBuilder.Creature(TestData.Squirtle, 30, "water-gun").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(lead, bench),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 13);

            engine.DrainPendingEvents();

            bench.CurrentHp = 0;
            bench.Status = StatusCondition.Fainted;
            var leadHpBefore = lead.CurrentHp;

            var revived = engine.ResolveTurn(BattleAction.UseItem(BattleSide.Player, "revive", 1));

            Assert.That(bench.CurrentHp, Is.EqualTo(bench.MaxHp * 50 / 100));
            Assert.That(bench.IsFainted, Is.False);
            Assert.That(lead.CurrentHp, Is.EqualTo(leadHpBefore), "The creature on the field must be untouched.");
            Assert.That(revived.OfType<ItemUsedEvent>().Exists(e => e.ItemId == "revive"), Is.True);
            Assert.That(revived.OfType<HealedEvent>().Count, Is.EqualTo(0),
                "A HealedEvent here names only the side, so a presenter would fill the wrong bar.");
            Assert.That(revived.OfType<MessageEvent>().Exists(m => m.Text.Contains("recovered")), Is.True,
                "The revive still has to be announced, naming who was actually revived.");

            // The same rule for a plain heal aimed off the field.
            bench.CurrentHp = 1;
            var healed = engine.ResolveTurn(BattleAction.UseItem(BattleSide.Player, "potion", 1));

            Assert.That(bench.CurrentHp, Is.EqualTo(21));
            Assert.That(healed.OfType<HealedEvent>().Count, Is.EqualTo(0));
            Assert.That(healed.OfType<MessageEvent>().Exists(m => m.Text.Contains("recovered")), Is.True);
        }

        /// <summary>A potion restores its listed amount, clamped to max HP.</summary>
        [Test]
        public void Potion_HealsAndClamps()
        {
            var engine = BattleTestBuilder.Engine();
            var player = BattleTestBuilder.Creature(TestData.Machop, 30, "protect").WithAbility(null);
            var foe = BattleTestBuilder.Creature(TestData.Squirtle, 30, "protect").WithAbility(null);

            engine.Begin(BattleKind.Wild,
                BattleTestBuilder.Party(player),
                BattleTestBuilder.Party(foe),
                Weather.Clear, seed: 12);

            player.CurrentHp = 1;
            engine.ResolveTurn(BattleAction.UseItem(BattleSide.Player, "potion"));
            Assert.That(player.CurrentHp, Is.EqualTo(21));

            player.CurrentHp = player.MaxHp - 3;
            engine.ResolveTurn(BattleAction.UseItem(BattleSide.Player, "potion"));
            Assert.That(player.CurrentHp, Is.EqualTo(player.MaxHp));
        }

        /// <summary>Balls are recognised as capture devices, and their multipliers are ordered.</summary>
        [Test]
        public void BallCatalogue_IsCoherent()
        {
            Assert.That(ItemCatalog.IsBall("poke-ball"), Is.True);
            Assert.That(ItemCatalog.IsBall("ultra-ball"), Is.True);
            Assert.That(ItemCatalog.IsBall("potion"), Is.False);

            ItemCatalog.TryGetBag("poke-ball", out var poke);
            ItemCatalog.TryGetBag("great-ball", out var great);
            ItemCatalog.TryGetBag("ultra-ball", out var ultra);
            ItemCatalog.TryGetBag(ItemCatalog.MasterBallId, out var master);

            Assert.That(great.BallMultiplier, Is.GreaterThan(poke.BallMultiplier));
            Assert.That(ultra.BallMultiplier, Is.GreaterThan(great.BallMultiplier));
            Assert.That(master.BallMultiplier, Is.EqualTo(0f), "The master ball is modelled as an automatic catch.");
        }

        /// <summary>A net ball is only worth its bonus against Water and Bug.</summary>
        [Test]
        public void NetBall_OnlyHelpsAgainstWaterAndBug()
        {
            var species = TestData.Species();
            species.TryGet(TestData.Squirtle, out var squirtle);
            species.TryGet(TestData.Machop, out var machop);

            Assert.That(ItemCatalog.BallMultiplier("net-ball", null, squirtle), Is.GreaterThan(1f));
            Assert.That(ItemCatalog.BallMultiplier("net-ball", null, machop), Is.EqualTo(1f));
        }
    }
}
