using System;
using System.Diagnostics;
using System.Globalization;
using PokeLab.Core;
using PokeLab.Intelligence;
using PokeLab.Intelligence.Data;
using PokeLab.Tests;

namespace PokeLab.Verify
{
    /// <summary>
    /// Drives the tactical layer through scripted battle states and checks that the live
    /// probability actually responds to what happened. The numbers themselves are not
    /// asserted against a reference — there is nothing to compare them to — but the
    /// direction of movement is, because a blend that does not move is worse than useless.
    /// </summary>
    public static class Scenarios
    {
        public static int Run(PokeLabData data)
        {
            var oracle = new PokeLabOracle(data);
            var failures = 0;

            Console.WriteLine();
            Console.WriteLine("--- tactical scenarios (Poliwag vs Geodude) ---");

            var baseline = Analyse(oracle, out var baselineReadout);
            Console.WriteLine($"baseline live:        {Pct(baselineReadout.LiveWinProbability)}");
            Console.WriteLine($"  type summary:       {baselineReadout.TypeSummary}");
            Console.WriteLine($"  recommendation:     {baselineReadout.RecommendedLine}");
            Console.WriteLine($"  threats:            {baselineReadout.Threats.Length}");
            if (baselineReadout.Threats.Length > 0)
            {
                Console.WriteLine($"    top:              {baselineReadout.Threats[0].Headline}");
                Console.WriteLine($"    detail:           {baselineReadout.Threats[0].Detail}");
                Console.WriteLine($"    ko chance:        {Pct(baselineReadout.Threats[0].IncomingKoChance)}");
            }
            Console.WriteLine($"  switches:           {baselineReadout.Switches.Length}");
            if (baselineReadout.Switches.Length > 0)
                Console.WriteLine($"    best:             {baselineReadout.Switches[0].Rationale}");
            Console.WriteLine($"  confident:          {baselineReadout.IsConfident}");

            // Taking a big hit must move the readout down.
            var hurt = Analyse(oracle, out var hurtReadout, playerHealth: 0.2f);
            Console.WriteLine($"after a big hit:      {Pct(hurtReadout.LiveWinProbability)} " +
                              $"({Delta(hurtReadout.LiveWinProbability, baseline)})");
            if (hurtReadout.LiveWinProbability >= baseline)
            {
                Console.WriteLine("  FAIL taking a big hit did not lower the readout");
                failures++;
            }

            // Landing a paralysis must move it up.
            var paralysed = Analyse(oracle, out var paralysedReadout, opponentStatus: StatusCondition.Paralysis);
            Console.WriteLine($"opponent paralysed:   {Pct(paralysedReadout.LiveWinProbability)} " +
                              $"({Delta(paralysedReadout.LiveWinProbability, baseline)})");
            if (paralysedReadout.LiveWinProbability <= baseline)
            {
                Console.WriteLine("  FAIL landing a paralysis did not raise the readout");
                failures++;
            }

            // Being paralysed ourselves must move it down.
            var selfParalysed = Analyse(oracle, out var selfReadout, playerStatus: StatusCondition.Paralysis);
            Console.WriteLine($"we are paralysed:     {Pct(selfReadout.LiveWinProbability)} " +
                              $"({Delta(selfReadout.LiveWinProbability, baseline)})");
            if (selfReadout.LiveWinProbability >= baseline)
            {
                Console.WriteLine("  FAIL our own paralysis did not lower the readout");
                failures++;
            }

            // Chipping the opponent down must move it up.
            var winning = Analyse(oracle, out var winningReadout, opponentHealth: 0.15f);
            Console.WriteLine($"opponent at 15%:      {Pct(winningReadout.LiveWinProbability)} " +
                              $"({Delta(winningReadout.LiveWinProbability, baseline)})");
            if (winningReadout.LiveWinProbability <= baseline)
            {
                Console.WriteLine("  FAIL chipping the opponent did not raise the readout");
                failures++;
            }

            // Structural expectations that the UI depends on.
            if (baselineReadout.MoveForecasts.Length != 2)
            {
                Console.WriteLine($"  FAIL expected 2 move forecasts, got {baselineReadout.MoveForecasts.Length}");
                failures++;
            }
            if (string.IsNullOrWhiteSpace(baselineReadout.RecommendedLine))
            {
                Console.WriteLine("  FAIL recommendation was empty");
                failures++;
            }
            for (var i = 1; i < baselineReadout.Switches.Length; i++)
            {
                if (baselineReadout.Switches[i - 1].GainPoints < baselineReadout.Switches[i].GainPoints)
                {
                    Console.WriteLine("  FAIL switches were not sorted by descending gain");
                    failures++;
                    break;
                }
            }
            foreach (var threat in baselineReadout.Threats)
            {
                if (threat.IncomingKoChance < 0f || threat.IncomingKoChance > 1f)
                {
                    Console.WriteLine($"  FAIL ko chance out of range: {threat.IncomingKoChance}");
                    failures++;
                }
            }

            failures += ContestedMatchup(oracle);
            failures += AbilityWarning(oracle);
            failures += EditModeMirror(oracle);
            failures += BenchmarkAnalyse(oracle);
            return failures;
        }

        /// <summary>
        /// Runs the same assertions as Assets/Tests/PokeLab/TacticalReadoutTests.cs against
        /// the same scenario. NUnit needs the editor, so this mirror is what proves those
        /// expectations actually hold before they are committed.
        /// </summary>
        private static int EditModeMirror(PokeLabOracle oracle)
        {
            Console.WriteLine();
            Console.WriteLine("--- EditMode test mirror ---");
            var failures = 0;

            void Check(string name, bool condition, string detail = null)
            {
                if (condition) return;
                Console.WriteLine($"  FAIL {name}{(detail == null ? "" : ": " + detail)}");
                failures++;
            }

            var baseline = Mirror(oracle);
            var baselineLive = baseline.LiveWinProbability;

            var hurt = Mirror(oracle, playerHealth: 0.3f).LiveWinProbability;
            Check("TakingABigHitLowersTheReadout", hurt < baselineLive && baselineLive - hurt > 0.05f,
                  $"{Pct(baselineLive)} -> {Pct(hurt)}");

            var paralysed = Mirror(oracle, opponentStatus: StatusCondition.Paralysis).LiveWinProbability;
            Check("LandingAParalysisRaisesTheReadout",
                  paralysed > baselineLive && paralysed - baselineLive > 0.03f,
                  $"{Pct(baselineLive)} -> {Pct(paralysed)}");

            Check("OurOwnStatusLowersTheReadout",
                  Mirror(oracle, playerStatus: StatusCondition.Paralysis).LiveWinProbability < baselineLive &&
                  Mirror(oracle, playerStatus: StatusCondition.Burn).LiveWinProbability < baselineLive &&
                  Mirror(oracle, playerStatus: StatusCondition.Sleep).LiveWinProbability < baselineLive);

            Check("ChippingTheOpponentRaisesTheReadout",
                  Mirror(oracle, opponentHealth: 0.2f).LiveWinProbability > baselineLive);

            var withParty = Mirror(oracle, withParty: true);
            Check("HealthyReservesHelp", withParty.LiveWinProbability > baselineLive,
                  $"{Pct(baselineLive)} -> {Pct(withParty.LiveWinProbability)}");

            foreach (var health in new[] { 0.01f, 0.05f, 1f })
            {
                var live = Mirror(oracle, playerHealth: health).LiveWinProbability;
                Check("ReadoutNeverClaimsCertainty", live >= 0.02f && live <= 0.98f, Pct(live));
            }

            Check("ThreatsAreRankedAndBounded", baseline.Threats.Length > 0);
            for (var i = 0; i < baseline.Threats.Length; i++)
            {
                var threat = baseline.Threats[i];
                Check("ThreatHasHeadline", !string.IsNullOrEmpty(threat.Headline));
                Check("ThreatHasDetail", !string.IsNullOrEmpty(threat.Detail));
                Check("ThreatKoChanceInRange", threat.IncomingKoChance >= 0f && threat.IncomingKoChance <= 1f);
                if (i > 0)
                    Check("ThreatsRankedWorstFirst", (int)baseline.Threats[i - 1].Level >= (int)threat.Level);
            }

            var doomed = Mirror(oracle, playerHealth: 0.05f);
            Check("ALethalHitIsReportedAsLethal",
                  doomed.Threats.Length > 0 && doomed.Threats[0].Level == ThreatLevel.Lethal &&
                  doomed.Threats[0].IncomingKoChance > 0.9f,
                  doomed.Threats.Length > 0 ? $"{doomed.Threats[0].Level} ko={Pct(doomed.Threats[0].IncomingKoChance)}" : "no threats");

            Check("SwitchesAreSortedByGain", withParty.Switches.Length == 2,
                  $"got {withParty.Switches.Length}");
            for (var i = 1; i < withParty.Switches.Length; i++)
                Check("SwitchOrder", withParty.Switches[i - 1].GainPoints >= withParty.Switches[i].GainPoints);
            foreach (var candidate in withParty.Switches)
            {
                Check("SwitchHasRationale", !string.IsNullOrEmpty(candidate.Rationale));
                Check("SwitchHasInstanceId", !string.IsNullOrEmpty(candidate.InstanceId));
                Check("SwitchProbabilityInRange",
                      candidate.ProjectedWinProbability >= 0f && candidate.ProjectedWinProbability <= 1f);
                Check("ActiveCreatureIsNotOfferedAsASwitch", candidate.PartyIndex != 0);
            }

            Check("MoveForecastsAlignToTheActiveMoveSlots", baseline.MoveForecasts.Length == 2,
                  $"got {baseline.MoveForecasts.Length}");

            Check("ConfidenceFollowsScouting",
                  baseline.IsConfident && !Mirror(oracle, scouted: false).IsConfident);

            Check("RecommendedLineIsPresent", !string.IsNullOrWhiteSpace(baseline.RecommendedLine));
            Check("TypeSummaryIsPresent", !string.IsNullOrWhiteSpace(baseline.TypeSummary));
            foreach (var line in new[] { baseline.RecommendedLine, baseline.TypeSummary })
            {
                var lower = line.ToLowerInvariant();
                Check("ProseHasNoModelVocabulary",
                      !line.Contains("_diff") && !lower.Contains("probability") &&
                      !lower.Contains("forest") && !lower.Contains("null"), line);
            }
            Check("RecommendationNamesTheCreature", baseline.RecommendedLine.Contains("Pikachu"),
                  baseline.RecommendedLine);

            var unscouted = Mirror(oracle, scouted: false);
            Check("UnscoutedOpponentsGetAScoutingRecommendation",
                  unscouted.RecommendedLine.Contains("Poliwag"), unscouted.RecommendedLine);

            foreach (var evidence in baseline.Matchup.Evidence)
            {
                Check("EvidenceLabelPresent", !string.IsNullOrEmpty(evidence.Label));
                Check("EvidenceLabelIsHumanReadable", !evidence.Label.Contains("_"), evidence.Label);
            }

            Console.WriteLine($"  type summary:       {baseline.TypeSummary}");
            Console.WriteLine($"  recommendation:     {baseline.RecommendedLine}");
            Console.WriteLine($"  unscouted advice:   {unscouted.RecommendedLine}");
            Console.WriteLine($"  best switch:        {(withParty.Switches.Length > 0 ? withParty.Switches[0].Rationale : "-")}");
            Console.WriteLine(failures == 0 ? "  all EditMode expectations hold" : $"  {failures} expectation(s) failed");
            return failures;
        }

        /// <summary>Byte-for-byte the scenario TacticalReadoutTests.Analyse builds.</summary>
        private static TacticalReadout Mirror(PokeLabOracle oracle, float playerHealth = 1f,
                                              float opponentHealth = 1f,
                                              StatusCondition playerStatus = StatusCondition.None,
                                              StatusCondition opponentStatus = StatusCondition.None,
                                              bool scouted = true, bool withParty = false)
        {
            var pikachu = FakeCreature.Make(31, "Pikachu", 40, 30, 22, 30, 25, 48,
                                            "thunder-shock", "quick-attack");
            var poliwag = FakeCreature.Make(66, "Poliwag", 44, 25, 24, 27, 24, 45,
                                            "water-gun", "tackle");
            pikachu.AtHealth(playerHealth).WithStatus(playerStatus);
            poliwag.AtHealth(opponentHealth).WithStatus(opponentStatus);

            var state = new FakeBattleState
            {
                TurnNumber = 4,
                PlayerActive = pikachu,
                OpponentActive = poliwag,
                OpponentParty = { poliwag },
            };
            state.PlayerParty.Add(pikachu);
            if (withParty)
            {
                state.PlayerParty.Add(FakeCreature.Make(81, "Geodude", 40, 40, 50, 15, 15, 10, "rock-throw"));
                state.PlayerParty.Add(FakeCreature.Make(1, "Bulbasaur", 45, 30, 30, 40, 40, 30, "vine-whip"));
            }
            if (scouted) state.Scouted.Add(poliwag.SpeciesId);

            var engine = new FakeBattleEngine(state);
            engine.SetForecast(BattleSide.Player, 0, 0.20f, 0.25f, 0.22f, 1.0f, 1f, 5);
            engine.SetForecast(BattleSide.Player, 1, 0.14f, 0.18f, 0.16f, 1.0f, 1f, 7);
            engine.SetForecast(BattleSide.Opponent, 0, 0.19f, 0.24f, 0.21f, 1.0f, 1f, 5);
            engine.SetForecast(BattleSide.Opponent, 1, 0.13f, 0.17f, 0.15f, 1.0f, 1f, 7);

            return oracle.Analyse(state, engine);
        }

        /// <summary>
        /// Repeat the status checks on a genuine coin-flip. Pikachu vs Poliwag is the most
        /// contested pair in the slice roster — the forest puts it at 0.5098 — so this is
        /// where the middle of the readout's range gets exercised, and where a player would
        /// actually be reading the number rather than the obvious.
        /// </summary>
        private static int ContestedMatchup(PokeLabOracle oracle)
        {
            Console.WriteLine();
            Console.WriteLine("--- tactical scenarios (coin flip: Pikachu vs Poliwag, prior 0.510) ---");
            var failures = 0;

            var baseline = Even(oracle, out var evenReadout);
            Console.WriteLine($"baseline live:        {Pct(baseline)}");
            Console.WriteLine($"  recommendation:     {evenReadout.RecommendedLine}");

            var paralysed = Even(oracle, out _, opponentStatus: StatusCondition.Paralysis);
            Console.WriteLine($"opponent paralysed:   {Pct(paralysed)} ({Delta(paralysed, baseline)})");
            if (paralysed - baseline < 0.03f)
            {
                Console.WriteLine("  FAIL paralysis moved the readout by less than 3 points on an even matchup");
                failures++;
            }

            var hurt = Even(oracle, out _, playerHealth: 0.3f);
            Console.WriteLine($"we are at 30%:        {Pct(hurt)} ({Delta(hurt, baseline)})");
            if (baseline - hurt < 0.05f)
            {
                Console.WriteLine("  FAIL a big hit moved the readout by less than 5 points on an even matchup");
                failures++;
            }
            return failures;
        }

        private static float Even(PokeLabOracle oracle, out TacticalReadout readout,
                                  float playerHealth = 1f,
                                  StatusCondition opponentStatus = StatusCondition.None)
        {
            var pikachu = FakeCreature.Make(31, "Pikachu", 40, 30, 22, 30, 25, 48, "thunder-shock", "quick-attack");
            var poliwag = FakeCreature.Make(66, "Poliwag", 44, 25, 24, 27, 24, 45, "water-gun", "tackle");
            pikachu.AtHealth(playerHealth);
            poliwag.WithStatus(opponentStatus);

            var state = new FakeBattleState
            {
                TurnNumber = 4,
                PlayerActive = pikachu,
                OpponentActive = poliwag,
                PlayerParty = { pikachu },
                OpponentParty = { poliwag },
            };
            state.Scouted.Add(poliwag.SpeciesId);

            var engine = new FakeBattleEngine(state);
            engine.SetForecast(BattleSide.Player, 0, 0.20f, 0.25f, 0.22f, 1.0f, 1f, 5);
            engine.SetForecast(BattleSide.Player, 1, 0.14f, 0.18f, 0.16f, 1.0f, 1f, 7);
            engine.SetForecast(BattleSide.Opponent, 0, 0.19f, 0.24f, 0.21f, 1.0f, 1f, 5);
            engine.SetForecast(BattleSide.Opponent, 1, 0.13f, 0.17f, 0.15f, 1.0f, 1f, 7);

            readout = oracle.Analyse(state, engine);
            return readout.LiveWinProbability;
        }

        /// <summary>
        /// Gastly's levitate must produce the ability-immunity caveat against a Ground
        /// attacker, matching the warning backend.py emits.
        /// </summary>
        private static int AbilityWarning(PokeLabOracle oracle)
        {
            Console.WriteLine();
            Console.WriteLine("--- ability immunity warning (Geodude vs Gastly) ---");
            var prediction = oracle.Predict(81, 100);
            foreach (var warning in prediction.Warnings) Console.WriteLine($"  {warning}");

            var found = false;
            foreach (var warning in prediction.Warnings)
                if (warning.Contains("nullify") && warning.Contains("Ground")) found = true;

            if (!found)
            {
                Console.WriteLine("  FAIL expected a Ground-immunity warning from Gastly's levitate");
                return 1;
            }
            return 0;
        }

        /// <summary>A full Analyse must fit the same 5 ms main-thread budget as Predict.</summary>
        private static int BenchmarkAnalyse(PokeLabOracle oracle)
        {
            Analyse(oracle, out _); // warm

            var watch = new Stopwatch();
            var samples = new System.Collections.Generic.List<double>(50);
            for (var i = 0; i < 50; i++)
            {
                watch.Restart();
                Analyse(oracle, out _, playerHealth: 1f - i * 0.01f);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            var median = samples[samples.Count / 2];
            var p90 = samples[(int)(samples.Count * 0.9)];
            Console.WriteLine($"Analyse (warm cache): median {median.ToString("F3", CultureInfo.InvariantCulture)} ms, " +
                              $"p90 {p90.ToString("F3", CultureInfo.InvariantCulture)} ms over 50 runs");

            // p90 for the same reason as the Predict benchmark: outliers here are process
            // noise, not the analyzer.
            if (p90 > 5.0)
            {
                Console.WriteLine("  FAIL Analyse exceeded the 5 ms budget at p90");
                return 1;
            }
            return 0;
        }

        private static float Analyse(PokeLabOracle oracle, out TacticalReadout readout,
                                     float playerHealth = 1f, float opponentHealth = 1f,
                                     StatusCondition playerStatus = StatusCondition.None,
                                     StatusCondition opponentStatus = StatusCondition.None)
        {
            // Poliwag (Water) into Geodude (Rock/Ground): Water is 4x here, so the player
            // should be well ahead, and Geodude's Rock Throw is the obvious threat.
            var poliwag = FakeCreature.Make(66, "Poliwag", 44, 25, 24, 27, 24, 45, "water-gun", "tackle");
            var geodude = FakeCreature.Make(81, "Geodude", 40, 40, 50, 15, 15, 10, "rock-throw", "tackle");
            var machop = FakeCreature.Make(73, "Machop", 46, 40, 25, 18, 18, 20, "karate-chop", "tackle");
            var pidgey = FakeCreature.Make(21, "Pidgey", 40, 25, 24, 18, 18, 28, "gust", "tackle");

            poliwag.AtHealth(playerHealth).WithStatus(playerStatus);
            geodude.AtHealth(opponentHealth).WithStatus(opponentStatus);

            var state = new FakeBattleState
            {
                TurnNumber = 3,
                PlayerActive = poliwag,
                OpponentActive = geodude,
                PlayerParty = { poliwag, machop, pidgey },
                OpponentParty = { geodude },
            };
            state.Scouted.Add(geodude.SpeciesId);

            var engine = new FakeBattleEngine(state);
            // Water Gun into Rock/Ground: 4x, close to half its health per turn.
            engine.SetForecast(BattleSide.Player, 0, 0.40f, 0.50f, 0.45f, 1.0f, 4f, 3);
            engine.SetForecast(BattleSide.Player, 1, 0.08f, 0.11f, 0.10f, 1.0f, 1f, 10);
            // Rock Throw back into a Water type: neutral, and it hurts.
            engine.SetForecast(BattleSide.Opponent, 0, 0.28f, 0.34f, 0.31f, 0.90f, 1f, 4);
            engine.SetForecast(BattleSide.Opponent, 1, 0.14f, 0.18f, 0.16f, 1.0f, 1f, 7);

            readout = oracle.Analyse(state, engine);
            return readout.LiveWinProbability;
        }

        private static string Pct(float value) =>
            (value * 100f).ToString("F1", CultureInfo.InvariantCulture) + "%";

        private static string Delta(float value, float baseline)
        {
            var points = (value - baseline) * 100f;
            return (points >= 0 ? "+" : string.Empty) + points.ToString("F1", CultureInfo.InvariantCulture) + " pts";
        }
    }
}
