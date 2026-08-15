using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>How hard the opponent plays. The knob the encounter designer turns.</summary>
    public enum AiDifficulty
    {
        /// <summary>Wild creatures: aggressive but erratic, and they never switch.</summary>
        Wild = 0,
        /// <summary>An early-route trainer: attacks sensibly, ignores support play.</summary>
        Rookie = 1,
        /// <summary>The default trainer: values status, boosts and switching.</summary>
        Standard = 2,
        /// <summary>A gym-tier trainer: no noise, and it plays around a predicted KO.</summary>
        Ace = 3,
    }

    /// <summary>
    /// The opponent's decision policy.
    ///
    /// Not random: every legal action is scored on expected damage, hit chance, KO
    /// potential, type advantage and — for trainers — the value of switching, and the best
    /// score wins. Difficulty is a noise term plus a set of feature gates, so one policy
    /// covers a panicking Rattata and a gym leader.
    ///
    /// It draws from a fork of the battle RNG rather than the battle stream itself, so
    /// retuning the AI cannot shift the damage rolls of an existing replay.
    /// </summary>
    public class BattleAi
    {
        /// <summary>Score below which a trainer starts seriously considering a switch.</summary>
        private const float SwitchConsiderationThreshold = 22f;
        /// <summary>Flat penalty for giving up a turn to switch.</summary>
        private const float SwitchTurnCost = 34f;
        private const float KoBonus = 70f;
        private const float PriorityKoBonus = 25f;

        private readonly List<ScoredAction> _scored = new List<ScoredAction>(12);

        public BattleAi(AiDifficulty difficulty = AiDifficulty.Standard)
        {
            Difficulty = difficulty;
        }

        /// <summary>The difficulty knob. Safe to change between turns.</summary>
        public AiDifficulty Difficulty { get; set; }

        private bool ConsidersSwitching => Difficulty >= AiDifficulty.Standard;
        private bool ValuesSupportMoves => Difficulty >= AiDifficulty.Standard;

        /// <summary>Score jitter, in the same units as the scores themselves.</summary>
        private int Noise => Difficulty switch
        {
            AiDifficulty.Wild => 45,
            AiDifficulty.Rookie => 26,
            AiDifficulty.Standard => 10,
            _ => 0,
        };

        private readonly struct ScoredAction
        {
            public readonly BattleAction Action;
            public readonly float Score;

            public ScoredAction(BattleAction action, float score)
            {
                Action = action; Score = score;
            }
        }

        /// <summary>Picks this side's action for the turn.</summary>
        public virtual BattleAction ChooseAction(BattleEngine engine, BattleSide side)
        {
            var state = engine.State;
            var legal = engine.LegalActions(side);
            if (legal.Count == 0) return BattleAction.UseMove(side, -1);

            // A fork keyed on the turn: deterministic, but independent of the battle's own
            // draw sequence so AI changes never perturb an existing replay's damage rolls.
            var rng = engine.Random.Fork(0x5A17 + state.TurnNumber * 7 + (int)side);

            // How badly the active creature is about to be hurt. This is the term that
            // makes a switch worth a turn: pivoting is cheap when staying in means dying.
            var incomingThreat = ConsidersSwitching ? IncomingThreatFraction(engine, side) : 0f;
            var ownHp = state.ActiveOf(side)?.HpFraction ?? 1f;
            var facingAKo = incomingThreat >= ownHp * 0.9f;

            _scored.Clear();
            var bestMoveScore = float.MinValue;

            for (var i = 0; i < legal.Count; i++)
            {
                var action = legal[i];
                float score;

                switch (action.Type)
                {
                    case BattleAction.Kind.Move:
                        score = ScoreMove(engine, side, action.MoveIndex);
                        if (score > bestMoveScore) bestMoveScore = score;
                        break;
                    case BattleAction.Kind.Switch:
                        if (!ConsidersSwitching) continue;
                        score = ScoreSwitch(engine, side, action.PartyIndex, incomingThreat);
                        break;
                    default:
                        // Capture and Run are player-only affordances.
                        continue;
                }

                _scored.Add(new ScoredAction(action, score));
            }

            if (_scored.Count == 0) return BattleAction.UseMove(side, -1);

            // Switching is only on the table when staying in is going badly or is about to
            // be fatal; without this the AI pivots on a marginal type read every turn and
            // hands over the tempo for nothing.
            var allowSwitch = ConsidersSwitching && (facingAKo || bestMoveScore < SwitchConsiderationThreshold);

            var noise = Noise;
            var best = _scored[0];
            var bestScore = float.MinValue;
            var chosen = false;

            for (var i = 0; i < _scored.Count; i++)
            {
                var candidate = _scored[i];
                if (candidate.Action.Type == BattleAction.Kind.Switch && !allowSwitch) continue;

                var score = candidate.Score + (noise > 0 ? rng.Next(noise) : 0);
                if (!chosen || score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    chosen = true;
                }
            }

            return chosen ? best.Action : _scored[0].Action;
        }

        /// <summary>Picks which party member replaces a fainted one.</summary>
        public virtual int ChooseReplacement(BattleEngine engine, BattleSide side)
        {
            var state = engine.State;
            var party = state.PartyOf(side);
            var bestIndex = -1;
            var bestScore = float.MinValue;

            for (var i = 0; i < party.Count; i++)
            {
                var member = party[i];
                if (member == null || member.IsFainted) continue;

                var score = MatchupScore(engine, side, member) + member.HpFraction * 20f;
                if (score <= bestScore) continue;

                bestScore = score;
                bestIndex = i;
            }

            return bestIndex;
        }

        // ---- Scoring ----------------------------------------------------------------

        /// <summary>
        /// Value of using a move this turn. Damaging moves are scored on the share of the
        /// target's remaining HP they are expected to remove, discounted by hit chance.
        /// </summary>
        protected virtual float ScoreMove(BattleEngine engine, BattleSide side, int moveIndex)
        {
            var move = engine.MoveAt(side, moveIndex);
            if (move == null) return 0f;

            var forecast = engine.ForecastMove(side, moveIndex);
            var target = engine.State.ActiveOf(BattleState.Other(side));
            if (target == null) return 0f;

            if (move.Category == MoveCategory.Status)
                return ValuesSupportMoves ? ScoreStatusMove(engine, side, move, target) : 1f;

            var targetHpFraction = target.HpFraction;
            var score = forecast.ExpectedFraction * 100f * forecast.HitChance;

            // Type read is already inside ExpectedFraction; this is the extra weight a
            // trainer gives to pressing a clear advantage.
            score += (forecast.TypeMultiplier - 1f) * 8f;

            var expectedRemoval = forecast.ExpectedFraction;
            if (expectedRemoval >= targetHpFraction)
            {
                score += KoBonus * forecast.HitChance;
                if (move.Priority > 0) score += PriorityKoBonus;
            }
            else if (Difficulty >= AiDifficulty.Ace && forecast.MaxFraction >= targetHpFraction)
            {
                // A high roll would do it — worth a little more than the average says.
                score += 12f * forecast.HitChance;
            }

            return score;
        }

        /// <summary>Value of a support move: only worth a turn while the effect is still available.</summary>
        protected virtual float ScoreStatusMove(BattleEngine engine, BattleSide side, MoveData move, CreatureInstance target)
        {
            var state = engine.State;
            var self = state.ActiveOf(side);
            if (self == null) return 0f;

            var score = 0f;
            var targetSide = move.TargetsSelf ? side : BattleState.Other(side);
            var stages = state.StatStagesOf(targetSide);

            if (move.StatChanges != null)
            {
                for (var i = 0; i < move.StatChanges.Length; i++)
                {
                    var change = move.StatChanges[i];
                    var index = (int)change.Stat;
                    if (index < 0 || index >= stages.Count) continue;

                    var current = stages[index];
                    var after = StatMath.Clamp(current + change.Stages);
                    if (after == current) continue;

                    // A boost is worth more early, when there is time to cash it in.
                    var headroom = Math.Abs(after - current);
                    score += headroom * 11f * (move.TargetsSelf ? self.HpFraction : 1f);
                }
            }

            if (move.InflictsStatus != StatusCondition.None)
            {
                if (target.Status != StatusCondition.None) return score;

                score += move.InflictsStatus switch
                {
                    StatusCondition.Sleep => 40f,
                    StatusCondition.Paralysis => engine.EffectiveSpeed(BattleState.Other(side)) > engine.EffectiveSpeed(side) ? 38f : 22f,
                    StatusCondition.BadPoison => 30f,
                    StatusCondition.Burn => 28f,
                    StatusCondition.Poison => 24f,
                    StatusCondition.Freeze => 34f,
                    _ => 10f,
                };
            }

            if (move.HealRatio > 0f)
                score += move.HealRatio * 100f * (1f - self.HpFraction);

            if ((move.InflictsVolatile & VolatileFlags.LeechSeeded) != 0 &&
                (state.VolatilesOf(BattleState.Other(side)) & VolatileFlags.LeechSeeded) == 0)
                score += 26f;

            if ((move.InflictsVolatile & VolatileFlags.Confused) != 0 &&
                (state.VolatilesOf(BattleState.Other(side)) & VolatileFlags.Confused) == 0)
                score += 20f;

            if ((move.InflictsVolatile & VolatileFlags.Protected) != 0)
                score += 8f;

            return score;
        }

        /// <summary>
        /// Value of pivoting to a benched creature, net of the turn it costs.
        /// <paramref name="incomingThreat"/> is the fraction of the active creature's max
        /// HP the opponent is expected to remove next turn — the higher it is, the less a
        /// forfeited turn is actually worth.
        /// </summary>
        protected virtual float ScoreSwitch(BattleEngine engine, BattleSide side, int partyIndex, float incomingThreat)
        {
            var party = engine.State.PartyOf(side);
            if (partyIndex < 0 || partyIndex >= party.Count) return float.MinValue;

            var candidate = party[partyIndex];
            if (candidate == null || candidate.IsFainted) return float.MinValue;

            var score = MatchupScore(engine, side, candidate);
            score += candidate.HpFraction * 15f;
            score += Math.Min(1f, incomingThreat) * 55f;

            // Bringing in something that is already hurt to eat a hit is rarely right.
            if (candidate.HpFraction < 0.35f) score -= 20f;

            return score - SwitchTurnCost;
        }

        /// <summary>
        /// Largest share of the active creature's HP the opponent can expect to take next
        /// turn, discounted by hit chance. RNG-free: it is built entirely from forecasts.
        /// </summary>
        private static float IncomingThreatFraction(BattleEngine engine, BattleSide side)
        {
            var foeSide = BattleState.Other(side);
            var foe = engine.State.ActiveOf(foeSide);
            if (foe?.Moves == null) return 0f;

            var worst = 0f;
            for (var i = 0; i < foe.Moves.Count; i++)
            {
                var forecast = engine.ForecastMove(foeSide, i);
                var threat = forecast.ExpectedFraction * forecast.HitChance;
                if (threat > worst) worst = threat;
            }

            return worst;
        }

        /// <summary>
        /// Cheap species-level read of how a candidate fares against the current opponent:
        /// what it can hit for, and what it will be hit for. Used for switches, where a
        /// live forecast is unavailable because the candidate is not on the field.
        /// </summary>
        private float MatchupScore(BattleEngine engine, BattleSide side, CreatureInstance candidate)
        {
            var chart = engine.TypeChart;
            var foe = engine.State.ActiveOf(BattleState.Other(side));
            if (chart == null || foe == null) return 0f;

            if (!engine.TryGetSpecies(candidate.SpeciesId, out var mine) ||
                !engine.TryGetSpecies(foe.SpeciesId, out var theirs))
                return 0f;

            var bestOutgoing = BestOffensiveMultiplier(engine, chart, candidate, mine, theirs);
            var worstIncoming = BestOffensiveMultiplier(engine, chart, foe, theirs, mine);

            // Both terms are centred on 1.0 (a neutral matchup) so a wash scores zero.
            return (bestOutgoing - 1f) * 26f + (1f - worstIncoming) * 24f;
        }

        private static float BestOffensiveMultiplier(BattleEngine engine, ITypeChart chart,
            CreatureInstance attacker, SpeciesData attackerSpecies, SpeciesData defenderSpecies)
        {
            var best = 0f;
            var sawMove = false;

            var registry = engine.MoveRegistry;
            if (registry != null && attacker.Moves != null)
            {
                for (var i = 0; i < attacker.Moves.Count; i++)
                {
                    var slot = attacker.Moves[i];
                    if (!slot.Usable || !registry.TryGet(slot.MoveId, out var move)) continue;
                    if (move.Category == MoveCategory.Status || move.Power <= 0) continue;

                    sawMove = true;
                    var multiplier = chart.Multiplier(move.Type, defenderSpecies.Type1, defenderSpecies.Type2);
                    if (multiplier > best) best = multiplier;
                }
            }

            if (sawMove) return best;

            // No usable attacking move known — fall back to the species' own types, which is
            // the best guess available for an opponent whose moves have not been revealed.
            best = chart.Multiplier(attackerSpecies.Type1, defenderSpecies.Type1, defenderSpecies.Type2);
            if (attackerSpecies.Type2 != ElementType.None)
            {
                var second = chart.Multiplier(attackerSpecies.Type2, defenderSpecies.Type1, defenderSpecies.Type2);
                if (second > best) best = second;
            }

            return best;
        }
    }
}
