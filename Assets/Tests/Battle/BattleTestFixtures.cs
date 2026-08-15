using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle.Tests
{
    /// <summary>
    /// In-memory stand-ins for the data layer.
    ///
    /// The intelligence worker owns the real <see cref="ISpeciesRegistry"/>,
    /// <see cref="IMoveRegistry"/> and <see cref="ITypeChart"/> and the JSON behind them.
    /// The battle tests must not depend on that work landing first, and must not assert
    /// against data another worker is free to retune, so they run against these fakes.
    /// </summary>
    public sealed class FakeSpeciesRegistry : ISpeciesRegistry
    {
        private readonly Dictionary<int, SpeciesData> _byId = new Dictionary<int, SpeciesData>();
        private readonly List<SpeciesData> _all = new List<SpeciesData>();

        public int Count => _all.Count;
        public IReadOnlyList<SpeciesData> All => _all;

        public void Add(SpeciesData species)
        {
            _byId[species.Id] = species;
            _all.Add(species);
        }

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
    }

    /// <summary>Move lookup plus a per-species learnset the tests assign explicitly.</summary>
    public sealed class FakeMoveRegistry : IMoveRegistry
    {
        private readonly Dictionary<string, MoveData> _byId = new Dictionary<string, MoveData>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, List<MoveData>> _learnsets = new Dictionary<int, List<MoveData>>();
        private readonly List<MoveData> _all = new List<MoveData>();

        public IReadOnlyList<MoveData> All => _all;

        public MoveData Add(MoveData move)
        {
            _byId[move.Id] = move;
            _all.Add(move);
            return move;
        }

        public void SetLearnset(int speciesId, params string[] moveIds)
        {
            var list = new List<MoveData>();
            foreach (var id in moveIds)
                if (_byId.TryGetValue(id, out var move)) list.Add(move);
            _learnsets[speciesId] = list;
        }

        public bool TryGet(string moveId, out MoveData move)
        {
            move = null;
            return !string.IsNullOrEmpty(moveId) && _byId.TryGetValue(moveId, out move);
        }

        public IReadOnlyList<MoveData> MovesFor(int speciesId, int level) =>
            _learnsets.TryGetValue(speciesId, out var list) ? list : new List<MoveData>();
    }

    /// <summary>
    /// The standard 18x18 chart, encoded as the exceptions to neutrality. The engine's
    /// tests own this copy so they still pin type behaviour if the exported chart changes.
    /// </summary>
    public sealed class FakeTypeChart : ITypeChart
    {
        private readonly float[,] _table = new float[ElementTypes.Count, ElementTypes.Count];

        public FakeTypeChart()
        {
            for (var a = 0; a < ElementTypes.Count; a++)
                for (var d = 0; d < ElementTypes.Count; d++)
                    _table[a, d] = 1f;

            Set(ElementType.Normal, nve: new[] { ElementType.Rock, ElementType.Steel }, immune: new[] { ElementType.Ghost });
            Set(ElementType.Fire,
                se: new[] { ElementType.Grass, ElementType.Ice, ElementType.Bug, ElementType.Steel },
                nve: new[] { ElementType.Fire, ElementType.Water, ElementType.Rock, ElementType.Dragon });
            Set(ElementType.Water,
                se: new[] { ElementType.Fire, ElementType.Ground, ElementType.Rock },
                nve: new[] { ElementType.Water, ElementType.Grass, ElementType.Dragon });
            Set(ElementType.Electric,
                se: new[] { ElementType.Water, ElementType.Flying },
                nve: new[] { ElementType.Electric, ElementType.Grass, ElementType.Dragon },
                immune: new[] { ElementType.Ground });
            Set(ElementType.Grass,
                se: new[] { ElementType.Water, ElementType.Ground, ElementType.Rock },
                nve: new[] { ElementType.Fire, ElementType.Grass, ElementType.Poison, ElementType.Flying, ElementType.Bug, ElementType.Dragon, ElementType.Steel });
            Set(ElementType.Ice,
                se: new[] { ElementType.Grass, ElementType.Ground, ElementType.Flying, ElementType.Dragon },
                nve: new[] { ElementType.Fire, ElementType.Water, ElementType.Ice, ElementType.Steel });
            Set(ElementType.Fighting,
                se: new[] { ElementType.Normal, ElementType.Ice, ElementType.Rock, ElementType.Dark, ElementType.Steel },
                nve: new[] { ElementType.Poison, ElementType.Flying, ElementType.Psychic, ElementType.Bug, ElementType.Fairy },
                immune: new[] { ElementType.Ghost });
            Set(ElementType.Poison,
                se: new[] { ElementType.Grass, ElementType.Fairy },
                nve: new[] { ElementType.Poison, ElementType.Ground, ElementType.Rock, ElementType.Ghost },
                immune: new[] { ElementType.Steel });
            Set(ElementType.Ground,
                se: new[] { ElementType.Fire, ElementType.Electric, ElementType.Poison, ElementType.Rock, ElementType.Steel },
                nve: new[] { ElementType.Grass, ElementType.Bug },
                immune: new[] { ElementType.Flying });
            Set(ElementType.Flying,
                se: new[] { ElementType.Grass, ElementType.Fighting, ElementType.Bug },
                nve: new[] { ElementType.Electric, ElementType.Rock, ElementType.Steel });
            Set(ElementType.Psychic,
                se: new[] { ElementType.Fighting, ElementType.Poison },
                nve: new[] { ElementType.Psychic, ElementType.Steel },
                immune: new[] { ElementType.Dark });
            Set(ElementType.Bug,
                se: new[] { ElementType.Grass, ElementType.Psychic, ElementType.Dark },
                nve: new[] { ElementType.Fire, ElementType.Fighting, ElementType.Poison, ElementType.Flying, ElementType.Ghost, ElementType.Steel, ElementType.Fairy });
            Set(ElementType.Rock,
                se: new[] { ElementType.Fire, ElementType.Ice, ElementType.Flying, ElementType.Bug },
                nve: new[] { ElementType.Fighting, ElementType.Ground, ElementType.Steel });
            Set(ElementType.Ghost,
                se: new[] { ElementType.Psychic, ElementType.Ghost },
                nve: new[] { ElementType.Dark },
                immune: new[] { ElementType.Normal });
            Set(ElementType.Dragon,
                se: new[] { ElementType.Dragon },
                nve: new[] { ElementType.Steel },
                immune: new[] { ElementType.Fairy });
            Set(ElementType.Dark,
                se: new[] { ElementType.Psychic, ElementType.Ghost },
                nve: new[] { ElementType.Fighting, ElementType.Dark, ElementType.Fairy });
            Set(ElementType.Steel,
                se: new[] { ElementType.Ice, ElementType.Rock, ElementType.Fairy },
                nve: new[] { ElementType.Fire, ElementType.Water, ElementType.Electric, ElementType.Steel });
            Set(ElementType.Fairy,
                se: new[] { ElementType.Fighting, ElementType.Dragon, ElementType.Dark },
                nve: new[] { ElementType.Fire, ElementType.Poison, ElementType.Steel });
        }

        private void Set(ElementType attack, ElementType[] se = null, ElementType[] nve = null, ElementType[] immune = null)
        {
            if (se != null) foreach (var d in se) _table[(int)attack, (int)d] = 2f;
            if (nve != null) foreach (var d in nve) _table[(int)attack, (int)d] = 0.5f;
            if (immune != null) foreach (var d in immune) _table[(int)attack, (int)d] = 0f;
        }

        public float Multiplier(ElementType attack, ElementType defend)
        {
            if (attack == ElementType.None || defend == ElementType.None) return 1f;
            return _table[(int)attack, (int)defend];
        }

        public float Multiplier(ElementType attack, ElementType defend1, ElementType defend2)
        {
            var multiplier = Multiplier(attack, defend1);
            if (defend2 != ElementType.None && defend2 != defend1) multiplier *= Multiplier(attack, defend2);
            return multiplier;
        }

        /// <summary>
        /// The fake reports no ability modifiers: the engine's own ability hooks cover the
        /// immunities the slice needs, and folding them in here too would double-count.
        /// </summary>
        public float AbilityModifier(string abilityIdentifier, ElementType attackType) => 1f;
    }

    /// <summary>The 12-species slice roster and a move pool wide enough to exercise every mechanic.</summary>
    public static class TestData
    {
        public const int Bulbasaur = 1;
        public const int Charmander = 5;
        public const int Squirtle = 10;
        public const int Pidgey = 21;
        public const int Rattata = 25;
        public const int Pikachu = 31;
        public const int Zubat = 47;
        public const int Oddish = 49;
        public const int Poliwag = 66;
        public const int Machop = 73;
        public const int Geodude = 81;
        public const int Gastly = 100;

        public static FakeSpeciesRegistry Species()
        {
            var registry = new FakeSpeciesRegistry();

            registry.Add(Make(Bulbasaur, 1, "Bulbasaur", ElementType.Grass, ElementType.Poison,
                45, 49, 49, 65, 65, 45, 64, "overgrow", "chlorophyll"));
            registry.Add(Make(Charmander, 4, "Charmander", ElementType.Fire, ElementType.None,
                39, 52, 43, 60, 50, 65, 62, "blaze"));
            registry.Add(Make(Squirtle, 7, "Squirtle", ElementType.Water, ElementType.None,
                44, 48, 65, 50, 64, 43, 63, "torrent", "rain-dish"));
            registry.Add(Make(Pidgey, 16, "Pidgey", ElementType.Normal, ElementType.Flying,
                40, 45, 40, 35, 35, 56, 50, "keen-eye"));
            registry.Add(Make(Rattata, 19, "Rattata", ElementType.Normal, ElementType.None,
                30, 56, 35, 25, 35, 72, 51, "run-away", "guts"));
            registry.Add(Make(Pikachu, 25, "Pikachu", ElementType.Electric, ElementType.None,
                35, 55, 40, 50, 50, 90, 112, "static", "lightning-rod"));
            registry.Add(Make(Zubat, 41, "Zubat", ElementType.Poison, ElementType.Flying,
                40, 45, 35, 30, 40, 55, 49, "inner-focus"));
            registry.Add(Make(Oddish, 43, "Oddish", ElementType.Grass, ElementType.Poison,
                45, 50, 55, 75, 65, 30, 64, "chlorophyll", "run-away"));
            registry.Add(Make(Poliwag, 60, "Poliwag", ElementType.Water, ElementType.None,
                40, 50, 40, 40, 40, 90, 60, "swift-swim"));
            registry.Add(Make(Machop, 66, "Machop", ElementType.Fighting, ElementType.None,
                70, 80, 50, 35, 35, 35, 61, "guts", "no-guard"));
            registry.Add(Make(Geodude, 74, "Geodude", ElementType.Rock, ElementType.Ground,
                40, 80, 100, 30, 30, 20, 60, "rock-head", "sturdy", "sand-veil"));
            registry.Add(Make(Gastly, 92, "Gastly", ElementType.Ghost, ElementType.Poison,
                30, 35, 30, 100, 35, 80, 62, "levitate"));

            return registry;
        }

        private static SpeciesData Make(int id, int dex, string name, ElementType t1, ElementType t2,
            int hp, int atk, int def, int spa, int spd, int spe, int baseExp, params string[] abilities) =>
            new SpeciesData
            {
                Id = id,
                NationalDex = dex,
                NameEn = name,
                NameKo = null,
                Type1 = t1,
                Type2 = t2,
                BaseStats = new[] { hp, atk, def, spa, spd, spe },
                BaseExperience = baseExp,
                Generation = 1,
                Abilities = abilities,
                ArtKey = $"Creature_{id}_{name}",
            };

        public static FakeMoveRegistry Moves()
        {
            var registry = new FakeMoveRegistry();

            registry.Add(new MoveData { Id = "tackle", NameEn = "Tackle", Type = ElementType.Normal, Category = MoveCategory.Physical, Power = 40, Accuracy = 100, PowerPoints = 35 });
            registry.Add(new MoveData { Id = "scratch", NameEn = "Scratch", Type = ElementType.Normal, Category = MoveCategory.Physical, Power = 40, Accuracy = 100, PowerPoints = 35 });
            registry.Add(new MoveData { Id = "quick-attack", NameEn = "Quick Attack", Type = ElementType.Normal, Category = MoveCategory.Physical, Power = 40, Accuracy = 100, PowerPoints = 30, Priority = 1 });
            registry.Add(new MoveData { Id = "karate-chop", NameEn = "Karate Chop", Type = ElementType.Fighting, Category = MoveCategory.Physical, Power = 50, Accuracy = 100, PowerPoints = 25, CritStageBonus = 1 });
            registry.Add(new MoveData { Id = "wing-attack", NameEn = "Wing Attack", Type = ElementType.Flying, Category = MoveCategory.Physical, Power = 60, Accuracy = 100, PowerPoints = 35 });
            registry.Add(new MoveData { Id = "rock-throw", NameEn = "Rock Throw", Type = ElementType.Rock, Category = MoveCategory.Physical, Power = 50, Accuracy = 90, PowerPoints = 15 });
            registry.Add(new MoveData { Id = "ember", NameEn = "Ember", Type = ElementType.Fire, Category = MoveCategory.Special, Power = 40, Accuracy = 100, PowerPoints = 25, InflictsStatus = StatusCondition.Burn, EffectChance = 10, MakesContact = false, IsProjectile = true });
            registry.Add(new MoveData { Id = "water-gun", NameEn = "Water Gun", Type = ElementType.Water, Category = MoveCategory.Special, Power = 40, Accuracy = 100, PowerPoints = 25, MakesContact = false, IsProjectile = true });
            registry.Add(new MoveData { Id = "vine-whip", NameEn = "Vine Whip", Type = ElementType.Grass, Category = MoveCategory.Physical, Power = 45, Accuracy = 100, PowerPoints = 25 });
            registry.Add(new MoveData { Id = "thunder-shock", NameEn = "Thunder Shock", Type = ElementType.Electric, Category = MoveCategory.Special, Power = 40, Accuracy = 100, PowerPoints = 30, InflictsStatus = StatusCondition.Paralysis, EffectChance = 10, MakesContact = false, IsProjectile = true });
            registry.Add(new MoveData { Id = "confusion", NameEn = "Confusion", Type = ElementType.Psychic, Category = MoveCategory.Special, Power = 50, Accuracy = 100, PowerPoints = 25, MakesContact = false });
            registry.Add(new MoveData { Id = "double-slap", NameEn = "Double Slap", Type = ElementType.Normal, Category = MoveCategory.Physical, Power = 15, Accuracy = 85, PowerPoints = 10, MinHits = 2, MaxHits = 5 });
            registry.Add(new MoveData { Id = "take-down", NameEn = "Take Down", Type = ElementType.Normal, Category = MoveCategory.Physical, Power = 90, Accuracy = 85, PowerPoints = 20, RecoilRatio = 0.25f });
            registry.Add(new MoveData { Id = "absorb", NameEn = "Absorb", Type = ElementType.Grass, Category = MoveCategory.Special, Power = 20, Accuracy = 100, PowerPoints = 25, DrainRatio = 0.5f, MakesContact = false });

            registry.Add(new MoveData { Id = "gust", NameEn = "Gust", Type = ElementType.Flying, Category = MoveCategory.Special, Power = 40, Accuracy = 100, PowerPoints = 35, MakesContact = false });
            registry.Add(new MoveData { Id = "tail-whip", NameEn = "Tail Whip", Type = ElementType.Normal, Category = MoveCategory.Status, Power = 0, Accuracy = 100, PowerPoints = 30, MakesContact = false, StatChanges = new[] { new StatStageChange { Stat = StatKind.Defense, Stages = -1 } } });
            registry.Add(new MoveData { Id = "growl", NameEn = "Growl", Type = ElementType.Normal, Category = MoveCategory.Status, Power = 0, Accuracy = 100, PowerPoints = 40, MakesContact = false, StatChanges = new[] { new StatStageChange { Stat = StatKind.Attack, Stages = -1 } } });
            registry.Add(new MoveData { Id = "swords-dance", NameEn = "Swords Dance", Type = ElementType.Normal, Category = MoveCategory.Status, Power = 0, Accuracy = 0, PowerPoints = 20, MakesContact = false, TargetsSelf = true, StatChanges = new[] { new StatStageChange { Stat = StatKind.Attack, Stages = 2 } } });
            registry.Add(new MoveData { Id = "sand-attack", NameEn = "Sand Attack", Type = ElementType.Ground, Category = MoveCategory.Status, Power = 0, Accuracy = 100, PowerPoints = 15, MakesContact = false, StatChanges = new[] { new StatStageChange { Stat = StatKind.Accuracy, Stages = -1 } } });
            registry.Add(new MoveData { Id = "thunder-wave", NameEn = "Thunder Wave", Type = ElementType.Electric, Category = MoveCategory.Status, Power = 0, Accuracy = 90, PowerPoints = 20, MakesContact = false, InflictsStatus = StatusCondition.Paralysis });
            registry.Add(new MoveData { Id = "will-o-wisp", NameEn = "Will-O-Wisp", Type = ElementType.Fire, Category = MoveCategory.Status, Power = 0, Accuracy = 85, PowerPoints = 15, MakesContact = false, InflictsStatus = StatusCondition.Burn });
            registry.Add(new MoveData { Id = "toxic", NameEn = "Toxic", Type = ElementType.Poison, Category = MoveCategory.Status, Power = 0, Accuracy = 90, PowerPoints = 10, MakesContact = false, InflictsStatus = StatusCondition.BadPoison });
            registry.Add(new MoveData { Id = "poison-powder", NameEn = "Poison Powder", Type = ElementType.Poison, Category = MoveCategory.Status, Power = 0, Accuracy = 75, PowerPoints = 35, MakesContact = false, InflictsStatus = StatusCondition.Poison });
            registry.Add(new MoveData { Id = "sleep-powder", NameEn = "Sleep Powder", Type = ElementType.Grass, Category = MoveCategory.Status, Power = 0, Accuracy = 75, PowerPoints = 15, MakesContact = false, InflictsStatus = StatusCondition.Sleep });
            registry.Add(new MoveData { Id = "ice-beam", NameEn = "Ice Beam", Type = ElementType.Ice, Category = MoveCategory.Special, Power = 90, Accuracy = 100, PowerPoints = 10, MakesContact = false, InflictsStatus = StatusCondition.Freeze, EffectChance = 10 });
            registry.Add(new MoveData { Id = "confuse-ray", NameEn = "Confuse Ray", Type = ElementType.Ghost, Category = MoveCategory.Status, Power = 0, Accuracy = 100, PowerPoints = 10, MakesContact = false, InflictsVolatile = VolatileFlags.Confused });
            registry.Add(new MoveData { Id = "leech-seed", NameEn = "Leech Seed", Type = ElementType.Grass, Category = MoveCategory.Status, Power = 0, Accuracy = 90, PowerPoints = 10, MakesContact = false, InflictsVolatile = VolatileFlags.LeechSeeded });
            registry.Add(new MoveData { Id = "protect", NameEn = "Protect", Type = ElementType.Normal, Category = MoveCategory.Status, Power = 0, Accuracy = 0, PowerPoints = 10, MakesContact = false, TargetsSelf = true, InflictsVolatile = VolatileFlags.Protected });
            registry.Add(new MoveData { Id = "bite", NameEn = "Bite", Type = ElementType.Dark, Category = MoveCategory.Physical, Power = 60, Accuracy = 100, PowerPoints = 25, InflictsVolatile = VolatileFlags.Flinched, EffectChance = 30 });
            registry.Add(new MoveData { Id = "recover", NameEn = "Recover", Type = ElementType.Normal, Category = MoveCategory.Status, Power = 0, Accuracy = 0, PowerPoints = 10, MakesContact = false, TargetsSelf = true, HealRatio = 0.5f });

            registry.SetLearnset(Bulbasaur, "tackle", "growl", "vine-whip", "leech-seed");
            registry.SetLearnset(Charmander, "scratch", "growl", "ember", "take-down");
            registry.SetLearnset(Squirtle, "tackle", "tail-whip", "water-gun", "protect");
            registry.SetLearnset(Pidgey, "tackle", "sand-attack", "gust", "wing-attack");
            registry.SetLearnset(Rattata, "tackle", "quick-attack", "bite", "take-down");
            registry.SetLearnset(Pikachu, "thunder-shock", "growl", "quick-attack", "thunder-wave");
            registry.SetLearnset(Zubat, "tackle", "confuse-ray", "bite", "wing-attack");
            registry.SetLearnset(Oddish, "absorb", "sleep-powder", "poison-powder", "vine-whip");
            registry.SetLearnset(Poliwag, "water-gun", "tackle", "double-slap", "ice-beam");
            registry.SetLearnset(Machop, "karate-chop", "tackle", "swords-dance", "take-down");
            registry.SetLearnset(Geodude, "tackle", "rock-throw", "take-down", "protect");
            registry.SetLearnset(Gastly, "confusion", "confuse-ray", "toxic", "will-o-wisp");

            return registry;
        }
    }

    /// <summary>Builds engines and creatures for tests without repeating the wiring in every fixture.</summary>
    public static class BattleTestBuilder
    {
        public static FakeSpeciesRegistry SpeciesRegistry { get; private set; }
        public static FakeMoveRegistry MoveRegistry { get; private set; }
        public static FakeTypeChart Chart { get; private set; }

        /// <summary>Fresh registries plus an engine wired to them. Never touches <see cref="ServiceHub"/>.</summary>
        public static BattleEngine Engine(BattleAi ai = null)
        {
            SpeciesRegistry = TestData.Species();
            MoveRegistry = TestData.Moves();
            Chart = new FakeTypeChart();
            return new BattleEngine(SpeciesRegistry, MoveRegistry, Chart, ai);
        }

        /// <summary>A creature with derived stats, perfect IVs and a chosen move list.</summary>
        public static CreatureInstance Creature(int speciesId, int level, params string[] moveIds)
        {
            var creature = CreatureFactory.Create(speciesId, level, speciesId * 100 + level,
                SpeciesRegistry, MoveRegistry, perfectIvs: true);

            if (moveIds != null && moveIds.Length > 0)
            {
                creature.Moves.Clear();
                foreach (var id in moveIds)
                {
                    if (!MoveRegistry.TryGet(id, out var move)) continue;
                    creature.Moves.Add(new MoveSlot { MoveId = id, CurrentPp = move.PowerPoints, MaxPp = move.PowerPoints });
                }
            }

            return creature;
        }

        /// <summary>Fixes a creature's ability, since the factory picks one from the species list.</summary>
        public static CreatureInstance WithAbility(this CreatureInstance creature, string abilityId)
        {
            creature.AbilityId = abilityId;
            return creature;
        }

        public static CreatureInstance WithHeldItem(this CreatureInstance creature, string itemId)
        {
            creature.HeldItemId = itemId;
            return creature;
        }

        public static CreatureInstance WithStatus(this CreatureInstance creature, StatusCondition status, int counter = 0)
        {
            creature.Status = status;
            creature.StatusCounter = counter;
            return creature;
        }

        /// <summary>Overrides derived stats so a test can hand-compute a damage figure.</summary>
        public static CreatureInstance WithStats(this CreatureInstance creature, int hp, int atk, int def, int spa, int spd, int spe)
        {
            creature.Stats = new[] { hp, atk, def, spa, spd, spe };
            creature.MaxHp = hp;
            creature.CurrentHp = hp;
            return creature;
        }

        public static IList<CreatureInstance> Party(params CreatureInstance[] members) => new List<CreatureInstance>(members);

        /// <summary>Every event of a type in a stream, in order.</summary>
        public static List<T> OfType<T>(this IReadOnlyList<BattleEvent> stream) where T : BattleEvent
        {
            var result = new List<T>();
            for (var i = 0; i < stream.Count; i++)
                if (stream[i] is T typed) result.Add(typed);
            return result;
        }

        /// <summary>
        /// Stable text rendering of an event stream. Two runs of the same battle must
        /// produce identical strings — this is the determinism assertion's yardstick.
        /// </summary>
        public static string Signature(this IReadOnlyList<BattleEvent> stream)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < stream.Count; i++) builder.Append(Describe(stream[i])).Append('\n');
            return builder.ToString();
        }

        private static string Describe(BattleEvent evt)
        {
            switch (evt)
            {
                case BattleStartedEvent e: return $"T{e.Turn} Start {e.Kind} {e.Weather}";
                case CreatureSentOutEvent e: return $"T{e.Turn} SentOut {e.Side} {e.Creature.InstanceId} rep={e.IsReplacement}";
                case CreatureWithdrawnEvent e: return $"T{e.Turn} Withdrawn {e.Side} {e.Creature.InstanceId}";
                case MoveDeclaredEvent e: return $"T{e.Turn} Declared {e.Side} {e.MoveId}";
                case MoveExecutedEvent e: return $"T{e.Turn} Executed {e.Attacker}->{e.Target} {e.MoveId} {e.HitIndex}/{e.HitCount}";
                case MoveMissedEvent e: return $"T{e.Turn} Missed {e.Attacker}->{e.Target} {e.MoveId} immune={e.WasImmune} by={e.BlockingAbilityId}";
                case DamageDealtEvent e: return $"T{e.Turn} Damage {e.Target} {e.Amount} hp={e.RemainingHp}/{e.MaxHp} crit={e.Critical} x{e.TypeMultiplier} src={e.IndirectSourceId}";
                case HealedEvent e: return $"T{e.Turn} Heal {e.Target} {e.Amount} hp={e.RemainingHp}/{e.MaxHp} src={e.SourceId}";
                case StatusChangedEvent e: return $"T{e.Turn} Status {e.Target} {e.Previous}->{e.Current}";
                case VolatileChangedEvent e: return $"T{e.Turn} Volatile {e.Target} +{e.Added} -{e.Removed}";
                case StatStageChangedEvent e: return $"T{e.Turn} Stage {e.Target} {e.Stat} {e.Delta} => {e.NewStage}";
                case AbilityTriggeredEvent e: return $"T{e.Turn} Ability {e.Side} {e.AbilityId}";
                case ItemUsedEvent e: return $"T{e.Turn} Item {e.Side} {e.ItemId}";
                case WeatherChangedEvent e: return $"T{e.Turn} Weather {e.Previous}->{e.Current}";
                case CreatureFaintedEvent e: return $"T{e.Turn} Fainted {e.Side} {e.Creature.InstanceId}";
                case CaptureAttemptEvent e: return $"T{e.Turn} Capture {e.BallId} shakes={e.Shakes} ok={e.Succeeded} p={e.CatchProbability:F6}";
                case ExperienceGainedEvent e: return $"T{e.Turn} Exp {e.InstanceId} +{e.Amount} total={e.NewTotal} lvl={e.NewLevel} up={e.LeveledUp}";
                case BattleEndedEvent e: return $"T{e.Turn} End {e.Outcome} money={e.MoneyEarned}";
                case MessageEvent e: return $"T{e.Turn} Message {e.Text}";
                default: return $"T{evt.Turn} {evt.GetType().Name}";
            }
        }
    }
}
