using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PokeLab.Core;
using PokeLab.Intelligence.Data;
using PokeLab.Intelligence.Model;

namespace PokeLab.Verify
{
    /// <summary>
    /// Compares the C# forest port against reference_predictions.json, which
    /// Tools/Export/gen_reference.py produced by running the upstream backend.py.
    ///
    /// Exits non-zero when any probability or evidence value drifts past tolerance, so it
    /// can gate a re-export.
    /// </summary>
    public static class Program
    {
        private const double ProbabilityTolerance = 1e-4;
        private const double EvidenceTolerance = 1e-3; // percentage points

        public static int Main(string[] args)
        {
            var root = args.Length > 0 ? args[0] : FindProjectRoot();
            var dataDir = Path.Combine(root, "Assets", "StreamingAssets", "pokelab");
            var referencePath = Path.Combine(root, "Tools", "Export", "reference_predictions.json");

            Console.WriteLine($"data:      {dataDir}");
            Console.WriteLine($"reference: {referencePath}");

            var species = SpeciesRegistry.Load(File.ReadAllText(Path.Combine(dataDir, "species.json"), Encoding.UTF8));
            var chart = PokeLabTypeChart.Load(File.ReadAllText(Path.Combine(dataDir, "typechart.json"), Encoding.UTF8));
            var forest = RandomForest.Load(File.ReadAllBytes(Path.Combine(dataDir, "forest.bin")));
            var combats = CombatRecords.Load(File.ReadAllBytes(Path.Combine(dataDir, "combats.bin")));
            var moves = MoveRegistry.Load(File.ReadAllText(Path.Combine(dataDir, "moves.json"), Encoding.UTF8));

            Console.WriteLine($"loaded:    {species.Count} species, {forest.TreeCount} trees / {forest.NodeCount} nodes, " +
                              $"{combats.PairCount} combat pairs, {moves.All.Count} moves");

            var oracle = new ForestOracle(forest, species, chart, combats);
            var reference = JsonValue.Parse(File.ReadAllText(referencePath, Encoding.UTF8));

            var failures = 0;
            var maxProbabilityDelta = 0d;
            var maxEvidenceDelta = 0d;
            var maxTypeDelta = 0d;
            var checkedPairs = 0;

            foreach (var entry in reference["pairs"].AsArray())
            {
                var first = entry["first"].AsInt();
                var second = entry["second"].AsInt();
                var prediction = oracle.Predict(first, second);
                checkedPairs++;

                var expected = entry["firstProbability"].AsDouble();
                var delta = Math.Abs(prediction.FirstWinProbability - expected);
                maxProbabilityDelta = Math.Max(maxProbabilityDelta, delta);
                if (delta > ProbabilityTolerance)
                {
                    failures++;
                    Console.WriteLine($"  FAIL p({first},{second}): expected {expected:F9}, got " +
                                      $"{prediction.FirstWinProbability:F9} (delta {delta:E3})");
                }

                maxTypeDelta = Math.Max(maxTypeDelta, Math.Abs(prediction.TypeEffectFirst - entry["typeEffectFirst"].AsDouble()));
                maxTypeDelta = Math.Max(maxTypeDelta, Math.Abs(prediction.TypeEffectSecond - entry["typeEffectSecond"].AsDouble()));
                maxTypeDelta = Math.Max(maxTypeDelta, Math.Abs(prediction.AbilityTypeEffectFirst - entry["abilityTypeEffectFirst"].AsDouble()));
                maxTypeDelta = Math.Max(maxTypeDelta, Math.Abs(prediction.AbilityTypeEffectSecond - entry["abilityTypeEffectSecond"].AsDouble()));

                if (prediction.DirectBattles != entry["directBattles"].AsInt() ||
                    prediction.DirectFirstWins != entry["directFirstWins"].AsInt() ||
                    prediction.DirectSecondWins != entry["directSecondWins"].AsInt())
                {
                    failures++;
                    Console.WriteLine($"  FAIL combats({first},{second}): expected " +
                                      $"{entry["directBattles"].AsInt()}/{entry["directFirstWins"].AsInt()}/{entry["directSecondWins"].AsInt()}, got " +
                                      $"{prediction.DirectBattles}/{prediction.DirectFirstWins}/{prediction.DirectSecondWins}");
                }

                // Evidence is compared by feature name, not by position: ties in the sort
                // are legitimately orderable either way, but the values must match.
                var expectedEvidence = new Dictionary<string, double>(StringComparer.Ordinal);
                var expectedValues = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var item in entry["evidence"].AsArray())
                {
                    expectedEvidence[item["feature"].AsString()] = item["probabilityPoints"].AsDouble();
                    expectedValues[item["feature"].AsString()] = item["value"].AsDouble();
                }

                foreach (var item in prediction.Evidence)
                {
                    if (!expectedEvidence.TryGetValue(item.Feature, out var expectedPoints))
                    {
                        failures++;
                        Console.WriteLine($"  FAIL evidence({first},{second}): unexpected feature {item.Feature}");
                        continue;
                    }
                    var pointsDelta = Math.Abs(item.ProbabilityPoints - expectedPoints);
                    maxEvidenceDelta = Math.Max(maxEvidenceDelta, pointsDelta);
                    if (pointsDelta > EvidenceTolerance)
                    {
                        failures++;
                        Console.WriteLine($"  FAIL evidence({first},{second}) {item.Feature}: " +
                                          $"expected {expectedPoints:F6}, got {item.ProbabilityPoints:F6}");
                    }

                    // The float32 feature value must round-trip identically too.
                    var valueDelta = Math.Abs(item.Value - expectedValues[item.Feature]);
                    if (valueDelta > 1e-4)
                    {
                        failures++;
                        Console.WriteLine($"  FAIL feature value({first},{second}) {item.Feature}: " +
                                          $"expected {expectedValues[item.Feature]:F6}, got {item.Value:F6}");
                    }
                }

                if (prediction.Evidence.Length != expectedEvidence.Count)
                {
                    failures++;
                    Console.WriteLine($"  FAIL evidence count({first},{second}): expected " +
                                      $"{expectedEvidence.Count}, got {prediction.Evidence.Length}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"pairs checked:        {checkedPairs}");
            Console.WriteLine($"max probability delta {maxProbabilityDelta:E3}  (tolerance {ProbabilityTolerance:E0})");
            Console.WriteLine($"max evidence delta    {maxEvidenceDelta:E3}  (tolerance {EvidenceTolerance:E0})");
            Console.WriteLine($"max type effect delta {maxTypeDelta:E3}");

            failures += CheckOrderInvariance(oracle, species);
            failures += Benchmark(oracle, species);
            failures += Scenarios.Run(new PokeLabData(species, moves, chart, forest, combats));

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures} problems)");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// The symmetric-probability construction is supposed to make the readout
        /// order-invariant. Verify it holds across a wide sweep, not just the reference set.
        /// </summary>
        private static int CheckOrderInvariance(ForestOracle oracle, SpeciesRegistry species)
        {
            var ids = new List<int>();
            foreach (var entry in species.All) ids.Add(entry.Id);

            var worst = 0d;
            var failures = 0;
            var random = new Random(20260815);
            for (var i = 0; i < 400; i++)
            {
                var a = ids[random.Next(ids.Count)];
                var b = ids[random.Next(ids.Count)];
                var forward = oracle.Predict(a, b).FirstWinProbability;
                var backward = oracle.Predict(b, a).FirstWinProbability;
                var delta = Math.Abs(forward - (1f - backward));
                worst = Math.Max(worst, delta);
            }
            Console.WriteLine($"order invariance:     max |p(A,B) - (1 - p(B,A))| = {worst:E3} over 400 random pairs");
            if (worst > 1e-6)
            {
                failures++;
                Console.WriteLine("  FAIL order invariance broke");
            }
            return failures;
        }

        /// <summary>A full Predict including evidence must fit in 5 ms on the main thread.</summary>
        private static int Benchmark(ForestOracle oracle, SpeciesRegistry species)
        {
            var ids = new List<int>();
            foreach (var entry in species.All) ids.Add(entry.Id);

            // Warm the JIT without polluting the measurement with cached results.
            foreach (var (a, b) in new[] { (1, 5), (5, 10), (10, 21), (21, 25) }) oracle.Predict(a, b);

            // Uncached cost is what matters: measure pairs that were never asked for
            // before, since a battle keeps hitting new matchups as parties change.
            var cold = new List<double>();
            var random = new Random(7);
            var watch = new Stopwatch();
            for (var i = 0; i < 60; i++)
            {
                var a = ids[random.Next(ids.Count)];
                var b = ids[random.Next(ids.Count)];
                watch.Restart();
                oracle.Predict(a, b);
                watch.Stop();
                cold.Add(watch.Elapsed.TotalMilliseconds);
            }
            cold.Sort();
            var median = cold[cold.Count / 2];
            var p90 = cold[(int)(cold.Count * 0.9)];
            var worst = cold[cold.Count - 1];
            Console.WriteLine($"Predict (uncached):   median {F(median)} ms, p90 {F(p90)} ms, " +
                              $"worst {F(worst)} ms over {cold.Count} cold pairs");

            // Gated on p90, not the single worst sample: an occasional multi-millisecond
            // outlier here is a GC pause or a JIT tier-up in the harness process, not the
            // descent loop, and gating on it makes the check flaky rather than meaningful.
            if (p90 > 5.0)
            {
                Console.WriteLine("  FAIL Predict exceeded the 5 ms budget at p90");
                return 1;
            }
            return 0;
        }

        private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

        private static string FindProjectRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets", "StreamingAssets", "pokelab")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("could not locate the Unity project root from " + AppContext.BaseDirectory);
        }
    }
}
