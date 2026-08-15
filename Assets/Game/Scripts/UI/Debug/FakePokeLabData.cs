using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.UI.Debugging
{
    /// <summary>
    /// A deterministic stand-in for the intelligence layer.
    ///
    /// The UI is built in parallel with the oracle, so without this there is no way to know
    /// whether the scanner animates correctly, whether the evidence bars rank sensibly, or
    /// whether a threat clearing looks right — the whole signature feature would be unverified
    /// until integration day. This generator produces plausible, *evolving* readouts: the
    /// probability random-walks, evidence contributions shift with it, threats appear and
    /// resolve, and switch recommendations re-rank.
    ///
    /// It is deterministic from a seed so a reviewer and the integrator see the same run, and
    /// it includes a scripted beat — a paralysis landing for roughly eighteen points on turn
    /// four — because that specific moment is the one the feature has to sell.
    ///
    /// Ship it committed but inert: nothing constructs this unless
    /// <see cref="PokeLabDebugDriver"/> is present and explicitly enabled.
    /// </summary>
    public sealed class FakePokeLabOracle : IPokeLabOracle
    {
        private readonly System.Random _random;
        private readonly FakeSpeciesRegistry _species;

        private float _probability = 0.52f;
        private float _previousProbability = 0.52f;
        private int _turn;
        private bool _paralysisLanded;
        private bool _confident;

        /// <summary>Turn on which the scripted paralysis swing fires.</summary>
        public int ScriptedSwingTurn = 4;

        /// <summary>Turn from which the readout is reported as confident.</summary>
        public int ConfidentFromTurn = 2;

        public FakePokeLabOracle(int seed, FakeSpeciesRegistry species)
        {
            _random = new System.Random(seed);
            _species = species;
        }

        public bool IsReady => true;

        /// <summary>Current simulated turn, for the driver's own display.</summary>
        public int Turn => _turn;

        public MatchupPrediction Predict(int firstSpeciesId, int secondSpeciesId)
        {
            return BuildPrediction(firstSpeciesId, secondSpeciesId, _probability);
        }

        public TacticalReadout Analyse(IBattleStateView state, IBattleEngine engine)
        {
            // The fake never reads live state — a driver that owned no engine would then be
            // unable to produce anything, which defeats the point.
            return Advance();
        }

        public MatchupPrediction ScoutEncounter(CreatureInstance playerLead, int wildSpeciesId)
        {
            var lead = playerLead?.SpeciesId ?? _species.PlayerLeadId;
            // Scouting is a species prior, so it must not depend on the live walk. Derive it
            // from the pair so the same encounter always scans the same.
            var prior = 0.5f + Mathf.Sin((lead * 37 + wildSpeciesId * 19) * 0.117f) * 0.32f;
            return BuildPrediction(lead, wildSpeciesId, Mathf.Clamp(prior, 0.06f, 0.94f));
        }

        /// <summary>Steps the simulation one turn and returns the resulting readout.</summary>
        public TacticalReadout Advance()
        {
            _turn++;
            _previousProbability = _probability;

            if (_turn == ScriptedSwingTurn && !_paralysisLanded)
            {
                // The moment the feature exists to sell: a status lands and the number jumps.
                _paralysisLanded = true;
                _probability = Mathf.Clamp01(_probability + 0.18f);
            }
            else
            {
                // Ornstein-Uhlenbeck-ish walk: drifts, but is pulled back toward even so the
                // bar does not pin at an end and stop being interesting to look at.
                var noise = (float)(_random.NextDouble() * 2.0 - 1.0) * 0.07f;
                var pull = (0.5f - _probability) * 0.10f;
                _probability = Mathf.Clamp(_probability + noise + pull, 0.04f, 0.96f);
            }

            _confident = _turn >= ConfidentFromTurn;

            var readout = new TacticalReadout
            {
                Matchup = BuildPrediction(_species.PlayerLeadId, _species.OpponentId, _probability),
                LiveWinProbability = _probability,
                DeltaPoints = (_probability - _previousProbability) * 100f,
                Threats = BuildThreats(),
                Switches = BuildSwitches(),
                MoveForecasts = BuildForecasts(),
                RecommendedLine = BuildRecommendedLine(),
                TypeSummary = BuildTypeSummary(),
                IsConfident = _confident,
            };
            return readout;
        }

        /// <summary>Rewinds to the opening state so a reviewer can replay the sequence.</summary>
        public void Restart()
        {
            _turn = 0;
            _probability = 0.52f;
            _previousProbability = 0.52f;
            _paralysisLanded = false;
            _confident = false;
        }

        // ------------------------------------------------------------- construction

        private MatchupPrediction BuildPrediction(int first, int second, float probability)
        {
            var typeFirst = Quantise(1.4f + Mathf.Sin(first * 0.31f + _turn * 0.05f) * 1.1f);
            var typeSecond = Quantise(1.1f + Mathf.Cos(second * 0.27f) * 0.9f);

            return new MatchupPrediction
            {
                FirstSpeciesId = first,
                SecondSpeciesId = second,
                FirstWinProbability = probability,
                TypeEffectFirst = typeFirst,
                TypeEffectSecond = typeSecond,
                // Half the time an ability blunts the incoming multiplier — that path needs
                // exercising, because it is the case the raw type chart gets wrong.
                AbilityTypeEffectFirst = typeFirst,
                AbilityTypeEffectSecond = (first + second) % 2 == 0 ? typeSecond * 0.5f : typeSecond,
                Evidence = BuildEvidence(probability),
                DirectBattles = 4 + (first + second) % 9,
                DirectFirstWins = 2 + (first % 4),
                DirectSecondWins = 1 + (second % 3),
                Warnings = BuildWarnings(),
            };
        }

        /// <summary>
        /// Eleven features matching the shape of the ported model, with the contributions
        /// summing roughly to the distance of the probability from even — the same property
        /// the real neutral-substitution output has.
        /// </summary>
        private PredictionEvidence[] BuildEvidence(float probability)
        {
            var bias = (probability - 0.5f) * 100f;
            var features = new (string feature, string label, float value, float weight)[]
            {
                ("Speed_diff", "Speed advantage", 34f + _turn * 2f, 0.32f),
                ("Attack_diff", "Physical attack gap", -18f, -0.21f),
                ("SpAttack_diff", "Special attack gap", 26f, 0.18f),
                ("Defense_diff", "Physical bulk", -9f, -0.12f),
                ("SpDefense_diff", "Special bulk", 12f, 0.09f),
                ("HP_diff", "Health pool", 21f, 0.14f),
                ("Type_effect", "Type effectiveness", 2f, 0.28f),
                ("Ability_type_effect", "Ability interaction", 0.5f, -0.11f),
                ("BST_diff", "Overall stat total", 44f, 0.16f),
                ("Legendary_diff", "Rarity tier", 0f, 0.02f),
                ("Generation_diff", "Generation", -1f, -0.01f),
            };

            var evidence = new PredictionEvidence[features.Length];
            for (var i = 0; i < features.Length; i++)
            {
                var (feature, label, value, weight) = features[i];
                // Jitter each contribution a little per turn so the ranking genuinely moves
                // between readouts instead of the same bars redrawing at the same lengths.
                var jitter = 1f + (float)(_random.NextDouble() - 0.5) * 0.25f;
                var points = bias * weight * jitter + weight * 6f;

                if (_paralysisLanded && feature == "Speed_diff") points += 14f;

                evidence[i] = new PredictionEvidence
                {
                    Feature = feature,
                    Label = label,
                    Value = value,
                    ProbabilityPoints = points,
                };
            }

            Array.Sort(evidence, (a, b) =>
                Mathf.Abs(b.ProbabilityPoints).CompareTo(Mathf.Abs(a.ProbabilityPoints)));
            return evidence;
        }

        private ThreatAssessment[] BuildThreats()
        {
            var threats = new List<ThreatAssessment>(3);

            // A persistent threat that de-escalates once the scripted paralysis lands: this
            // is what exercises the "threats clear" path.
            if (!_paralysisLanded)
            {
                threats.Add(new ThreatAssessment
                {
                    Level = ThreatLevel.Lethal,
                    Headline = "Outsped and one-shot by Aqua Fang",
                    Detail = "They move first and roll 104–122% of your remaining HP.",
                    SourceMoveId = "aqua_fang",
                    IncomingKoChance = 0.78f,
                });
            }
            else
            {
                threats.Add(new ThreatAssessment
                {
                    Level = ThreatLevel.Manageable,
                    Headline = "Aqua Fang still threatens a two-turn KO",
                    Detail = "Paralysis removed the speed tie; you now move first 75% of turns.",
                    SourceMoveId = "aqua_fang",
                    IncomingKoChance = 0.22f,
                });
            }

            // A threat that only appears mid-battle, exercising the "threats appear" path.
            if (_turn >= 3)
            {
                threats.Add(new ThreatAssessment
                {
                    Level = _turn >= 6 ? ThreatLevel.Dangerous : ThreatLevel.Manageable,
                    Headline = "Stacking Sand Veil evasion",
                    Detail = "Your accuracy is down two stages while the sandstorm holds.",
                    SourceMoveId = "sandstorm",
                    IncomingKoChance = 0.14f + _turn * 0.02f,
                });
            }

            if (_turn >= 5)
            {
                threats.Add(new ThreatAssessment
                {
                    Level = ThreatLevel.Trivial,
                    Headline = "Residual burn chip damage",
                    Detail = "6% of max HP per turn. Not lethal for four more turns.",
                    SourceMoveId = "burn",
                    IncomingKoChance = 0.03f,
                });
            }

            return threats.ToArray();
        }

        private SwitchRecommendation[] BuildSwitches()
        {
            // Gains oscillate at different rates so the ranking reorders every few turns,
            // which is the only way to see the re-rank movement chevrons working.
            var a = 14f + Mathf.Sin(_turn * 0.9f) * 9f;
            var b = 11f + Mathf.Cos(_turn * 0.6f) * 8f;
            var c = 3f + Mathf.Sin(_turn * 1.4f + 1f) * 5f;

            return new[]
            {
                new SwitchRecommendation
                {
                    PartyIndex = 1,
                    InstanceId = _species.PartyInstanceIds[1],
                    ProjectedWinProbability = Mathf.Clamp01(_probability + a / 100f),
                    GainPoints = a,
                    Rationale = "Resists their coverage move and outspeeds by 24 points.",
                },
                new SwitchRecommendation
                {
                    PartyIndex = 2,
                    InstanceId = _species.PartyInstanceIds[2],
                    ProjectedWinProbability = Mathf.Clamp01(_probability + b / 100f),
                    GainPoints = b,
                    Rationale = "Immune to their STAB; walls the matchup but cannot pressure back.",
                },
                new SwitchRecommendation
                {
                    PartyIndex = 3,
                    InstanceId = _species.PartyInstanceIds[3],
                    ProjectedWinProbability = Mathf.Clamp01(_probability + c / 100f),
                    GainPoints = c,
                    Rationale = "Trades evenly. Worth it only to preserve your lead.",
                },
            };
        }

        private DamageForecast[] BuildForecasts()
        {
            return new[]
            {
                new DamageForecast(0.28f, 0.34f, 0.31f, 1f, 2f, 4),
                new DamageForecast(0.11f, 0.14f, 0.125f, 0.85f, 0.5f, 9),
                new DamageForecast(0f, 0f, 0f, 1f, 1f, int.MaxValue),
                new DamageForecast(0.44f, 0.53f, 0.485f, 0.7f, 2f, 3),
            };
        }

        private string BuildRecommendedLine()
        {
            if (!_paralysisLanded && _turn >= 2)
                return "Lead with Static Pulse to remove the speed gap before they set up.";
            if (_paralysisLanded && _turn < 6)
                return "Press the advantage — Ember Lash now KOs through their remaining bulk.";
            return "Hold position. Their sandstorm expires in two turns and the matchup flips back.";
        }

        private string BuildTypeSummary()
        {
            return _paralysisLanded
                ? "You hit for ×2 and now move first — the type edge is finally worth something."
                : "You hit for ×2, but they outspeed and hit back for ×1.5.";
        }

        private string[] BuildWarnings()
        {
            if (_turn < 2) return Array.Empty<string>();
            if (_turn < 5)
            {
                return new[] { "Opponent may carry Water Absorb — your Water coverage could heal them instead." };
            }
            return new[]
            {
                "Opponent may carry Water Absorb — your Water coverage could heal them instead.",
                "Two party members are unscouted; switch projections for those slots are priors only.",
            };
        }

        /// <summary>Snaps a value to the type chart's legal multipliers so chips read correctly.</summary>
        private static float Quantise(float value)
        {
            if (value <= 0.15f) return 0f;
            if (value < 0.75f) return 0.5f;
            if (value < 1.5f) return 1f;
            if (value < 3f) return 2f;
            return 4f;
        }
    }

    /// <summary>
    /// A handful of invented species so names, types and portraits resolve while the real
    /// dex export is still being built. Deliberately not real creature names — a reviewer
    /// must be able to tell fake data from shipped data at a glance.
    /// </summary>
    public sealed class FakeSpeciesRegistry : ISpeciesRegistry
    {
        private readonly List<SpeciesData> _all = new List<SpeciesData>(8);
        private readonly Dictionary<int, SpeciesData> _byId = new Dictionary<int, SpeciesData>(8);

        /// <summary>Species id used as the player's active creature.</summary>
        public int PlayerLeadId => 901;

        /// <summary>Species id used as the opponent.</summary>
        public int OpponentId => 906;

        /// <summary>Stable instance ids for the fake party, so switch rows diff correctly.</summary>
        public readonly string[] PartyInstanceIds =
        {
            "fake-party-0", "fake-party-1", "fake-party-2", "fake-party-3", "fake-party-4", "fake-party-5",
        };

        public FakeSpeciesRegistry()
        {
            Add(901, "Emberling", ElementType.Fire, ElementType.None, 58, 72, 54, 80, 60, 92);
            Add(902, "Thornhare", ElementType.Grass, ElementType.Normal, 66, 78, 62, 55, 64, 88);
            Add(903, "Glacimew", ElementType.Ice, ElementType.Fairy, 72, 52, 70, 96, 88, 64);
            Add(904, "Voltnip", ElementType.Electric, ElementType.None, 54, 66, 48, 84, 56, 110);
            Add(905, "Bouldrake", ElementType.Rock, ElementType.Dragon, 94, 108, 106, 62, 74, 42);
            Add(906, "Tidefang", ElementType.Water, ElementType.Dark, 88, 102, 78, 64, 70, 96);
            Add(907, "Mothglow", ElementType.Bug, ElementType.Psychic, 62, 48, 58, 104, 92, 76);
        }

        private void Add(int id, string name, ElementType t1, ElementType t2,
            int hp, int atk, int def, int spa, int spd, int spe)
        {
            var species = new SpeciesData
            {
                Id = id,
                NationalDex = id - 900,
                NameEn = name,
                Type1 = t1,
                Type2 = t2,
                BaseStats = new[] { hp, atk, def, spa, spd, spe },
                Height = 12f,
                Weight = 240f,
                BaseExperience = 120,
                Generation = 9,
                Abilities = new[] { "placeholder_ability" },
                ArtKey = "Creature_" + id + "_" + name,
            };
            _all.Add(species);
            _byId[id] = species;
        }

        public int Count => _all.Count;
        public IReadOnlyList<SpeciesData> All => _all;

        public bool TryGet(int speciesId, out SpeciesData species) => _byId.TryGetValue(speciesId, out species);

        public bool TryFindByName(string nameEnOrKo, out SpeciesData species)
        {
            for (var i = 0; i < _all.Count; i++)
            {
                if (string.Equals(_all[i].NameEn, nameEnOrKo, StringComparison.OrdinalIgnoreCase))
                {
                    species = _all[i];
                    return true;
                }
            }
            species = null;
            return false;
        }

        /// <summary>Builds a party of instances matching <see cref="PartyInstanceIds"/>.</summary>
        public List<CreatureInstance> BuildParty()
        {
            var party = new List<CreatureInstance>(6);
            for (var i = 0; i < 6 && i < _all.Count; i++)
            {
                var species = _all[i];
                var maxHp = 90 + species.BaseStats[(int)StatKind.Hp];
                party.Add(new CreatureInstance
                {
                    SpeciesId = species.Id,
                    Nickname = null,
                    Level = 24 + i,
                    Experience = 4200 + i * 380,
                    MaxHp = maxHp,
                    CurrentHp = Mathf.RoundToInt(maxHp * Mathf.Lerp(1f, 0.35f, i / 5f)),
                    Stats = new[]
                    {
                        maxHp,
                        species.BaseStats[1] + 30, species.BaseStats[2] + 28,
                        species.BaseStats[3] + 30, species.BaseStats[4] + 28,
                        species.BaseStats[5] + 26,
                    },
                    Ivs = new[] { 24, 18, 26, 31, 12, 20 },
                    AbilityId = "placeholder_ability",
                    HeldItemId = i == 0 ? "focus_band" : null,
                    Status = i == 1 ? StatusCondition.Poison : StatusCondition.None,
                    InstanceId = PartyInstanceIds[i],
                    Moves = new List<MoveSlot>
                    {
                        new MoveSlot { MoveId = "ember_lash", CurrentPp = 12, MaxPp = 15 },
                        new MoveSlot { MoveId = "static_pulse", CurrentPp = 6, MaxPp = 10 },
                        new MoveSlot { MoveId = "guard_stance", CurrentPp = 20, MaxPp = 20 },
                        new MoveSlot { MoveId = "aqua_fang", CurrentPp = 0, MaxPp = 5 },
                    },
                });
            }
            return party;
        }
    }

    /// <summary>Minimal profile so party-dependent views render during isolated testing.</summary>
    public sealed class FakePlayerProfile : IPlayerProfile
    {
        private readonly List<CreatureInstance> _party;
        private readonly List<CreatureInstance> _storage = new List<CreatureInstance>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly HashSet<int> _caught = new HashSet<int>();
        private readonly Dictionary<string, int> _inventory = new Dictionary<string, int>
        {
            { "poke_ball", 12 }, { "great_ball", 5 }, { "potion", 7 },
            { "super_potion", 3 }, { "antidote", 2 }, { "revive", 1 },
        };

        public FakePlayerProfile(FakeSpeciesRegistry species)
        {
            _party = species.BuildParty();
            for (var i = 0; i < species.All.Count; i++)
            {
                _seen.Add(species.All[i].Id);
                if (i % 3 != 2) _caught.Add(species.All[i].Id);
            }
        }

        public string TrainerName => "Rowan";
        public int Money => 4820;
        public IReadOnlyList<CreatureInstance> Party => _party;
        public IReadOnlyList<CreatureInstance> Storage => _storage;
        public IReadOnlyCollection<int> SeenSpecies => _seen;
        public IReadOnlyCollection<int> CaughtSpecies => _caught;
        public IReadOnlyDictionary<string, int> Inventory => _inventory;

        public bool TryAddToParty(CreatureInstance creature)
        {
            if (_party.Count >= 6) return false;
            _party.Add(creature);
            return true;
        }

        public void AddItem(string itemId, int count)
        {
            _inventory.TryGetValue(itemId, out var existing);
            _inventory[itemId] = existing + count;
        }

        public bool ConsumeItem(string itemId, int count = 1)
        {
            if (!_inventory.TryGetValue(itemId, out var existing) || existing < count) return false;
            _inventory[itemId] = existing - count;
            return true;
        }

        public void MarkSeen(int speciesId) => _seen.Add(speciesId);
        public void MarkCaught(int speciesId) { _seen.Add(speciesId); _caught.Add(speciesId); }
    }

    /// <summary>Move definitions matching the ids the fake oracle references.</summary>
    public sealed class FakeMoveRegistry : IMoveRegistry
    {
        private readonly List<MoveData> _all = new List<MoveData>(6);
        private readonly Dictionary<string, MoveData> _byId = new Dictionary<string, MoveData>(6);

        public FakeMoveRegistry()
        {
            Add("ember_lash", "Ember Lash", ElementType.Fire, MoveCategory.Physical, 75, 100, 15);
            Add("static_pulse", "Static Pulse", ElementType.Electric, MoveCategory.Status, 0, 90, 10);
            Add("guard_stance", "Guard Stance", ElementType.Steel, MoveCategory.Status, 0, 0, 20);
            Add("aqua_fang", "Aqua Fang", ElementType.Water, MoveCategory.Physical, 95, 85, 5);
            Add("mind_shear", "Mind Shear", ElementType.Psychic, MoveCategory.Special, 85, 100, 10);
            Add("sandstorm", "Sandstorm", ElementType.Rock, MoveCategory.Status, 0, 0, 10);
        }

        private void Add(string id, string name, ElementType type, MoveCategory category, int power, int accuracy, int pp)
        {
            var move = new MoveData
            {
                Id = id, NameEn = name, Type = type, Category = category,
                Power = power, Accuracy = accuracy, PowerPoints = pp,
                InflictsStatus = id == "static_pulse" ? StatusCondition.Paralysis : StatusCondition.None,
                EffectChance = id == "static_pulse" ? 100 : 0,
            };
            _all.Add(move);
            _byId[id] = move;
        }

        public bool TryGet(string moveId, out MoveData move)
        {
            move = null;
            return !string.IsNullOrEmpty(moveId) && _byId.TryGetValue(moveId, out move);
        }

        public IReadOnlyList<MoveData> All => _all;
        public IReadOnlyList<MoveData> MovesFor(int speciesId, int level) => _all;
    }
}
