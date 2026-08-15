using System;
using System.Collections.Generic;

namespace PokeLab.Core
{
    /// <summary>
    /// A concrete creature instance — a party member or a wild encounter.
    /// The battle engine owns mutation; every other system treats this as read-only.
    /// </summary>
    [Serializable]
    public sealed class CreatureInstance
    {
        public int SpeciesId;
        public string Nickname;
        public int Level = 5;
        public int Experience;

        public int CurrentHp;
        public int MaxHp;

        /// <summary>Final computed stats indexed by <see cref="StatKind"/>, length <see cref="StatKinds.BaseCount"/>.</summary>
        public int[] Stats = new int[StatKinds.BaseCount];
        /// <summary>Individual values, 0-31.</summary>
        public int[] Ivs = new int[StatKinds.BaseCount];

        public string AbilityId;
        public string HeldItemId;
        public StatusCondition Status = StatusCondition.None;
        /// <summary>Remaining sleep turns, or badly-poisoned counter.</summary>
        public int StatusCounter;

        public List<MoveSlot> Moves = new List<MoveSlot>(4);

        /// <summary>Stable identity used to correlate model, animator and UI across a battle.</summary>
        public string InstanceId = Guid.NewGuid().ToString("N");

        public bool IsFainted => CurrentHp <= 0;
        public float HpFraction => MaxHp <= 0 ? 0f : (float)CurrentHp / MaxHp;
    }

    [Serializable]
    public struct MoveSlot
    {
        public string MoveId;
        public int CurrentPp;
        public int MaxPp;
        public bool Usable => CurrentPp > 0;
    }

    /// <summary>Which side of the field a combatant belongs to.</summary>
    public enum BattleSide
    {
        Player = 0,
        Opponent = 1,
    }

    public enum BattleKind
    {
        Wild = 0,
        Trainer = 1,
    }

    public enum BattleOutcome
    {
        InProgress = 0,
        PlayerVictory = 1,
        PlayerDefeat = 2,
        Captured = 3,
        Fled = 4,
    }

    /// <summary>A player or AI decision for one turn.</summary>
    public readonly struct BattleAction
    {
        public enum Kind { Move, Switch, Item, Run, Capture }

        public readonly Kind Type;
        public readonly BattleSide Side;
        public readonly int MoveIndex;
        public readonly int PartyIndex;
        public readonly string ItemId;

        private BattleAction(Kind type, BattleSide side, int moveIndex, int partyIndex, string itemId)
        {
            Type = type; Side = side; MoveIndex = moveIndex; PartyIndex = partyIndex; ItemId = itemId;
        }

        public static BattleAction UseMove(BattleSide side, int moveIndex) =>
            new BattleAction(Kind.Move, side, moveIndex, -1, null);
        public static BattleAction SwitchTo(BattleSide side, int partyIndex) =>
            new BattleAction(Kind.Switch, side, -1, partyIndex, null);
        public static BattleAction UseItem(BattleSide side, string itemId, int partyIndex = -1) =>
            new BattleAction(Kind.Item, side, -1, partyIndex, itemId);
        public static BattleAction Run(BattleSide side) =>
            new BattleAction(Kind.Run, side, -1, -1, null);
        public static BattleAction Capture(BattleSide side, string ballId) =>
            new BattleAction(Kind.Capture, side, -1, -1, ballId);
    }

    /// <summary>Read-only snapshot of the field, safe to hand to UI, AI and the Poké Lab oracle.</summary>
    public interface IBattleStateView
    {
        BattleKind Kind { get; }
        int TurnNumber { get; }
        Weather Weather { get; }
        BattleOutcome Outcome { get; }

        CreatureInstance ActiveOf(BattleSide side);
        IReadOnlyList<CreatureInstance> PartyOf(BattleSide side);
        /// <summary>Stat stages in [-6, 6] indexed by <see cref="StatKind"/>, length 8.</summary>
        IReadOnlyList<int> StatStagesOf(BattleSide side);
        VolatileFlags VolatilesOf(BattleSide side);

        /// <summary>True once the player has seen this species faint or attack — gates Poké Lab intel.</summary>
        bool HasScouted(int speciesId);
    }

    /// <summary>
    /// The turn-based engine. Deterministic given a seed: the same seed and the same
    /// action sequence must always produce the same event stream.
    /// </summary>
    public interface IBattleEngine
    {
        IBattleStateView State { get; }

        void Begin(BattleKind kind, IList<CreatureInstance> playerParty, IList<CreatureInstance> opponentParty, Weather weather, int seed);

        /// <summary>
        /// Resolves one full turn and returns the ordered event stream for presentation.
        /// The opponent action is chosen internally by the battle AI.
        /// </summary>
        IReadOnlyList<BattleEvent> ResolveTurn(BattleAction playerAction);

        /// <summary>Legal actions for the given side this turn, for UI gating and AI search.</summary>
        IReadOnlyList<BattleAction> LegalActions(BattleSide side);

        /// <summary>
        /// Forward simulation used by the tactical layer. Must not mutate live state.
        /// Returns expected damage as a fraction of the target's max HP.
        /// </summary>
        DamageForecast ForecastMove(BattleSide attacker, int moveIndex);
    }

    /// <summary>Deterministic, RNG-free expectation of a move's outcome. Feeds the Poké Lab panel.</summary>
    public readonly struct DamageForecast
    {
        public readonly float MinFraction;
        public readonly float MaxFraction;
        public readonly float ExpectedFraction;
        public readonly float HitChance;
        public readonly float TypeMultiplier;
        public readonly Effectiveness Effectiveness;
        /// <summary>Turns to KO at the expected roll. int.MaxValue when the move cannot KO.</summary>
        public readonly int TurnsToKo;

        public DamageForecast(float min, float max, float expected, float hitChance, float typeMultiplier, int turnsToKo)
        {
            MinFraction = min; MaxFraction = max; ExpectedFraction = expected;
            HitChance = hitChance; TypeMultiplier = typeMultiplier;
            Effectiveness = EffectivenessRules.Classify(typeMultiplier);
            TurnsToKo = turnsToKo;
        }
    }
}
