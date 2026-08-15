using System.IO;
using System.Text;
using NUnit.Framework;
using PokeLab.Core;
using PokeLab.Intelligence;
using PokeLab.Intelligence.Data;
using UnityEngine;

namespace PokeLab.Tests
{
    /// <summary>
    /// Behaviour of the live tactical readout.
    ///
    /// These assert direction and range rather than exact numbers. The blend has no
    /// external ground truth to compare against — what matters is that the readout moves
    /// the right way when the battle does, and never reports something the UI cannot
    /// render.
    /// </summary>
    [TestFixture]
    public sealed class TacticalReadoutTests
    {
        private static PokeLabOracle _oracle;

        [OneTimeSetUp]
        public void LoadExportedData()
        {
            var root = Path.Combine(Application.streamingAssetsPath, PokeLabData.Files.Directory);
            if (!Directory.Exists(root))
                Assert.Ignore($"Poké Lab data not exported yet ({root}). Run Tools/Export/export_pokelab.py.");

            var data = PokeLabData.Parse(
                File.ReadAllText(Path.Combine(root, PokeLabData.Files.Species), Encoding.UTF8),
                File.ReadAllText(Path.Combine(root, PokeLabData.Files.Moves), Encoding.UTF8),
                File.ReadAllText(Path.Combine(root, PokeLabData.Files.TypeChart), Encoding.UTF8),
                File.ReadAllBytes(Path.Combine(root, PokeLabData.Files.Forest)),
                File.ReadAllBytes(Path.Combine(root, PokeLabData.Files.Combats)));
            _oracle = new PokeLabOracle(data);
        }

        /// <summary>
        /// Pikachu vs Poliwag: the forest calls this 0.5098, the closest matchup in the
        /// slice roster. Using a coin flip means a shift in the battle state shows up as a
        /// legible change rather than being flattened by a saturated logistic curve.
        /// </summary>
        private static TacticalReadout Analyse(float playerHealth = 1f, float opponentHealth = 1f,
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

            return _oracle.Analyse(state, engine);
        }

        // --- The blend responds to the battle ------------------------------------------

        [Test]
        public void TakingABigHitLowersTheReadout()
        {
            var baseline = Analyse().LiveWinProbability;
            var hurt = Analyse(playerHealth: 0.3f).LiveWinProbability;

            Assert.Less(hurt, baseline, "dropping to 30% health must lower the readout");
            Assert.Greater(baseline - hurt, 0.05f,
                "a hit that big should move the readout by more than five points");
        }

        [Test]
        public void LandingAParalysisRaisesTheReadout()
        {
            var baseline = Analyse().LiveWinProbability;
            var paralysed = Analyse(opponentStatus: StatusCondition.Paralysis).LiveWinProbability;

            Assert.Greater(paralysed, baseline, "paralysing the opponent must raise the readout");
            Assert.Greater(paralysed - baseline, 0.03f,
                "a paralysis should be worth more than three points on a coin-flip matchup");
        }

        [Test]
        public void OurOwnStatusLowersTheReadout()
        {
            var baseline = Analyse().LiveWinProbability;
            Assert.Less(Analyse(playerStatus: StatusCondition.Paralysis).LiveWinProbability, baseline);
            Assert.Less(Analyse(playerStatus: StatusCondition.Burn).LiveWinProbability, baseline);
            Assert.Less(Analyse(playerStatus: StatusCondition.Sleep).LiveWinProbability, baseline);
        }

        [Test]
        public void ChippingTheOpponentRaisesTheReadout()
        {
            var baseline = Analyse().LiveWinProbability;
            Assert.Greater(Analyse(opponentHealth: 0.2f).LiveWinProbability, baseline);
        }

        [Test]
        public void HealthyReservesHelp()
        {
            var alone = Analyse().LiveWinProbability;
            Assert.Greater(Analyse(withParty: true).LiveWinProbability, alone,
                "a bench to switch to is worth something");
        }

        [Test]
        public void ReadoutNeverClaimsCertainty()
        {
            // Even a hopeless position keeps a sliver of doubt: rolls, crits and flinches
            // all live outside this model.
            foreach (var health in new[] { 0.01f, 0.05f, 1f })
            {
                var live = Analyse(playerHealth: health, opponentHealth: 1f).LiveWinProbability;
                Assert.GreaterOrEqual(live, 0.02f);
                Assert.LessOrEqual(live, 0.98f);
            }
        }

        // --- Structure the UI depends on -----------------------------------------------

        [Test]
        public void ThreatsAreRankedAndBounded()
        {
            var readout = Analyse();
            Assert.IsNotEmpty(readout.Threats, "a creature with moves should produce threats");

            for (var i = 0; i < readout.Threats.Length; i++)
            {
                var threat = readout.Threats[i];
                Assert.IsNotEmpty(threat.Headline, "a threat needs a headline");
                Assert.IsNotEmpty(threat.Detail, "a threat needs detail");
                Assert.GreaterOrEqual(threat.IncomingKoChance, 0f);
                Assert.LessOrEqual(threat.IncomingKoChance, 1f);
                if (i > 0)
                    Assert.GreaterOrEqual((int)readout.Threats[i - 1].Level, (int)threat.Level,
                        "threats must be ranked worst first");
            }
        }

        [Test]
        public void ALethalHitIsReportedAsLethal()
        {
            // At 5% health, a move averaging 21% is a guaranteed knockout.
            var readout = Analyse(playerHealth: 0.05f);
            Assert.AreEqual(ThreatLevel.Lethal, readout.Threats[0].Level);
            Assert.Greater(readout.Threats[0].IncomingKoChance, 0.9f);
        }

        [Test]
        public void SwitchesAreSortedByGain()
        {
            var readout = Analyse(withParty: true);
            Assert.AreEqual(2, readout.Switches.Length, "two healthy reserves are switchable");

            for (var i = 1; i < readout.Switches.Length; i++)
                Assert.GreaterOrEqual(readout.Switches[i - 1].GainPoints, readout.Switches[i].GainPoints);

            foreach (var candidate in readout.Switches)
            {
                Assert.IsNotEmpty(candidate.Rationale, "a switch needs a reason the player can read");
                Assert.IsNotEmpty(candidate.InstanceId);
                Assert.GreaterOrEqual(candidate.ProjectedWinProbability, 0f);
                Assert.LessOrEqual(candidate.ProjectedWinProbability, 1f);
            }
        }

        [Test]
        public void ActiveCreatureIsNotOfferedAsASwitch()
        {
            var readout = Analyse(withParty: true);
            foreach (var candidate in readout.Switches)
                Assert.AreNotEqual(0, candidate.PartyIndex, "the active creature must not be a switch target");
        }

        [Test]
        public void MoveForecastsAlignToTheActiveMoveSlots()
        {
            var readout = Analyse();
            Assert.AreEqual(2, readout.MoveForecasts.Length, "Pikachu was given two moves");
        }

        [Test]
        public void ConfidenceFollowsScouting()
        {
            Assert.IsTrue(Analyse(scouted: true).IsConfident);
            Assert.IsFalse(Analyse(scouted: false).IsConfident, "an unscouted opponent is not trustworthy intel");
        }

        // --- Prose ---------------------------------------------------------------------

        [Test]
        public void ProseReadsLikeAFieldDeviceNotDebugOutput()
        {
            var readout = Analyse();

            Assert.IsNotEmpty(readout.RecommendedLine);
            Assert.IsNotEmpty(readout.TypeSummary);

            // Model vocabulary must never reach the player.
            foreach (var line in new[] { readout.RecommendedLine, readout.TypeSummary })
            {
                StringAssert.DoesNotContain("_diff", line);
                StringAssert.DoesNotContain("probability", line.ToLowerInvariant());
                StringAssert.DoesNotContain("forest", line.ToLowerInvariant());
                StringAssert.DoesNotContain("null", line.ToLowerInvariant());
            }

            // It should name what is on the field, not describe it abstractly.
            StringAssert.Contains("Pikachu", readout.RecommendedLine);
            Debug.Log($"[PokeLab] {readout.TypeSummary}\n[PokeLab] {readout.RecommendedLine}");
        }

        [Test]
        public void UnscoutedOpponentsGetAScoutingRecommendation()
        {
            var readout = Analyse(scouted: false);
            StringAssert.Contains("Poliwag", readout.RecommendedLine);
        }

        [Test]
        public void EvidenceLabelsAreHumanReadable()
        {
            var readout = Analyse();
            foreach (var evidence in readout.Matchup.Evidence)
            {
                Assert.IsNotEmpty(evidence.Label);
                StringAssert.DoesNotContain("_", evidence.Label);
            }
        }
    }
}
