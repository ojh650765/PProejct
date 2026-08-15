using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Builds <see cref="CreatureInstance"/> values for the overworld: the starter, trainer
    /// parties, and any creature a save restores.
    ///
    /// Stats come from <see cref="ISpeciesRegistry"/> using the standard level formula. When the
    /// registry is not registered yet — normal during partial integration — a flat base-stat
    /// estimate is used so a starter still exists and the loop still runs. Once the intelligence
    /// worker registers the real dex, every creature built after that point is exact, and the
    /// fallback is loud about having been used.
    ///
    /// The battle engine owns mutation of these instances. This type only ever constructs them.
    /// </summary>
    public static class CreatureFactory
    {
        private static bool _warnedAboutMissingRegistry;

        /// <summary>Default IV spread when a creature is rolled rather than restored.</summary>
        private const int MaxIv = 31;

        /// <summary>
        /// Builds a creature at a level, deriving IVs from the supplied seed so a wild creature
        /// generated from an encounter seed is reproducible.
        /// </summary>
        public static CreatureInstance Create(int speciesId, int level, int seed)
        {
            var rng = new DeterministicRandom(seed);
            var creature = new CreatureInstance
            {
                SpeciesId = speciesId,
                Level = Mathf.Max(1, level),
                Nickname = null,
                Ivs = new int[StatKinds.BaseCount],
                Stats = new int[StatKinds.BaseCount],
                Moves = new List<MoveSlot>(4),
            };

            for (var i = 0; i < StatKinds.BaseCount; i++) creature.Ivs[i] = rng.RangeInclusive(0, MaxIv);

            ApplyStats(creature);
            ApplyAbility(creature);
            ApplyMoves(creature);
            creature.CurrentHp = creature.MaxHp;
            return creature;
        }

        /// <summary>Rebuilds derived stats after a level change or a save restore.</summary>
        public static void ApplyStats(CreatureInstance creature)
        {
            if (creature == null) return;
            if (creature.Stats == null || creature.Stats.Length < StatKinds.BaseCount)
                creature.Stats = new int[StatKinds.BaseCount];
            if (creature.Ivs == null || creature.Ivs.Length < StatKinds.BaseCount)
                creature.Ivs = new int[StatKinds.BaseCount];

            var baseStats = ResolveBaseStats(creature.SpeciesId);
            var level = Mathf.Max(1, creature.Level);

            // Standard main-series formulae with EVs held at zero. The battle worker may refine
            // this; keeping it here means the overworld can build a party before that lands.
            var hp = Mathf.FloorToInt((2f * baseStats[(int)StatKind.Hp] + creature.Ivs[(int)StatKind.Hp]) * level / 100f) + level + 10;
            creature.Stats[(int)StatKind.Hp] = hp;
            creature.MaxHp = hp;

            for (var stat = 1; stat < StatKinds.BaseCount; stat++)
            {
                var value = Mathf.FloorToInt((2f * baseStats[stat] + creature.Ivs[stat]) * level / 100f) + 5;
                creature.Stats[stat] = Mathf.Max(1, value);
            }

            creature.CurrentHp = Mathf.Clamp(creature.CurrentHp <= 0 ? hp : creature.CurrentHp, 0, hp);
        }

        private static int[] ResolveBaseStats(int speciesId)
        {
            if (ServiceHub.TryGet<ISpeciesRegistry>(out var registry) && registry.TryGet(speciesId, out var species))
            {
                if (species.BaseStats != null && species.BaseStats.Length >= StatKinds.BaseCount)
                    return species.BaseStats;
            }

            if (!_warnedAboutMissingRegistry)
            {
                _warnedAboutMissingRegistry = true;
                Debug.LogWarning("[CreatureFactory] ISpeciesRegistry unavailable; using estimated base "
                                 + "stats. Creatures built before the dex loads will not match the real data.");
            }

            // A flat mid-tier spread. Deliberately not per-species: guessing twelve stat lines
            // would produce numbers that look authoritative and are not.
            return new[] { 45, 49, 49, 49, 49, 45 };
        }

        private static void ApplyAbility(CreatureInstance creature)
        {
            if (!string.IsNullOrEmpty(creature.AbilityId)) return;
            if (!ServiceHub.TryGet<ISpeciesRegistry>(out var registry)) return;
            if (!registry.TryGet(creature.SpeciesId, out var species)) return;
            if (species.Abilities != null && species.Abilities.Length > 0) creature.AbilityId = species.Abilities[0];
        }

        private static void ApplyMoves(CreatureInstance creature)
        {
            if (creature.Moves == null) creature.Moves = new List<MoveSlot>(4);
            if (creature.Moves.Count > 0) return;
            if (!ServiceHub.TryGet<IMoveRegistry>(out var moves)) return;

            var learnset = moves.MovesFor(creature.SpeciesId, creature.Level);
            if (learnset == null) return;

            // Last four learned, which is the conventional wild/trainer moveset rule.
            var start = Mathf.Max(0, learnset.Count - 4);
            for (var i = start; i < learnset.Count && creature.Moves.Count < 4; i++)
            {
                var move = learnset[i];
                if (move == null) continue;
                creature.Moves.Add(new MoveSlot { MoveId = move.Id, CurrentPp = move.PowerPoints, MaxPp = move.PowerPoints });
            }
        }

        /// <summary>Full restore: HP to max, status cleared, PP refilled. The healing machine's job.</summary>
        public static void FullHeal(CreatureInstance creature)
        {
            if (creature == null) return;
            creature.CurrentHp = creature.MaxHp;
            creature.Status = StatusCondition.None;
            creature.StatusCounter = 0;
            if (creature.Moves == null) return;
            for (var i = 0; i < creature.Moves.Count; i++)
            {
                var slot = creature.Moves[i];
                slot.CurrentPp = slot.MaxPp;
                creature.Moves[i] = slot;
            }
        }

        /// <summary>Display name honouring the nickname, then the dex, then the slice fallback.</summary>
        public static string DisplayName(CreatureInstance creature)
        {
            if (creature == null) return string.Empty;
            if (!string.IsNullOrEmpty(creature.Nickname)) return creature.Nickname;
            if (ServiceHub.TryGet<ISpeciesRegistry>(out var registry) && registry.TryGet(creature.SpeciesId, out var species))
                return species.DisplayName;
            return SliceRoster.FallbackName(creature.SpeciesId);
        }
    }
}
