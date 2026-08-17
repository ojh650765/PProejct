using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// Everything one side of the field owns: its party, which member is out, and the
    /// per-switch-in state (stat stages, volatiles, counters) that resets when it swaps.
    ///
    /// Deliberately a plain data holder — all the rules live in the engine, so this can be
    /// snapshotted, inspected by tests and read by the AI without side effects.
    /// </summary>
    public sealed class BattleSideState
    {
        /// <summary>Stat stages indexed by <see cref="StatKind"/>, including accuracy and evasion.</summary>
        public const int StageCount = 8;

        public BattleSide Side;
        public readonly List<CreatureInstance> Party = new List<CreatureInstance>(6);
        public int ActiveIndex;

        public readonly int[] Stages = new int[StageCount];
        public VolatileFlags Volatiles;

        /// <summary>Turns of confusion left. Confusion ends when this reaches zero.</summary>
        public int ConfusionTurns;

        /// <summary>Consecutive successful Protects. Each one quarters the next one's odds.</summary>
        public int ProtectStreak;

        /// <summary>Side that seeded this one, so leech drain returns to the right creature.</summary>
        public BattleSide LeechSeedSource;

        /// <summary>Move id used last turn. Part of the state snapshot the tactical layer reads.</summary>
        public string LastMoveId;

        /// <summary>Instance ids that were on the field while the current opponent was alive.</summary>
        public readonly HashSet<string> Participants = new HashSet<string>();

        public CreatureInstance Active =>
            ActiveIndex >= 0 && ActiveIndex < Party.Count ? Party[ActiveIndex] : null;

        /// <summary>True when at least one party member can still be sent out.</summary>
        public bool HasHealthyMember
        {
            get
            {
                for (var i = 0; i < Party.Count; i++)
                    if (Party[i] != null && !Party[i].IsFainted) return true;
                return false;
            }
        }

        /// <summary>First party index that can be sent out, or -1 when the side is wiped.</summary>
        public int FirstHealthyIndex(int excluding = -1)
        {
            for (var i = 0; i < Party.Count; i++)
            {
                if (i == excluding) continue;
                if (Party[i] != null && !Party[i].IsFainted) return i;
            }
            return -1;
        }

        /// <summary>
        /// Wipes everything that is tied to the creature currently out. Called on every
        /// switch and on a faint replacement — boosts, drops, confusion and seeds all go.
        /// </summary>
        public void ResetOnSwitch()
        {
            Array.Clear(Stages, 0, Stages.Length);
            Volatiles = VolatileFlags.None;
            ConfusionTurns = 0;
            ProtectStreak = 0;
            LastMoveId = null;
        }
    }

    /// <summary>
    /// The concrete <see cref="IBattleStateView"/>. Handed to the UI, the AI and the Poké
    /// Lab oracle; none of them may mutate it, which the interface enforces by exposing
    /// only read-only collections.
    /// </summary>
    public sealed class BattleState : IBattleStateView
    {
        private readonly BattleSideState _player = new BattleSideState { Side = BattleSide.Player };
        private readonly BattleSideState _opponent = new BattleSideState { Side = BattleSide.Opponent };
        private readonly HashSet<int> _scouted = new HashSet<int>();

        public BattleKind Kind { get; internal set; }
        public int TurnNumber { get; internal set; }
        public Weather Weather { get; internal set; }
        public BattleOutcome Outcome { get; internal set; } = BattleOutcome.InProgress;

        /// <summary>Trainer identifier for trainer battles, surfaced by <see cref="BattleStartedEvent"/>.</summary>
        public string OpponentTrainerId { get; internal set; }

        /// <summary>Creature captured this battle, non-null only once the outcome is <see cref="BattleOutcome.Captured"/>.</summary>
        public CreatureInstance CapturedCreature { get; internal set; }

        internal BattleSideState Sides(BattleSide side) => side == BattleSide.Player ? _player : _opponent;
        internal BattleSideState PlayerSide => _player;
        internal BattleSideState OpponentSide => _opponent;

        public static BattleSide Other(BattleSide side) =>
            side == BattleSide.Player ? BattleSide.Opponent : BattleSide.Player;

        public CreatureInstance ActiveOf(BattleSide side) => Sides(side).Active;

        public IReadOnlyList<CreatureInstance> PartyOf(BattleSide side) => Sides(side).Party;

        public IReadOnlyList<int> StatStagesOf(BattleSide side) => Sides(side).Stages;

        public VolatileFlags VolatilesOf(BattleSide side) => Sides(side).Volatiles;

        public bool HasScouted(int speciesId) => _scouted.Contains(speciesId);

        /// <summary>
        /// Marks a species as scouted. The engine calls this the first time a creature is
        /// sent out, attacks or faints — the moments a trainer could actually read it.
        /// </summary>
        internal void MarkScouted(int speciesId)
        {
            if (speciesId > 0) _scouted.Add(speciesId);
        }

        /// <summary>Every species id observed this battle, for the encounter result's dex update.</summary>
        public IReadOnlyCollection<int> ScoutedSpecies => _scouted;

        internal void Clear()
        {
            _player.Party.Clear();
            _opponent.Party.Clear();
            _player.ResetOnSwitch();
            _opponent.ResetOnSwitch();
            _player.Participants.Clear();
            _opponent.Participants.Clear();
            _player.ActiveIndex = 0;
            _opponent.ActiveIndex = 0;
            _scouted.Clear();
            TurnNumber = 0;
            Outcome = BattleOutcome.InProgress;
            CapturedCreature = null;
            // The trainer belongs to the battle that named them. Begin stamps the next one
            // in straight after this, so a reused engine cannot carry the last one forward.
            OpponentTrainerId = null;
        }
    }
}
