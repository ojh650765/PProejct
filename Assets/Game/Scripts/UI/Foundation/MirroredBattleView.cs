using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The battle read from the other end of the field.
    ///
    /// <b>Why this is needed at all.</b> A PvP match is two copies of the engine stepped in
    /// lockstep, and both of them must simulate the SAME assignment of sides — almost
    /// everything the engine does is ordered by side, so mirroring the sides would have the
    /// two generators consumed in opposite orders and the same seed would produce two
    /// different battles. So the match's player 0 is the engine's Player side on both
    /// machines, and player 1 is the engine's Opponent even though they are, to themselves,
    /// the player.
    ///
    /// <b>Why a decorator rather than teaching the views.</b> The first attempt threaded
    /// "which side is mine" through each reader, and it kept finding more of them: the HUD,
    /// the command panel, the scanner, and then <c>TacticalAnalyzer</c>, which has a dozen
    /// hardcoded sides and lives in an assembly that must not know the UI exists. Every one
    /// of those would have been a rule each future view had to remember, and the failure mode
    /// for forgetting is a screen that is subtly wrong for exactly one of the two players —
    /// which no local test would ever catch. Flipping once, here, leaves every reader exactly
    /// as it was written.
    ///
    /// Read-only on purpose. <see cref="Begin"/> and <see cref="ResolveTurn"/> throw rather
    /// than delegate: a view that starts or advances a battle through a presentation wrapper
    /// would be driving the simulation from the wrong end, and a loud failure in a place
    /// nothing should be calling beats a quiet desync in a place everything is watching.
    /// </summary>
    internal sealed class MirroredBattleView : IBattleEngine, IBattleStateView
    {
        private readonly IBattleEngine _inner;

        public MirroredBattleView(IBattleEngine inner) =>
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        /// <summary>True when this still wraps the given engine, so the cache can be trusted.</summary>
        public bool Wraps(IBattleEngine engine) => ReferenceEquals(_inner, engine);

        private static BattleSide Flip(BattleSide side) =>
            side == BattleSide.Player ? BattleSide.Opponent : BattleSide.Player;

        // ---- IBattleEngine ---------------------------------------------------------------

        public IBattleStateView State => this;

        public void Begin(BattleKind kind, IList<CreatureInstance> playerParty,
                          IList<CreatureInstance> opponentParty, Weather weather, int seed) =>
            throw new NotSupportedException(
                "MirroredBattleView is a way of looking at a battle, not a way of running one. " +
                "Start battles through BattleStage, which owns which end of the field this is.");

        public IReadOnlyList<BattleEvent> ResolveTurn(BattleAction playerAction) =>
            throw new NotSupportedException(
                "MirroredBattleView must not advance the battle: in a PvP match a turn needs " +
                "both players' actions. Use BattleStage.SubmitPvpTurn.");

        /// <summary>
        /// The legal actions, restamped so they can be handed straight back.
        ///
        /// Asking the engine about the local player means asking about its Opponent, and the
        /// actions it answers with are stamped for that side. Returning them unchanged would
        /// give the UI a list of buttons that, when pressed, tell the engine to move the
        /// wrong creature.
        /// </summary>
        public IReadOnlyList<BattleAction> LegalActions(BattleSide side)
        {
            var actions = _inner.LegalActions(Flip(side));
            if (actions == null || actions.Count == 0) return actions;

            var restamped = new List<BattleAction>(actions.Count);
            for (var i = 0; i < actions.Count; i++) restamped.Add(Restamp(actions[i], side));
            return restamped;
        }

        public DamageForecast ForecastMove(BattleSide attacker, int moveIndex) =>
            _inner.ForecastMove(Flip(attacker), moveIndex);

        // ---- IBattleStateView ------------------------------------------------------------

        public BattleKind Kind => _inner.State.Kind;
        public int TurnNumber => _inner.State.TurnNumber;
        public Weather Weather => _inner.State.Weather;

        /// <summary>
        /// The result as this end of the field experienced it. "Player victory" is a statement
        /// about the engine's Player side, so for player 1 it is exactly backwards.
        /// </summary>
        public BattleOutcome Outcome
        {
            get
            {
                switch (_inner.State.Outcome)
                {
                    case BattleOutcome.PlayerVictory: return BattleOutcome.PlayerDefeat;
                    case BattleOutcome.PlayerDefeat: return BattleOutcome.PlayerVictory;
                    // InProgress and Fled read the same from both ends, and a capture cannot
                    // happen in the only battle this wrapper is ever used for.
                    default: return _inner.State.Outcome;
                }
            }
        }

        public CreatureInstance ActiveOf(BattleSide side) => _inner.State.ActiveOf(Flip(side));

        public IReadOnlyList<CreatureInstance> PartyOf(BattleSide side) =>
            _inner.State.PartyOf(Flip(side));

        public IReadOnlyList<int> StatStagesOf(BattleSide side) =>
            _inner.State.StatStagesOf(Flip(side));

        public VolatileFlags VolatilesOf(BattleSide side) => _inner.State.VolatilesOf(Flip(side));

        /// <summary>Not side-bearing: a species has been scouted or it has not.</summary>
        public bool HasScouted(int speciesId) => _inner.State.HasScouted(speciesId);

        // ---- helpers ---------------------------------------------------------------------

        private static BattleAction Restamp(BattleAction action, BattleSide side)
        {
            if (action.Side == side) return action;

            switch (action.Type)
            {
                case BattleAction.Kind.Move: return BattleAction.UseMove(side, action.MoveIndex);
                case BattleAction.Kind.Switch: return BattleAction.SwitchTo(side, action.PartyIndex);
                case BattleAction.Kind.Item: return BattleAction.UseItem(side, action.ItemId, action.PartyIndex);
                case BattleAction.Kind.Capture: return BattleAction.Capture(side, action.ItemId);
                default: return BattleAction.Run(side);
            }
        }
    }
}
