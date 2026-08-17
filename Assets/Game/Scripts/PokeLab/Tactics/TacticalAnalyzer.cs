using System;
using System.Collections.Generic;
using PokeLab.Core;
using PokeLab.Intelligence.Data;
using PokeLab.Intelligence.Model;

namespace PokeLab.Intelligence.Tactics
{
    /// <summary>
    /// Turns the species-level forest prior into advice about the battle actually on the
    /// field: what can kill you next turn, what to switch to, and what to press.
    ///
    /// The forest only knows "Pikachu tends to beat Geodude". It has never seen a HP bar,
    /// a paralysis, or a party. Everything in here exists to fold that missing state back
    /// in without discarding the prior.
    /// </summary>
    public sealed class TacticalAnalyzer
    {
        private readonly ForestOracle _oracle;
        private readonly SpeciesRegistry _species;
        private readonly PokeLabTypeChart _chart;
        private readonly IMoveRegistry _moves;

        // Trend tracking for DeltaPoints. Keyed on nothing but recency: a readout is only
        // ever compared with the previous one from the same battle.
        private float _previousLive = float.NaN;
        private int _previousTurn = -1;

        public TacticalAnalyzer(ForestOracle oracle, SpeciesRegistry species,
                                PokeLabTypeChart chart, IMoveRegistry moves)
        {
            _oracle = oracle ?? throw new ArgumentNullException(nameof(oracle));
            _species = species ?? throw new ArgumentNullException(nameof(species));
            _chart = chart ?? throw new ArgumentNullException(nameof(chart));
            // Injected rather than resolved from ServiceHub so the whole tactical layer
            // stays engine-free and can be exercised from the verification harness.
            _moves = moves;
        }

        // --- The blending model ------------------------------------------------------
        //
        // Everything is combined in log-odds, so each factor is an independent additive
        // shift and the result cannot leave (0, 1) no matter how the terms stack.
        //
        //   z = PriorWeight  * logit(prior)
        //     + RaceWeight   * (opponentTurnsToKo - playerTurnsToKo), clamped to +-3 turns
        //     + HpWeight     * (playerHpFraction - opponentHpFraction)
        //     + StatusWeight * (opponentStatusBurden - playerStatusBurden)
        //     + StageWeight  * (playerStageScore - opponentStageScore)
        //     + PartyWeight  * (playerReserves - opponentReserves), capped at 5 a side
        //     + CoverageWeight * (playerCoverage - opponentCoverage)
        //
        // The race term dominates on purpose. Who KOs whom first is the single fact that
        // decides most exchanges, and it is the one thing the species prior cannot see: a
        // 90% favourite that needs four turns to break through loses to something that
        // needs two. The remaining terms are corrections, sized so no one of them can flip
        // a decided race on its own.
        //
        // PriorWeight shrinks the forest's log-odds rather than taking them at face value.
        // The model was trained to call a winner from a completed battle record, so it is
        // extremely confident — real matchups come back at 0.99 or 0.001, and a species
        // prior that strong would pin the live readout to the rails before the battle
        // state was even considered. Halving its log-odds keeps the ranking it provides
        // while leaving the middle of the range for what is happening on the field, which
        // is the number the player is actually reading.

        private const double PriorWeight = 0.45;
        private const double RaceWeight = 0.85;
        private const double HpWeight = 2.0;
        private const double StatusWeight = 1.0;
        private const double StageWeight = 0.55;
        private const double PartyWeight = 0.8;
        private const double CoverageWeight = 0.9;

        /// <summary>Turns-to-KO beyond this are indistinguishable in practice.</summary>
        private const int MaxRaceTurns = 6;

        public TacticalReadout Analyse(IBattleStateView state, IBattleEngine engine)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var player = state.ActiveOf(BattleSide.Player);
            var opponent = state.ActiveOf(BattleSide.Opponent);
            if (player == null || opponent == null) return EmptyReadout();

            var matchup = _oracle.Predict(player.SpeciesId, opponent.SpeciesId);
            _species.TryGet(player.SpeciesId, out var playerSpecies);
            _species.TryGet(opponent.SpeciesId, out var opponentSpecies);

            var playerMoves = ForecastAll(engine, state, BattleSide.Player);
            var opponentMoves = ForecastAll(engine, state, BattleSide.Opponent);

            var threats = BuildThreats(opponent, player, opponentSpecies, playerSpecies, opponentMoves);
            var live = BlendLive(state, matchup.FirstWinProbability, player, opponent,
                                 playerMoves, opponentMoves, playerSpecies, opponentSpecies);

            var switches = BuildSwitches(state, engine, opponent, opponentSpecies, player,
                                         opponentMoves, live);

            var readout = new TacticalReadout
            {
                Matchup = matchup,
                LiveWinProbability = live,
                DeltaPoints = ComputeDelta(state.TurnNumber, live),
                Threats = threats,
                Switches = switches,
                MoveForecasts = Extract(playerMoves),
                TypeSummary = BuildTypeSummary(playerSpecies, opponentSpecies, matchup),
                IsConfident = state.HasScouted(opponent.SpeciesId),
            };

            readout.RecommendedLine = BuildRecommendation(state, player, opponent, playerSpecies,
                                                          opponentSpecies, playerMoves, threats,
                                                          switches, live, readout.IsConfident);
            return readout;
        }

        // --- Live probability ---------------------------------------------------------

        private float BlendLive(IBattleStateView state, float prior,
                                CreatureInstance player, CreatureInstance opponent,
                                MoveForecast[] playerMoves, MoveForecast[] opponentMoves,
                                SpeciesData playerSpecies, SpeciesData opponentSpecies)
        {
            var z = PriorWeight * Logit(Clamp01(prior, 0.001f, 0.999f));

            // Race: how many turns each side needs to finish the other, with the mover
            // advantage folded in as half a turn.
            var playerTurns = TurnsToKo(playerMoves, opponent.HpFraction);
            var opponentTurns = TurnsToKo(opponentMoves, player.HpFraction);

            var playerFirst = MovesFirst(state, player, opponent);
            var race = (opponentTurns - playerTurns) + (playerFirst ? 0.5d : -0.5d);
            z += RaceWeight * Math.Max(-3d, Math.Min(3d, race));

            z += HpWeight * (player.HpFraction - opponent.HpFraction);
            z += StatusWeight * (StatusBurden(opponent) - StatusBurden(player));
            z += StageWeight * (StageScore(state.StatStagesOf(BattleSide.Player))
                                - StageScore(state.StatStagesOf(BattleSide.Opponent)));
            z += PartyWeight * (Reserves(state, BattleSide.Player) - Reserves(state, BattleSide.Opponent));

            // Coverage: does each side actually own a move that hurts the other? A
            // creature that cannot touch its opponent is losing regardless of stats.
            z += CoverageWeight * (Coverage(playerMoves) - Coverage(opponentMoves));

            return CertaintyGuard((float)Sigmoid(z));
        }

        /// <summary>
        /// The scanner never claims a battle is over. Damage rolls, crits, flinches and a
        /// full bench all live outside this model, so a readout of 0% or 100% would be a
        /// promise the device cannot keep — and the one time it is wrong is the time the
        /// player stops trusting it. Two points of doubt at each end costs nothing in the
        /// range players actually read.
        /// </summary>
        private static float CertaintyGuard(float probability) =>
            probability < 0.02f ? 0.02f : probability > 0.98f ? 0.98f : probability;

        /// <summary>
        /// Expected turns to knock the target out at the average roll. Uses the best move
        /// available; a side with nothing that damages returns the ceiling, which is what
        /// drives the "nothing you have dents it" advice.
        /// </summary>
        private static double TurnsToKo(MoveForecast[] forecasts, float targetHpFraction)
        {
            var best = 0d;
            foreach (var forecast in forecasts)
            {
                if (!forecast.Usable) continue;
                // Weight by hit chance: a 70% accurate move really does take longer.
                var perTurn = forecast.Value.ExpectedFraction * Math.Max(0.05f, forecast.Value.HitChance);
                if (perTurn > best) best = perTurn;
            }
            if (best <= 0d) return MaxRaceTurns;
            return Math.Min(MaxRaceTurns, Math.Max(1d, targetHpFraction / best));
        }

        private static double Coverage(MoveForecast[] forecasts)
        {
            // 0 when nothing lands meaningful damage, 1 when something takes a quarter of
            // the target's health per turn. Saturating deliberately: past that point the
            // race term already carries the information.
            var best = 0d;
            foreach (var forecast in forecasts)
            {
                if (!forecast.Usable) continue;
                var perTurn = forecast.Value.ExpectedFraction * Math.Max(0.05f, forecast.Value.HitChance);
                if (perTurn > best) best = perTurn;
            }
            return Math.Min(1d, best / 0.25d);
        }

        /// <summary>Cost of a side's non-volatile status, in log-odds against it.</summary>
        private static double StatusBurden(CreatureInstance creature)
        {
            switch (creature.Status)
            {
                case StatusCondition.Freeze: return 1.2d;
                case StatusCondition.Sleep: return 0.8d;
                case StatusCondition.Paralysis: return 0.55d;
                case StatusCondition.BadPoison: return 0.5d;
                case StatusCondition.Burn: return 0.45d;
                case StatusCondition.Poison: return 0.3d;
                default: return 0d;
            }
        }

        /// <summary>
        /// Stat stages weighted by how much each actually decides a battle. Offence and
        /// speed matter more than accuracy or evasion, and the whole score is scaled so a
        /// full +2 sweep is worth roughly one HP bar rather than the battle.
        /// </summary>
        private static double StageScore(IReadOnlyList<int> stages)
        {
            if (stages == null) return 0d;
            var score = 0d;
            for (var stat = 0; stat < stages.Count; stat++)
            {
                var weight = (StatKind)stat switch
                {
                    StatKind.Attack => 0.30d,
                    StatKind.SpAttack => 0.30d,
                    StatKind.Speed => 0.25d,
                    StatKind.Defense => 0.20d,
                    StatKind.SpDefense => 0.20d,
                    StatKind.Accuracy => 0.15d,
                    StatKind.Evasion => 0.15d,
                    _ => 0d,
                };
                score += weight * Math.Max(-6, Math.Min(6, stages[stat]));
            }
            return score;
        }

        /// <summary>
        /// Healthy bodies still in reserve, normalised against a full bench of five.
        ///
        /// Deliberately a count and not a fraction of the party: one healthy reserve out of
        /// one is not the same asset as five out of five, and a fraction would score them
        /// identically. Capped so an oversized party cannot dominate the blend.
        /// </summary>
        private static double Reserves(IBattleStateView state, BattleSide side)
        {
            var party = state.PartyOf(side);
            if (party == null || party.Count == 0) return 0d;
            var active = state.ActiveOf(side);
            var healthy = 0;
            foreach (var member in party)
            {
                if (member == null || ReferenceEquals(member, active)) continue;
                if (!member.IsFainted) healthy++;
            }
            return Math.Min(5, healthy) / 5d;
        }

        private static bool MovesFirst(IBattleStateView state, CreatureInstance player, CreatureInstance opponent)
        {
            var playerSpeed = EffectiveSpeed(player, state.StatStagesOf(BattleSide.Player));
            var opponentSpeed = EffectiveSpeed(opponent, state.StatStagesOf(BattleSide.Opponent));
            return playerSpeed >= opponentSpeed;
        }

        private static double EffectiveSpeed(CreatureInstance creature, IReadOnlyList<int> stages)
        {
            var speed = creature.Stats != null && creature.Stats.Length > (int)StatKind.Speed
                ? creature.Stats[(int)StatKind.Speed]
                : 1;
            var stage = stages != null && stages.Count > (int)StatKind.Speed ? stages[(int)StatKind.Speed] : 0;
            stage = Math.Max(-6, Math.Min(6, stage));
            var multiplier = stage >= 0 ? (2d + stage) / 2d : 2d / (2d - stage);
            // Paralysis halves speed from gen 7 onward, which is the ruleset the slice uses.
            if (creature.Status == StatusCondition.Paralysis) multiplier *= 0.5d;
            return speed * multiplier;
        }

        // --- Threats ------------------------------------------------------------------

        private static ThreatAssessment[] BuildThreats(CreatureInstance opponent, CreatureInstance player,
                                                       SpeciesData opponentSpecies, SpeciesData playerSpecies,
                                                       MoveForecast[] opponentMoves)
        {
            var opponentName = DisplayName(opponent, opponentSpecies);

            var usable = 0;
            foreach (var forecast in opponentMoves) if (forecast.Usable) usable++;

            if (usable == 0)
            {
                // Either the opponent has no moves loaded yet or the engine cannot forecast
                // for it. Say so rather than reporting a reassuring empty threat list.
                return new[]
                {
                    new ThreatAssessment
                    {
                        Level = ThreatLevel.Dangerous,
                        Headline = PokeLabStrings.UnknownThreat(opponentName),
                        Detail = PokeLabStrings.NotScouted(opponentName),
                        SourceMoveId = null,
                        IncomingKoChance = 0f,
                    }
                };
            }

            var threats = new List<ThreatAssessment>(usable);
            foreach (var forecast in opponentMoves)
            {
                if (!forecast.Usable) continue;
                var damage = forecast.Value;
                var koChance = KnockoutChance(damage, player.HpFraction);
                var level = ClassifyThreat(koChance, damage.ExpectedFraction, player.HpFraction);
                var moveName = forecast.DisplayName ?? "an unknown move";

                threats.Add(new ThreatAssessment
                {
                    Level = level,
                    Headline = PokeLabStrings.ThreatHeadline(level, opponentName, moveName),
                    Detail = PokeLabStrings.ThreatDetail(moveName, forecast.MoveType,
                                                         damage.ExpectedFraction, koChance,
                                                         damage.Effectiveness),
                    SourceMoveId = forecast.MoveId,
                    IncomingKoChance = koChance,
                });
            }

            // A status the player is already carrying is a standing threat in its own
            // right — it is costing turns every round whether or not anything attacks.
            var statusNote = PokeLabStrings.StatusNote(player.Status, DisplayName(player, playerSpecies));
            if (statusNote != null)
            {
                threats.Add(new ThreatAssessment
                {
                    Level = ThreatLevel.Manageable,
                    Headline = statusNote,
                    Detail = statusNote,
                    SourceMoveId = null,
                    IncomingKoChance = 0f,
                });
            }

            // Worst first: the player should read the thing that kills them at the top.
            threats.Sort((a, b) =>
            {
                var byLevel = b.Level.CompareTo(a.Level);
                return byLevel != 0 ? byLevel : b.IncomingKoChance.CompareTo(a.IncomingKoChance);
            });
            return threats.ToArray();
        }

        /// <summary>
        /// Chance the move ends the target this turn. Damage rolls are uniform across the
        /// 85-100% band, so the probability the roll clears the remaining health is just
        /// the share of the band above it — scaled by the chance of connecting at all.
        /// </summary>
        private static float KnockoutChance(DamageForecast damage, float remainingHpFraction)
        {
            if (remainingHpFraction <= 0f) return 0f;
            if (damage.MaxFraction < remainingHpFraction) return 0f;

            var hit = damage.HitChance <= 0f ? 1f : damage.HitChance;
            var span = damage.MaxFraction - damage.MinFraction;
            if (span <= 0f) return damage.MinFraction >= remainingHpFraction ? hit : 0f;

            var share = (damage.MaxFraction - remainingHpFraction) / span;
            return hit * Math.Max(0f, Math.Min(1f, share));
        }

        private static ThreatLevel ClassifyThreat(float koChance, float expectedFraction, float remainingHp)
        {
            if (koChance >= 0.5f) return ThreatLevel.Lethal;
            if (koChance > 0f || expectedFraction >= remainingHp * 0.6f) return ThreatLevel.Dangerous;
            if (expectedFraction >= 0.2f) return ThreatLevel.Manageable;
            return ThreatLevel.Trivial;
        }

        // --- Switches -----------------------------------------------------------------

        private SwitchRecommendation[] BuildSwitches(IBattleStateView state, IBattleEngine engine,
                                                     CreatureInstance opponent, SpeciesData opponentSpecies,
                                                     CreatureInstance active, MoveForecast[] opponentMoves,
                                                     float stayingProbability)
        {
            var party = state.PartyOf(BattleSide.Player);
            if (party == null || party.Count == 0) return Array.Empty<SwitchRecommendation>();

            // The single hardest-hitting thing the opponent has, which is what the incoming
            // creature eats for free on the switch turn.
            MoveForecast worst = default;
            var worstDamage = -1f;
            foreach (var forecast in opponentMoves)
            {
                if (!forecast.Usable) continue;
                var expected = forecast.Value.ExpectedFraction * Math.Max(0.05f, forecast.Value.HitChance);
                if (expected > worstDamage) { worstDamage = expected; worst = forecast; }
            }

            var results = new List<SwitchRecommendation>(party.Count);
            for (var index = 0; index < party.Count; index++)
            {
                var candidate = party[index];
                if (candidate == null || candidate.IsFainted) continue;
                if (ReferenceEquals(candidate, active)) continue;

                if (!_species.TryGet(candidate.SpeciesId, out var candidateSpecies)) continue;

                var freeHit = EstimateSwitchInDamage(worst, worstDamage, active, candidate,
                                                     candidateSpecies, opponentSpecies);
                var arrivingHp = Math.Max(0f, candidate.HpFraction - freeHit);

                var projected = ProjectSwitch(state, candidate, candidateSpecies, opponent,
                                              opponentSpecies, arrivingHp);
                var gain = 100f * (projected - stayingProbability);

                results.Add(new SwitchRecommendation
                {
                    PartyIndex = index,
                    InstanceId = candidate.InstanceId,
                    ProjectedWinProbability = projected,
                    GainPoints = gain,
                    Rationale = BuildSwitchRationale(candidate, candidateSpecies, gain, freeHit, worst),
                });
            }

            results.Sort((a, b) => b.GainPoints.CompareTo(a.GainPoints));
            return results.ToArray();
        }

        /// <summary>
        /// What the incoming creature takes on the switch turn.
        ///
        /// The engine can only forecast against whoever is currently active, so the known
        /// damage to the active creature is rescaled onto the candidate by the three things
        /// that actually differ: type effectiveness, the relevant defensive stat, and the
        /// health pool the damage is measured against.
        /// </summary>
        private float EstimateSwitchInDamage(MoveForecast worst, float worstDamage,
                                             CreatureInstance active, CreatureInstance candidate,
                                             SpeciesData candidateSpecies, SpeciesData opponentSpecies)
        {
            if (!worst.Usable || worstDamage <= 0f) return 0f;
            if (!_species.TryGet(active.SpeciesId, out var activeSpecies)) return worstDamage;

            var againstActive = _chart.Multiplier(worst.MoveType, activeSpecies.Type1, activeSpecies.Type2);
            var againstCandidate = _chart.Multiplier(worst.MoveType, candidateSpecies.Type1, candidateSpecies.Type2);
            // An immune active tells us nothing about the candidate, so fall back to the
            // candidate's own multiplier rather than dividing by zero.
            var typeRatio = againstActive > 0f ? againstCandidate / againstActive : againstCandidate;

            var defenceStat = worst.Category == MoveCategory.Special ? StatKind.SpDefense : StatKind.Defense;
            var activeDefence = StatOf(active, defenceStat);
            var candidateDefence = StatOf(candidate, defenceStat);
            var defenceRatio = candidateDefence > 0 ? (float)activeDefence / candidateDefence : 1f;

            var hpRatio = candidate.MaxHp > 0 ? (float)active.MaxHp / candidate.MaxHp : 1f;

            return Math.Max(0f, Math.Min(1f, worstDamage * typeRatio * defenceRatio * hpRatio));
        }

        /// <summary>
        /// Win probability with the candidate in, at the health it arrives with. Uses the
        /// same log-odds blend as the live number but without move forecasts, which the
        /// engine cannot produce for a creature that is not on the field yet — the species
        /// prior plus typing carries it instead.
        /// </summary>
        private float ProjectSwitch(IBattleStateView state, CreatureInstance candidate,
                                    SpeciesData candidateSpecies, CreatureInstance opponent,
                                    SpeciesData opponentSpecies, float arrivingHp)
        {
            var prior = _oracle.Predict(candidate.SpeciesId, opponent.SpeciesId).FirstWinProbability;
            // Same prior shrink as the live blend, so a projected number is directly
            // comparable with the one the player is already looking at.
            var z = PriorWeight * Logit(Clamp01(prior, 0.001f, 0.999f));

            z += HpWeight * (arrivingHp - opponent.HpFraction);
            z += StatusWeight * (StatusBurden(opponent) - StatusBurden(candidate));
            // The opponent keeps its stat stages through the switch; ours reset.
            z += StageWeight * (0d - StageScore(state.StatStagesOf(BattleSide.Opponent)));
            z += PartyWeight * (Reserves(state, BattleSide.Player) - Reserves(state, BattleSide.Opponent));

            // Typing stands in for the coverage term: the prior already encodes species
            // typing, so only the swing relative to an even matchup is added here.
            if (candidateSpecies != null && opponentSpecies != null)
            {
                var ours = MatchupFeatures.BestEffect(candidateSpecies, opponentSpecies, _chart, false, out _, out _);
                var theirs = MatchupFeatures.BestEffect(opponentSpecies, candidateSpecies, _chart, false, out _, out _);
                z += 0.35d * (MatchupFeatures.EffectScore(ours) - MatchupFeatures.EffectScore(theirs));
            }

            return CertaintyGuard((float)Sigmoid(z));
        }

        private static string BuildSwitchRationale(CreatureInstance candidate, SpeciesData candidateSpecies,
                                                   float gain, float freeHit, MoveForecast worst)
        {
            var name = DisplayName(candidate, candidateSpecies);
            if (gain <= 0f) return PokeLabStrings.SwitchLoss(name);

            var reason = worst.Usable && worst.MoveType != ElementType.None
                ? PokeLabStrings.SwitchReasonTyping(worst.MoveType)
                : PokeLabStrings.SwitchReasonBulk();

            var line = PokeLabStrings.SwitchGain(name, gain, reason);
            if (freeHit >= 0.15f)
                line += " " + PokeLabStrings.SwitchCostsFreeHit(name, freeHit) + ".";
            return line;
        }

        // --- Prose --------------------------------------------------------------------

        private static string BuildTypeSummary(SpeciesData player, SpeciesData opponent, MatchupPrediction matchup)
        {
            if (player == null || opponent == null) return string.Empty;
            return PokeLabStrings.TypeSummary(PokeLabStrings.SpeciesName(player),
                                              PokeLabStrings.SpeciesName(opponent),
                                              opponent.Type1, opponent.Type2,
                                              matchup.TypeEffectFirst, matchup.TypeEffectSecond);
        }

        private string BuildRecommendation(IBattleStateView state, CreatureInstance player,
                                           CreatureInstance opponent, SpeciesData playerSpecies,
                                           SpeciesData opponentSpecies, MoveForecast[] playerMoves,
                                           ThreatAssessment[] threats, SwitchRecommendation[] switches,
                                           float live, bool confident)
        {
            var playerName = DisplayName(player, playerSpecies);
            var opponentName = DisplayName(opponent, opponentSpecies);

            // Finishing the opponent outranks everything else.
            MoveForecast finisher = default;
            var finisherDamage = 0f;
            foreach (var forecast in playerMoves)
            {
                if (!forecast.Usable) continue;
                if (forecast.Value.MinFraction >= opponent.HpFraction &&
                    forecast.Value.HitChance >= 0.85f &&
                    forecast.Value.ExpectedFraction > finisherDamage)
                {
                    finisher = forecast;
                    finisherDamage = forecast.Value.ExpectedFraction;
                }
            }
            if (finisher.Usable) return PokeLabStrings.RecommendKo(finisher.DisplayName);

            // Then: are we about to die, and is there a better body to send in?
            var lethal = threats.Length > 0 && threats[0].Level == ThreatLevel.Lethal;
            var bestSwitch = switches.Length > 0 ? switches[0] : default;
            var hasSwitch = switches.Length > 0 && bestSwitch.GainPoints > 0f;

            if (lethal && hasSwitch && bestSwitch.GainPoints >= 5f)
            {
                if (!_species.TryGet(PartyMemberSpecies(state, bestSwitch.PartyIndex), out var candidateSpecies))
                    candidateSpecies = null;
                return PokeLabStrings.RecommendSwitch(
                    PokeLabStrings.PartyMemberName(candidateSpecies),
                    bestSwitch.ProjectedWinProbability);
            }
            if (lethal && !hasSwitch && player.HpFraction <= 0.35f)
                return PokeLabStrings.RecommendHeal(playerName);

            // Then: the best attacking line, named concretely.
            MoveForecast best = default;
            var bestScore = 0f;
            foreach (var forecast in playerMoves)
            {
                if (!forecast.Usable) continue;
                var score = forecast.Value.ExpectedFraction * Math.Max(0.05f, forecast.Value.HitChance);
                if (score > bestScore) { bestScore = score; best = forecast; }
            }

            if (!confident) return PokeLabStrings.RecommendScout(opponentName);

            if (best.Usable && bestScore > 0.02f)
            {
                // Show what the line is worth, not just its name: the projected number is
                // the live probability nudged by how much of the race this move closes.
                var projected = (float)Sigmoid(Logit(Clamp01(live, 0.001f, 0.999f)) + RaceWeight * Math.Min(1d, bestScore * 4d));
                return PokeLabStrings.RecommendMoveNamed(playerName, best.DisplayName, projected);
            }

            if (hasSwitch && bestSwitch.GainPoints >= 10f)
            {
                if (!_species.TryGet(PartyMemberSpecies(state, bestSwitch.PartyIndex), out var candidateSpecies))
                    candidateSpecies = null;
                return PokeLabStrings.RecommendSwitch(
                    PokeLabStrings.PartyMemberName(candidateSpecies),
                    bestSwitch.ProjectedWinProbability);
            }

            if (bestScore <= 0.02f) return PokeLabStrings.RecommendStall(opponentName);
            return live >= 0.5f ? PokeLabStrings.RecommendHold() : PokeLabStrings.RecommendStall(opponentName);
        }

        private static int PartyMemberSpecies(IBattleStateView state, int partyIndex)
        {
            var party = state.PartyOf(BattleSide.Player);
            if (party == null || partyIndex < 0 || partyIndex >= party.Count) return -1;
            return party[partyIndex]?.SpeciesId ?? -1;
        }

        // --- Move forecasts -----------------------------------------------------------

        /// <summary>A forecast plus the identity the prose needs; DamageForecast alone
        /// cannot say which move it described.</summary>
        private readonly struct MoveForecast
        {
            public readonly bool Usable;
            public readonly DamageForecast Value;
            public readonly string MoveId;
            public readonly string DisplayName;
            public readonly ElementType MoveType;
            public readonly MoveCategory Category;

            public MoveForecast(DamageForecast value, string moveId, string displayName,
                                ElementType moveType, MoveCategory category)
            {
                Usable = true;
                Value = value;
                MoveId = moveId;
                DisplayName = displayName;
                MoveType = moveType;
                Category = category;
            }
        }

        /// <summary>
        /// One entry per move slot, in slot order.
        ///
        /// Slot-aligned rather than compacted, because <see cref="TacticalReadout.MoveForecasts"/>
        /// is indexed by slot: the battle command panel binds forecast <c>i</c> to move
        /// <c>i</c>. Dropping an empty slot or one the engine could not forecast used to
        /// shift every later move's numbers up under the wrong name — a throw on slot 1
        /// showed slot 2's damage against slot 1's move. A slot with nothing to say keeps
        /// its place as a default entry, whose <c>Usable</c> is false; every reader here
        /// already skips those, and the one field that leaves this class carries
        /// <c>default(DamageForecast)</c> in that position.
        /// </summary>
        private MoveForecast[] ForecastAll(IBattleEngine engine, IBattleStateView state, BattleSide side)
        {
            var creature = state.ActiveOf(side);
            if (engine == null || creature?.Moves == null || creature.Moves.Count == 0)
                return Array.Empty<MoveForecast>();

            var results = new MoveForecast[creature.Moves.Count];

            for (var index = 0; index < creature.Moves.Count; index++)
            {
                var slot = creature.Moves[index];
                if (string.IsNullOrEmpty(slot.MoveId)) continue;

                DamageForecast forecast;
                try
                {
                    forecast = engine.ForecastMove(side, index);
                }
                catch (Exception)
                {
                    // The battle engine may not be wired up yet during partial integration.
                    // Degrading to "no forecast" is far better than taking the scanner down.
                    continue;
                }

                var name = slot.MoveId;
                var type = ElementType.None;
                var category = MoveCategory.Physical;
                if (_moves != null && _moves.TryGet(slot.MoveId, out var data))
                {
                    name = PokeLabStrings.MoveName(data);
                    type = data.Type;
                    category = data.Category;
                }

                results[index] = new MoveForecast(forecast, slot.MoveId, name, type, category);
            }
            return results;
        }

        /// <summary>The damage half of each forecast, one per slot and still in slot order.</summary>
        private static DamageForecast[] Extract(MoveForecast[] forecasts)
        {
            var result = new DamageForecast[forecasts.Length];
            for (var i = 0; i < forecasts.Length; i++) result[i] = forecasts[i].Value;
            return result;
        }

        // --- Small helpers ------------------------------------------------------------

        private float ComputeDelta(int turnNumber, float live)
        {
            // A new battle resets the trend rather than inheriting the previous one's.
            if (turnNumber <= 1 || turnNumber < _previousTurn || float.IsNaN(_previousLive))
            {
                _previousLive = live;
                _previousTurn = turnNumber;
                return 0f;
            }
            var delta = 100f * (live - _previousLive);
            _previousLive = live;
            _previousTurn = turnNumber;
            return delta;
        }

        private static string DisplayName(CreatureInstance creature, SpeciesData species)
        {
            // A nickname the player chose always wins; otherwise defer to the string table,
            // which owns which of the two species names this build shows.
            if (creature != null && !string.IsNullOrEmpty(creature.Nickname)) return creature.Nickname;
            return PokeLabStrings.SpeciesName(species);
        }

        private static int StatOf(CreatureInstance creature, StatKind stat)
        {
            var index = (int)stat;
            if (creature?.Stats == null || index >= creature.Stats.Length) return 1;
            return Math.Max(1, creature.Stats[index]);
        }

        private static TacticalReadout EmptyReadout() => new TacticalReadout
        {
            Matchup = new MatchupPrediction(),
            LiveWinProbability = 0.5f,
            RecommendedLine = string.Empty,
            TypeSummary = string.Empty,
            IsConfident = false,
        };

        private static float Clamp01(float value, float low, float high) =>
            value < low ? low : value > high ? high : value;

        private static double Logit(double p) => Math.Log(p / (1d - p));

        private static double Sigmoid(double z) => 1d / (1d + Math.Exp(-z));
    }
}
