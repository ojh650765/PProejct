using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Intelligence.Data
{
    /// <summary>
    /// The authored move pool and learnsets from moves.json.
    ///
    /// Learnsets are stored pre-sorted by level, and the per-(species, level) result is
    /// memoised, because the battle layer asks for them whenever a creature is built or
    /// levels up and the answer never changes.
    /// </summary>
    public sealed class MoveRegistry : IMoveRegistry
    {
        /// <summary>How many moves a creature may carry — the mainline limit.</summary>
        public const int MaxKnownMoves = 4;

        private readonly MoveData[] _ordered;
        private readonly Dictionary<string, MoveData> _byId;
        private readonly Dictionary<int, LearnsetEntry[]> _learnsets;
        private readonly Dictionary<long, MoveData[]> _learnsetCache = new Dictionary<long, MoveData[]>();

        private readonly struct LearnsetEntry
        {
            public readonly int Level;
            public readonly MoveData Move;
            public LearnsetEntry(int level, MoveData move) { Level = level; Move = move; }
        }

        private MoveRegistry(MoveData[] ordered, Dictionary<string, MoveData> byId,
                             Dictionary<int, LearnsetEntry[]> learnsets)
        {
            _ordered = ordered;
            _byId = byId;
            _learnsets = learnsets;
        }

        public IReadOnlyList<MoveData> All => _ordered;

        public bool TryGet(string moveId, out MoveData move)
        {
            move = null;
            if (string.IsNullOrEmpty(moveId)) return false;
            return _byId.TryGetValue(moveId, out move);
        }

        /// <summary>
        /// Everything the species has learned by this level, newest last, capped at the
        /// four most recent — the same rule the games use when a creature is generated.
        /// </summary>
        public IReadOnlyList<MoveData> MovesFor(int speciesId, int level)
        {
            var cacheKey = ((long)speciesId << 32) | (uint)Math.Max(0, level);
            if (_learnsetCache.TryGetValue(cacheKey, out var cached)) return cached;

            MoveData[] result;
            if (!_learnsets.TryGetValue(speciesId, out var entries))
            {
                result = Array.Empty<MoveData>();
            }
            else
            {
                var learned = new List<MoveData>(MaxKnownMoves);
                foreach (var entry in entries)
                {
                    if (entry.Level > level) break; // entries are level-sorted
                    // A relearned move keeps its original slot rather than duplicating.
                    learned.Remove(entry.Move);
                    learned.Add(entry.Move);
                    if (learned.Count > MaxKnownMoves) learned.RemoveAt(0);
                }
                result = learned.ToArray();
            }

            _learnsetCache[cacheKey] = result;
            return result;
        }

        public static MoveRegistry Load(string json)
        {
            var root = JsonValue.Parse(json);
            var rows = root["Moves"].AsArray();
            if (rows.Count == 0) throw new InvalidOperationException("moves.json contains no moves.");

            var ordered = new MoveData[rows.Count];
            var byId = new Dictionary<string, MoveData>(rows.Count, StringComparer.Ordinal);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var changes = row["StatChanges"].AsArray();
                var statChanges = new StatStageChange[changes.Count];
                for (var c = 0; c < changes.Count; c++)
                {
                    statChanges[c] = new StatStageChange
                    {
                        Stat = (StatKind)changes[c]["Stat"].AsInt(),
                        Stages = changes[c]["Stages"].AsInt(),
                    };
                }

                var move = new MoveData
                {
                    Id = row["Id"].AsString(string.Empty),
                    NameEn = row["NameEn"].AsString(string.Empty),
                    NameKo = row["NameKo"].AsString(string.Empty),
                    Type = (ElementType)row["Type"].AsInt(-1),
                    Category = (MoveCategory)row["Category"].AsInt(),
                    Power = row["Power"].AsInt(),
                    Accuracy = row["Accuracy"].AsInt(100),
                    PowerPoints = row["PowerPoints"].AsInt(15),
                    Priority = row["Priority"].AsInt(),
                    CritStageBonus = row["CritStageBonus"].AsInt(),
                    InflictsStatus = (StatusCondition)row["InflictsStatus"].AsInt(),
                    InflictsVolatile = (VolatileFlags)row["InflictsVolatile"].AsInt(),
                    EffectChance = row["EffectChance"].AsInt(),
                    StatChanges = statChanges,
                    TargetsSelf = row["TargetsSelf"].AsBool(),
                    DrainRatio = row["DrainRatio"].AsFloat(),
                    RecoilRatio = row["RecoilRatio"].AsFloat(),
                    HealRatio = row["HealRatio"].AsFloat(),
                    MinHits = row["MinHits"].AsInt(),
                    MaxHits = row["MaxHits"].AsInt(),
                    VfxKey = row["VfxKey"].AsString(string.Empty),
                    AnimationKey = row["AnimationKey"].AsString(string.Empty),
                    IsProjectile = row["IsProjectile"].AsBool(),
                    MakesContact = row["MakesContact"].AsBool(true),
                };

                if (string.IsNullOrEmpty(move.Id))
                    throw new InvalidOperationException($"moves.json entry {i} has no Id.");
                ordered[i] = move;
                byId[move.Id] = move;
            }

            var learnsets = new Dictionary<int, LearnsetEntry[]>();
            foreach (var set in root["Learnsets"].AsArray())
            {
                var speciesId = set["SpeciesId"].AsInt();
                var raw = set["Entries"].AsArray();
                var entries = new List<LearnsetEntry>(raw.Count);
                var authoredOrder = new List<int>(raw.Count);
                foreach (var entry in raw)
                {
                    var moveId = entry["MoveId"].AsString();
                    if (!byId.TryGetValue(moveId ?? string.Empty, out var move))
                        throw new InvalidOperationException(
                            $"learnset for species {speciesId} references unknown move '{moveId}'.");
                    authoredOrder.Add(entries.Count);
                    entries.Add(new LearnsetEntry(entry["Level"].AsInt(1), move));
                }

                // Sort defensively: MovesFor's early-out depends on level order. List.Sort
                // is unstable, so the authored index breaks ties — moves learned at the
                // same level must keep the order the export wrote them in.
                var indices = authoredOrder.ToArray();
                var sorted = entries.ToArray();
                Array.Sort(indices, (a, b) =>
                {
                    var byLevel = sorted[a].Level.CompareTo(sorted[b].Level);
                    return byLevel != 0 ? byLevel : a.CompareTo(b);
                });
                var ordered1 = new LearnsetEntry[indices.Length];
                for (var i = 0; i < indices.Length; i++) ordered1[i] = sorted[indices[i]];
                learnsets[speciesId] = ordered1;
            }

            return new MoveRegistry(ordered, byId, learnsets);
        }
    }
}
