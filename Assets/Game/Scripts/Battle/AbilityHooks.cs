using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>PokeAPI ability identifiers the engine understands. String keys, because that is what the dex ships.</summary>
    public static class AbilityIds
    {
        public const string Overgrow = "overgrow";
        public const string Blaze = "blaze";
        public const string Torrent = "torrent";
        public const string Chlorophyll = "chlorophyll";
        public const string SwiftSwim = "swift-swim";
        public const string Static = "static";
        public const string LightningRod = "lightning-rod";
        public const string Intimidate = "intimidate";
        public const string Levitate = "levitate";
        public const string Sturdy = "sturdy";
        public const string RainDish = "rain-dish";
        public const string Guts = "guts";
        public const string RunAway = "run-away";
        public const string KeenEye = "keen-eye";
        public const string InnerFocus = "inner-focus";
        public const string NoGuard = "no-guard";
        public const string SandVeil = "sand-veil";
        public const string RockHead = "rock-head";
    }

    /// <summary>
    /// The slice of the engine an ability is allowed to touch. Abilities never mutate
    /// creatures directly — they go through these calls so that every change they make
    /// still produces the events the presentation layers are waiting for.
    /// </summary>
    public interface IAbilityHost
    {
        BattleState State { get; }
        BattleRandom Random { get; }

        /// <summary>Pushes an event onto the current turn's stream.</summary>
        void Emit(BattleEvent evt);

        /// <summary>Emits the "ability activated" beat. Call before the effect it explains.</summary>
        void AnnounceAbility(BattleSide side, string abilityId);

        /// <summary>Damage from a non-move source. Returns the amount actually dealt.</summary>
        int ApplyIndirectDamage(BattleSide side, int amount, string sourceId);

        /// <summary>Healing from any source, clamped to max HP. Returns the amount actually healed.</summary>
        int ApplyHeal(BattleSide side, int amount, string sourceId);

        /// <summary>Attempts a stat stage change, honouring blocks and the ±6 clamp.</summary>
        bool TryChangeStage(BattleSide target, StatKind stat, int delta, string sourceId);

        /// <summary>Attempts a non-volatile status, honouring type and ability immunities.</summary>
        bool TryApplyStatus(BattleSide target, StatusCondition status, string sourceId);

        /// <summary>Species row for the creature currently out on a side, or null when unknown.</summary>
        SpeciesData SpeciesOf(BattleSide side);
    }

    /// <summary>Which creature an ability hook is being asked about.</summary>
    public readonly struct AbilityContext
    {
        public readonly IAbilityHost Host;
        /// <summary>Side that owns the ability being consulted.</summary>
        public readonly BattleSide Side;
        public readonly CreatureInstance Self;

        public AbilityContext(IAbilityHost host, BattleSide side, CreatureInstance self)
        {
            Host = host; Side = side; Self = self;
        }

        public BattleSide Opposing => BattleState.Other(Side);
        public Weather Weather => Host?.State?.Weather ?? Weather.Clear;
    }

    /// <summary>Details of an incoming hit, for on-hit reactions like static.</summary>
    public readonly struct HitContext
    {
        public readonly IAbilityHost Host;
        /// <summary>Side that owns the ability — the one that was hit.</summary>
        public readonly BattleSide Side;
        public readonly BattleSide AttackerSide;
        public readonly MoveData Move;
        public readonly int Damage;
        public readonly bool Fainted;

        public HitContext(IAbilityHost host, BattleSide side, BattleSide attackerSide, MoveData move, int damage, bool fainted)
        {
            Host = host; Side = side; AttackerSide = attackerSide; Move = move; Damage = damage; Fainted = fainted;
        }
    }

    /// <summary>A status the engine is about to inflict, offered to the target's ability first.</summary>
    public readonly struct StatusAttemptContext
    {
        public readonly IAbilityHost Host;
        public readonly BattleSide Side;
        public readonly CreatureInstance Self;
        public readonly StatusCondition Status;
        public readonly string SourceId;

        public StatusAttemptContext(IAbilityHost host, BattleSide side, CreatureInstance self, StatusCondition status, string sourceId)
        {
            Host = host; Side = side; Self = self; Status = status; SourceId = sourceId;
        }
    }

    /// <summary>
    /// One ability. Every hook is a no-op by default so a new ability only overrides what
    /// it actually changes — a switch statement over ability ids would force every new
    /// entry to touch every site instead.
    /// </summary>
    public abstract class BattleAbility
    {
        /// <summary>PokeAPI identifier, matching <see cref="CreatureInstance.AbilityId"/>.</summary>
        public abstract string Id { get; }

        /// <summary>Player-facing name for <see cref="AbilityTriggeredEvent"/>.</summary>
        public virtual string DisplayName => Id;

        /// <summary>Fires when the creature is sent out, including at battle start.</summary>
        public virtual void OnEntry(AbilityContext ctx) { }

        /// <summary>Adjusts the damage context. Called once as the attacker and once as the defender.</summary>
        public virtual void OnModifyDamage(AbilityContext ctx, DamageContext damage, bool asAttacker) { }

        /// <summary>Multiplies effective Speed for turn order. Return the value unchanged to opt out.</summary>
        public virtual float ModifySpeed(AbilityContext ctx, float speed) => speed;

        /// <summary>
        /// Adjusts the final hit chance. <paramref name="asAttacker"/> distinguishes the
        /// mover's ability from the target's.
        /// </summary>
        public virtual float ModifyAccuracy(AbilityContext ctx, float accuracy, bool asAttacker) => accuracy;

        /// <summary>True when this ability makes its owner's moves and incoming moves bypass accuracy entirely.</summary>
        public virtual bool IgnoresAccuracy(AbilityContext ctx) => false;

        /// <summary>
        /// Pure predicate: does this ability cancel the incoming move outright? It has to
        /// stay side-effect free because <see cref="IBattleEngine.ForecastMove"/> consults
        /// it, and a forecast may neither mutate state nor emit events.
        /// The engine emits a <see cref="MoveMissedEvent"/> with <c>WasImmune</c> set.
        /// </summary>
        public virtual bool AbsorbsMove(MoveData move, ElementType moveType) => false;

        /// <summary>The consequence of an absorb — a stat boost, a heal. Only fired during real resolution.</summary>
        public virtual void OnAbsorbedMove(AbilityContext ctx, MoveData move) { }

        /// <summary>Reaction after taking a hit, e.g. a contact ability.</summary>
        public virtual void OnHitByMove(HitContext ctx) { }

        /// <summary>True to refuse the status. Announce the ability inside the hook if it should be visible.</summary>
        public virtual bool BlocksStatus(StatusAttemptContext ctx) => false;

        /// <summary>True to refuse a stat drop inflicted by the opponent.</summary>
        public virtual bool BlocksStatDrop(AbilityContext ctx, StatKind stat) => false;

        /// <summary>True to refuse a flinch.</summary>
        public virtual bool BlocksFlinch(AbilityContext ctx) => false;

        /// <summary>True to refuse recoil damage.</summary>
        public virtual bool BlocksRecoil(AbilityContext ctx) => false;

        /// <summary>True to be immune to the given weather's chip damage.</summary>
        public virtual bool BlocksWeatherDamage(AbilityContext ctx, Weather weather) => false;

        /// <summary>True to guarantee escape from a wild battle.</summary>
        public virtual bool GuaranteesEscape(AbilityContext ctx) => false;

        /// <summary>
        /// Last chance to survive a lethal hit. Return the HP to be left with, or 0 to let
        /// the hit land as calculated.
        /// </summary>
        public virtual int SurviveLethalHit(AbilityContext ctx, int incomingDamage, int hpBeforeHit) => 0;

        /// <summary>End-of-turn upkeep, after status and weather damage.</summary>
        public virtual void OnEndOfTurn(AbilityContext ctx) { }
    }

    /// <summary>
    /// Ability lookup. The library registers itself on first use; game code can add more
    /// with <see cref="Register"/> without touching the engine.
    /// </summary>
    public static class AbilityRegistry
    {
        private static readonly Dictionary<string, BattleAbility> Entries =
            new Dictionary<string, BattleAbility>(StringComparer.OrdinalIgnoreCase);

        private static readonly BattleAbility Inert = new InertAbility();
        private static bool _seeded;

        /// <summary>Adds or replaces an ability. Later registrations win, so mods can override.</summary>
        public static void Register(BattleAbility ability)
        {
            if (ability == null || string.IsNullOrEmpty(ability.Id)) return;
            Entries[ability.Id] = ability;
        }

        /// <summary>
        /// Resolves an ability id. Never returns null — an unknown ability behaves as a
        /// creature with no ability, which is the only safe failure mode mid-battle.
        /// </summary>
        public static BattleAbility Resolve(string abilityId)
        {
            EnsureSeeded();
            if (string.IsNullOrEmpty(abilityId)) return Inert;
            return Entries.TryGetValue(abilityId, out var ability) ? ability : Inert;
        }

        /// <summary>True when the id has real behaviour rather than falling back to the inert stub.</summary>
        public static bool IsImplemented(string abilityId)
        {
            EnsureSeeded();
            return !string.IsNullOrEmpty(abilityId) && Entries.ContainsKey(abilityId);
        }

        /// <summary>Every implemented ability id, for tooling and tests.</summary>
        public static IEnumerable<string> ImplementedIds
        {
            get { EnsureSeeded(); return Entries.Keys; }
        }

        private static void EnsureSeeded()
        {
            if (_seeded) return;
            _seeded = true;
            AbilityLibrary.RegisterAll();
        }

        private sealed class InertAbility : BattleAbility
        {
            public override string Id => string.Empty;
        }
    }
}
