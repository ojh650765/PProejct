using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// Builds <see cref="CreatureInstance"/>s from dex data. Stats and max HP are always
    /// derived here from base stats, IVs and level — never authored — so a level 7 Pikachu
    /// rolled in the overworld and one restored from a save agree exactly.
    ///
    /// Every path takes a seed, and identity comes from <see cref="CreatureIds.Derive"/> so
    /// that every creature in the game shares one id scheme and an event stream carrying
    /// <see cref="ExperienceGainedEvent.InstanceId"/> stays reproducible from that seed.
    /// </summary>
    public static class CreatureFactory
    {
        /// <summary>Maximum individual value, matching the dataset's 0-31 range.</summary>
        public const int MaxIv = 31;

        /// <summary>
        /// Creates a creature of the given species and level.
        /// </summary>
        /// <param name="speciesId">Dex key, as used by <see cref="ISpeciesRegistry"/>.</param>
        /// <param name="level">Level, clamped to 1-100.</param>
        /// <param name="seed">Drives IVs, ability choice and the instance id.</param>
        /// <param name="species">Species lookup. Falls back to <see cref="ServiceHub"/> when null.</param>
        /// <param name="moves">Move lookup used to fill the four slots. Falls back to <see cref="ServiceHub"/> when null.</param>
        /// <param name="perfectIvs">True to give flat 31s — used by tests and by scripted story battles.</param>
        /// <param name="ordinal">
        /// Distinguishes creatures built from the same seed, e.g. the slot within a trainer's
        /// party. Two same-species, same-level creatures from one seed would otherwise share
        /// an instance id, and the presentation layers key their views on it.
        /// </param>
        public static CreatureInstance Create(
            int speciesId,
            int level,
            int seed,
            ISpeciesRegistry species = null,
            IMoveRegistry moves = null,
            bool perfectIvs = false,
            int ordinal = 0)
        {
            species ??= Resolve<ISpeciesRegistry>();
            moves ??= Resolve<IMoveRegistry>();

            if (species == null || !species.TryGet(speciesId, out var data))
                throw new InvalidOperationException(
                    $"Species {speciesId} is not in the registry. The dex must be loaded before creatures are built.");

            level = Math.Clamp(level, 1, StatMath.MaxLevel);
            var rng = new BattleRandom(seed);

            var creature = new CreatureInstance
            {
                SpeciesId = speciesId,
                Nickname = null,
                Level = level,
                Experience = StatMath.ExperienceForLevel(level),
                InstanceId = CreatureIds.Derive(speciesId, level, seed, ordinal),
            };

            for (var i = 0; i < StatKinds.BaseCount; i++)
                creature.Ivs[i] = perfectIvs ? MaxIv : rng.Range(0, MaxIv);

            creature.AbilityId = PickAbility(data, rng);
            RecomputeStats(creature, data);
            creature.CurrentHp = creature.MaxHp;

            if (moves != null) FillMoves(creature, moves.MovesFor(speciesId, level));

            return creature;
        }

        /// <summary>
        /// Recomputes <see cref="CreatureInstance.Stats"/> and <see cref="CreatureInstance.MaxHp"/>
        /// in place, preserving the creature's damage taken so a level-up heals by exactly
        /// the HP the level gained rather than topping the creature up.
        /// </summary>
        public static void RecomputeStats(CreatureInstance creature, SpeciesData species)
        {
            if (creature == null || species == null) return;

            if (creature.Stats == null || creature.Stats.Length < StatKinds.BaseCount)
                creature.Stats = new int[StatKinds.BaseCount];
            if (creature.Ivs == null || creature.Ivs.Length < StatKinds.BaseCount)
                creature.Ivs = new int[StatKinds.BaseCount];

            var previousMax = creature.MaxHp;

            var newMax = StatMath.ComputeHp(
                BaseStat(species, StatKind.Hp), creature.Ivs[(int)StatKind.Hp], creature.Level);

            creature.Stats[(int)StatKind.Hp] = newMax;
            creature.MaxHp = newMax;

            // Through BaseStat, not the array: that helper exists precisely to tolerate a
            // short or missing row in hand-authored data, and indexing directly turned such a
            // row into an IndexOutOfRangeException thrown from a mid-battle level-up.
            for (var i = 1; i < StatKinds.BaseCount; i++)
                creature.Stats[i] = StatMath.ComputeStat(
                    BaseStat(species, (StatKind)i), creature.Ivs[i], creature.Level);

            if (previousMax > 0 && newMax > previousMax && creature.CurrentHp > 0)
                creature.CurrentHp += newMax - previousMax;

            creature.CurrentHp = Math.Clamp(creature.CurrentHp, 0, newMax);
        }

        /// <summary>Fills the four move slots from a learnset, newest moves last.</summary>
        public static void FillMoves(CreatureInstance creature, IReadOnlyList<MoveData> learnset)
        {
            creature.Moves ??= new List<MoveSlot>(4);
            creature.Moves.Clear();
            if (learnset == null) return;

            // The registry returns the learnset in level order, so the last four entries are
            // the most recently learned — that is the set a wild creature should fight with.
            var start = Math.Max(0, learnset.Count - 4);
            for (var i = start; i < learnset.Count; i++)
            {
                var move = learnset[i];
                if (move == null) continue;
                creature.Moves.Add(new MoveSlot
                {
                    MoveId = move.Id,
                    CurrentPp = move.PowerPoints,
                    MaxPp = move.PowerPoints,
                });
            }
        }

        /// <summary>Restores every move slot to full PP. Called by healing stations and tests.</summary>
        public static void RestorePp(CreatureInstance creature)
        {
            if (creature?.Moves == null) return;
            for (var i = 0; i < creature.Moves.Count; i++)
            {
                var slot = creature.Moves[i];
                slot.CurrentPp = slot.MaxPp;
                creature.Moves[i] = slot;
            }
        }

        /// <summary>Base stat accessor that tolerates a short or missing array in hand-authored data.</summary>
        public static int BaseStat(SpeciesData species, StatKind stat)
        {
            var index = (int)stat;
            if (species?.BaseStats == null || index < 0 || index >= species.BaseStats.Length) return 1;
            return Math.Max(1, species.BaseStats[index]);
        }

        private static string PickAbility(SpeciesData data, BattleRandom rng)
        {
            if (data.Abilities == null || data.Abilities.Length == 0) return string.Empty;
            // Hidden abilities are not modelled, so any listed ability is equally likely.
            return data.Abilities[rng.Next(data.Abilities.Length)] ?? string.Empty;
        }

        private static T Resolve<T>() where T : class =>
            ServiceHub.TryGet<T>(out var service) ? service : null;
    }
}
