using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Tests
{
    /// <summary>
    /// A hand-driven battle state and engine, so the tactical layer can be tested without
    /// the real engine existing yet. Pure C# with no NUnit or UnityEngine dependency, so
    /// the console verification harness in Tools/Export/Verify compiles it too and both
    /// sides exercise exactly the same double.
    ///
    /// Damage is scripted rather than simulated: these tests are about how the readout
    /// reacts to the numbers, not about whether the numbers are right.
    /// </summary>
    public sealed class FakeBattleState : IBattleStateView
    {
        public BattleKind Kind { get; set; } = BattleKind.Wild;
        public int TurnNumber { get; set; } = 1;
        public Weather Weather { get; set; } = Weather.Clear;
        public BattleOutcome Outcome { get; set; } = BattleOutcome.InProgress;

        public CreatureInstance PlayerActive;
        public CreatureInstance OpponentActive;
        public List<CreatureInstance> PlayerParty = new List<CreatureInstance>();
        public List<CreatureInstance> OpponentParty = new List<CreatureInstance>();
        public int[] PlayerStages = new int[8];
        public int[] OpponentStages = new int[8];
        public VolatileFlags PlayerVolatiles = VolatileFlags.None;
        public VolatileFlags OpponentVolatiles = VolatileFlags.None;
        public readonly HashSet<int> Scouted = new HashSet<int>();

        public CreatureInstance ActiveOf(BattleSide side) =>
            side == BattleSide.Player ? PlayerActive : OpponentActive;

        public IReadOnlyList<CreatureInstance> PartyOf(BattleSide side) =>
            side == BattleSide.Player ? PlayerParty : OpponentParty;

        public IReadOnlyList<int> StatStagesOf(BattleSide side) =>
            side == BattleSide.Player ? PlayerStages : OpponentStages;

        public VolatileFlags VolatilesOf(BattleSide side) =>
            side == BattleSide.Player ? PlayerVolatiles : OpponentVolatiles;

        public bool HasScouted(int speciesId) => Scouted.Contains(speciesId);
    }

    /// <summary>Returns whatever damage the test scripted for each (side, move slot).</summary>
    public sealed class FakeBattleEngine : IBattleEngine
    {
        private readonly Dictionary<(BattleSide, int), DamageForecast> _forecasts =
            new Dictionary<(BattleSide, int), DamageForecast>();

        public FakeBattleEngine(IBattleStateView state) { State = state; }

        public IBattleStateView State { get; }

        public void SetForecast(BattleSide side, int moveIndex, float min, float max, float expected,
                                float hitChance, float typeMultiplier, int turnsToKo)
        {
            _forecasts[(side, moveIndex)] =
                new DamageForecast(min, max, expected, hitChance, typeMultiplier, turnsToKo);
        }

        public DamageForecast ForecastMove(BattleSide attacker, int moveIndex)
        {
            if (_forecasts.TryGetValue((attacker, moveIndex), out var forecast)) return forecast;
            return new DamageForecast(0f, 0f, 0f, 1f, 1f, int.MaxValue);
        }

        public void Begin(BattleKind kind, IList<CreatureInstance> playerParty,
                          IList<CreatureInstance> opponentParty, Weather weather, int seed) =>
            throw new NotSupportedException("FakeBattleEngine only forecasts.");

        public IReadOnlyList<BattleEvent> ResolveTurn(BattleAction playerAction) =>
            throw new NotSupportedException("FakeBattleEngine only forecasts.");

        public IReadOnlyList<BattleAction> LegalActions(BattleSide side) =>
            Array.Empty<BattleAction>();
    }

    /// <summary>Builders that keep the scenario setup in the tests readable.</summary>
    public static class FakeCreature
    {
        public static CreatureInstance Make(int speciesId, string nickname, int maxHp,
                                            int attack, int defense, int spAttack, int spDefense, int speed,
                                            params string[] moveIds)
        {
            var creature = new CreatureInstance
            {
                SpeciesId = speciesId,
                Nickname = nickname,
                Level = 15,
                MaxHp = maxHp,
                CurrentHp = maxHp,
                Stats = new[] { maxHp, attack, defense, spAttack, spDefense, speed },
                Ivs = new int[StatKinds.BaseCount],
                Moves = new List<MoveSlot>(4),
            };
            foreach (var moveId in moveIds)
                creature.Moves.Add(new MoveSlot { MoveId = moveId, CurrentPp = 20, MaxPp = 20 });
            return creature;
        }

        public static CreatureInstance AtHealth(this CreatureInstance creature, float fraction)
        {
            creature.CurrentHp = Math.Max(1, (int)Math.Round(creature.MaxHp * fraction));
            return creature;
        }

        public static CreatureInstance WithStatus(this CreatureInstance creature, StatusCondition status)
        {
            creature.Status = status;
            return creature;
        }
    }
}
