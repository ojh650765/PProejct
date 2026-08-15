using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// The abilities the slice roster actually carries. Each is a small class overriding
    /// only the hooks it needs, so adding the next one costs a class and a registration
    /// line rather than an edit to the resolution path.
    /// </summary>
    public static class AbilityLibrary
    {
        /// <summary>Registers every built-in ability. Called once by <see cref="AbilityRegistry"/>.</summary>
        public static void RegisterAll()
        {
            AbilityRegistry.Register(new PinchBoostAbility(AbilityIds.Overgrow, "Overgrow", ElementType.Grass));
            AbilityRegistry.Register(new PinchBoostAbility(AbilityIds.Blaze, "Blaze", ElementType.Fire));
            AbilityRegistry.Register(new PinchBoostAbility(AbilityIds.Torrent, "Torrent", ElementType.Water));

            AbilityRegistry.Register(new WeatherSpeedAbility(AbilityIds.Chlorophyll, "Chlorophyll", Weather.Sun));
            AbilityRegistry.Register(new WeatherSpeedAbility(AbilityIds.SwiftSwim, "Swift Swim", Weather.Rain));

            AbilityRegistry.Register(new StaticAbility());
            AbilityRegistry.Register(new LightningRodAbility());
            AbilityRegistry.Register(new IntimidateAbility());
            AbilityRegistry.Register(new LevitateAbility());
            AbilityRegistry.Register(new SturdyAbility());
            AbilityRegistry.Register(new RainDishAbility());
            AbilityRegistry.Register(new GutsAbility());
            AbilityRegistry.Register(new RunAwayAbility());
            AbilityRegistry.Register(new KeenEyeAbility());
            AbilityRegistry.Register(new InnerFocusAbility());
            AbilityRegistry.Register(new NoGuardAbility());
            AbilityRegistry.Register(new SandVeilAbility());
            AbilityRegistry.Register(new RockHeadAbility());
        }

        /// <summary>Overgrow, Blaze and Torrent differ only in the type they push.</summary>
        private sealed class PinchBoostAbility : BattleAbility
        {
            private const float PinchThreshold = 1f / 3f;
            private const float PowerBoost = 1.5f;

            private readonly ElementType _type;

            public PinchBoostAbility(string id, string displayName, ElementType type)
            {
                Id = id; DisplayName = displayName; _type = type;
            }

            public override string Id { get; }
            public override string DisplayName { get; }

            public override void OnModifyDamage(AbilityContext ctx, DamageContext damage, bool asAttacker)
            {
                if (!asAttacker || damage.MoveType != _type) return;
                if (ctx.Self == null || ctx.Self.HpFraction > PinchThreshold) return;
                damage.PowerMultiplier *= PowerBoost;
            }
        }

        /// <summary>Chlorophyll and Swift Swim: double Speed under their weather.</summary>
        private sealed class WeatherSpeedAbility : BattleAbility
        {
            private readonly Weather _weather;

            public WeatherSpeedAbility(string id, string displayName, Weather weather)
            {
                Id = id; DisplayName = displayName; _weather = weather;
            }

            public override string Id { get; }
            public override string DisplayName { get; }

            public override float ModifySpeed(AbilityContext ctx, float speed) =>
                ctx.Weather == _weather ? speed * 2f : speed;
        }

        private sealed class StaticAbility : BattleAbility
        {
            private const int ParalysisChance = 30;

            public override string Id => AbilityIds.Static;
            public override string DisplayName => "Static";

            public override void OnHitByMove(HitContext ctx)
            {
                // Only contact makes the charge jump, and a fainted body cannot discharge.
                if (ctx.Move == null || !ctx.Move.MakesContact || ctx.Fainted) return;
                if (!ctx.Host.Random.Chance(ParalysisChance)) return;

                ctx.Host.AnnounceAbility(ctx.Side, AbilityIds.Static);
                ctx.Host.TryApplyStatus(ctx.AttackerSide, StatusCondition.Paralysis, AbilityIds.Static);
            }
        }

        private sealed class LightningRodAbility : BattleAbility
        {
            public override string Id => AbilityIds.LightningRod;
            public override string DisplayName => "Lightning Rod";

            public override bool AbsorbsMove(MoveData move, ElementType moveType) =>
                moveType == ElementType.Electric && move != null && move.Category != MoveCategory.Status;

            public override void OnAbsorbedMove(AbilityContext ctx, MoveData move) =>
                ctx.Host.TryChangeStage(ctx.Side, StatKind.SpAttack, 1, AbilityIds.LightningRod);
        }

        private sealed class IntimidateAbility : BattleAbility
        {
            public override string Id => AbilityIds.Intimidate;
            public override string DisplayName => "Intimidate";

            public override void OnEntry(AbilityContext ctx)
            {
                var target = ctx.Opposing;
                if (ctx.Host.State.ActiveOf(target) == null) return;

                ctx.Host.AnnounceAbility(ctx.Side, AbilityIds.Intimidate);
                ctx.Host.TryChangeStage(target, StatKind.Attack, -1, AbilityIds.Intimidate);
            }
        }

        private sealed class LevitateAbility : BattleAbility
        {
            public override string Id => AbilityIds.Levitate;
            public override string DisplayName => "Levitate";

            public override bool AbsorbsMove(MoveData move, ElementType moveType) =>
                moveType == ElementType.Ground && move != null && move.Category != MoveCategory.Status;
        }

        private sealed class SturdyAbility : BattleAbility
        {
            public override string Id => AbilityIds.Sturdy;
            public override string DisplayName => "Sturdy";

            public override int SurviveLethalHit(AbilityContext ctx, int incomingDamage, int hpBeforeHit)
            {
                // Only from full HP, and only when the hit would actually have killed.
                if (ctx.Self == null || hpBeforeHit < ctx.Self.MaxHp) return 0;
                if (incomingDamage < hpBeforeHit) return 0;

                ctx.Host.AnnounceAbility(ctx.Side, AbilityIds.Sturdy);
                return 1;
            }
        }

        private sealed class RainDishAbility : BattleAbility
        {
            private const int HealDivisor = 16;

            public override string Id => AbilityIds.RainDish;
            public override string DisplayName => "Rain Dish";

            public override void OnEndOfTurn(AbilityContext ctx)
            {
                if (ctx.Weather != Weather.Rain || ctx.Self == null || ctx.Self.IsFainted) return;
                if (ctx.Self.CurrentHp >= ctx.Self.MaxHp) return;

                ctx.Host.AnnounceAbility(ctx.Side, AbilityIds.RainDish);
                ctx.Host.ApplyHeal(ctx.Side, ctx.Self.MaxHp / HealDivisor, AbilityIds.RainDish);
            }
        }

        private sealed class GutsAbility : BattleAbility
        {
            private const float AttackBoost = 1.5f;

            public override string Id => AbilityIds.Guts;
            public override string DisplayName => "Guts";

            public override void OnModifyDamage(AbilityContext ctx, DamageContext damage, bool asAttacker)
            {
                if (!asAttacker || damage.Move == null || damage.Move.Category != MoveCategory.Physical) return;
                if (ctx.Self == null || ctx.Self.Status == StatusCondition.None) return;

                // The burn halving is skipped inside the calculator for Guts holders, so this
                // is a clean 1.5x rather than a 3x correction.
                damage.AttackMultiplier *= AttackBoost;
            }
        }

        private sealed class RunAwayAbility : BattleAbility
        {
            public override string Id => AbilityIds.RunAway;
            public override string DisplayName => "Run Away";

            public override bool GuaranteesEscape(AbilityContext ctx) => true;
        }

        private sealed class KeenEyeAbility : BattleAbility
        {
            public override string Id => AbilityIds.KeenEye;
            public override string DisplayName => "Keen Eye";

            public override bool BlocksStatDrop(AbilityContext ctx, StatKind stat) => stat == StatKind.Accuracy;
        }

        private sealed class InnerFocusAbility : BattleAbility
        {
            public override string Id => AbilityIds.InnerFocus;
            public override string DisplayName => "Inner Focus";

            public override bool BlocksFlinch(AbilityContext ctx) => true;
        }

        private sealed class NoGuardAbility : BattleAbility
        {
            public override string Id => AbilityIds.NoGuard;
            public override string DisplayName => "No Guard";

            // Cuts both ways: the holder's moves land and so does everything aimed at it.
            public override bool IgnoresAccuracy(AbilityContext ctx) => true;
        }

        private sealed class SandVeilAbility : BattleAbility
        {
            private const float EvasionBoost = 0.8f;

            public override string Id => AbilityIds.SandVeil;
            public override string DisplayName => "Sand Veil";

            public override float ModifyAccuracy(AbilityContext ctx, float accuracy, bool asAttacker) =>
                !asAttacker && ctx.Weather == Weather.Sandstorm ? accuracy * EvasionBoost : accuracy;

            public override bool BlocksWeatherDamage(AbilityContext ctx, Weather weather) =>
                weather == Weather.Sandstorm;
        }

        private sealed class RockHeadAbility : BattleAbility
        {
            public override string Id => AbilityIds.RockHead;
            public override string DisplayName => "Rock Head";

            public override bool BlocksRecoil(AbilityContext ctx) => true;
        }
    }
}
