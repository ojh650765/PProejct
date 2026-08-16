using System;
using System.Collections.Generic;

namespace PokeLab.Core
{
    /// <summary>
    /// One row of the species dex, mirroring
    /// data/pokemon_master_corrected_with_abilities.csv from the source repository.
    /// Populated by the export pipeline into StreamingAssets/pokelab/species.json.
    /// </summary>
    [Serializable]
    public sealed class SpeciesData
    {
        /// <summary>KaggleID — the primary key the prediction model is indexed by.</summary>
        public int Id;
        public int NationalDex;
        public string NameEn;
        public string NameKo;
        public ElementType Type1 = ElementType.None;
        public ElementType Type2 = ElementType.None;

        /// <summary>Base stats indexed by <see cref="StatKind"/>, length <see cref="StatKinds.BaseCount"/>.</summary>
        public int[] BaseStats = new int[StatKinds.BaseCount];

        /// <summary>Decimetres, as stored by PokeAPI. Divide by 10 for metres.</summary>
        public float Height;
        /// <summary>Hectograms, as stored by PokeAPI. Divide by 10 for kilograms.</summary>
        public float Weight;
        public int BaseExperience;
        public int Generation;
        public bool Legendary;

        /// <summary>PokeAPI ability identifiers, e.g. "overgrow", "chlorophyll".</summary>
        public string[] Abilities = Array.Empty<string>();
        public string[] AbilitiesKo = Array.Empty<string>();

        /// <summary>PokeRogue starter cost 1-10, a human tier estimate. 0 when unknown.</summary>
        public int StarterCost;

        /// <summary>Addressable-style key for the art prefab, resolved by the art registry.</summary>
        public string ArtKey;

        public int BaseStatTotal
        {
            get
            {
                var total = 0;
                for (var i = 0; i < BaseStats.Length; i++) total += BaseStats[i];
                return total;
            }
        }

        public bool HasType(ElementType type) => type != ElementType.None && (Type1 == type || Type2 == type);

        public string DisplayName => Loc.Pick(NameEn, NameKo);
    }

    /// <summary>
    /// Immutable move definition. Authored as JSON in StreamingAssets/pokelab/moves.json
    /// so the battle and data workers can extend the pool without a recompile.
    /// </summary>
    [Serializable]
    public sealed class MoveData
    {
        public string Id;
        public string NameEn;
        public string NameKo;
        public ElementType Type = ElementType.Normal;
        public MoveCategory Category = MoveCategory.Physical;

        /// <summary>0 for status moves.</summary>
        public int Power;
        /// <summary>0-100. A value of 0 means the move never misses.</summary>
        public int Accuracy = 100;
        public int PowerPoints = 15;
        /// <summary>Higher goes first regardless of Speed. Usually 0.</summary>
        public int Priority;

        /// <summary>Extra crit stages beyond the base 1/24 roll.</summary>
        public int CritStageBonus;

        /// <summary>Status inflicted on hit, gated by <see cref="EffectChance"/>.</summary>
        public StatusCondition InflictsStatus = StatusCondition.None;
        public VolatileFlags InflictsVolatile = VolatileFlags.None;
        /// <summary>0-100. Applies to status and stat-stage side effects.</summary>
        public int EffectChance;

        /// <summary>Stat stage deltas applied to the target (or self when <see cref="TargetsSelf"/>).</summary>
        public StatStageChange[] StatChanges = Array.Empty<StatStageChange>();
        public bool TargetsSelf;

        /// <summary>Fraction of damage dealt that heals the user, 0-1.</summary>
        public float DrainRatio;
        /// <summary>Fraction of damage dealt taken as recoil, 0-1.</summary>
        public float RecoilRatio;
        /// <summary>Fraction of the user's max HP restored, 0-1. Status moves only.</summary>
        public float HealRatio;

        /// <summary>Inclusive multi-hit range. Both 0 means a single hit.</summary>
        public int MinHits;
        public int MaxHits;

        /// <summary>Presentation hints consumed by the VFX and cinematics workers.</summary>
        public string VfxKey;
        public string AnimationKey;
        /// <summary>True for moves whose animation should launch a travelling projectile.</summary>
        public bool IsProjectile;
        /// <summary>True for contact moves — drives the lunge animation and contact abilities.</summary>
        public bool MakesContact = true;

        public string DisplayName => Loc.Pick(NameEn, NameKo);
    }

    [Serializable]
    public struct StatStageChange
    {
        public StatKind Stat;
        /// <summary>Clamped to [-6, +6] once applied.</summary>
        public int Stages;
    }

    /// <summary>Read-only lookup over the exported dex. Implemented by the intelligence layer.</summary>
    public interface ISpeciesRegistry
    {
        int Count { get; }
        IReadOnlyList<SpeciesData> All { get; }
        bool TryGet(int speciesId, out SpeciesData species);
        bool TryFindByName(string nameEnOrKo, out SpeciesData species);
    }

    /// <summary>Read-only lookup over the move pool.</summary>
    public interface IMoveRegistry
    {
        bool TryGet(string moveId, out MoveData move);
        IReadOnlyList<MoveData> All { get; }
        /// <summary>The learnset a species is given at the supplied level.</summary>
        IReadOnlyList<MoveData> MovesFor(int speciesId, int level);
    }

    /// <summary>Type effectiveness, sourced from TYPE_MULTIPLIERS in domain.py.</summary>
    public interface ITypeChart
    {
        /// <summary>Single-type multiplier, one of 0, 0.5, 1, 2.</summary>
        float Multiplier(ElementType attack, ElementType defend);

        /// <summary>Combined multiplier against a one- or two-type defender.</summary>
        float Multiplier(ElementType attack, ElementType defend1, ElementType defend2);

        /// <summary>
        /// Defensive ability modifier from pokerogue_derived/ability_type_modifiers.csv
        /// (0 immunity, 0.5 resist, 1.25 amplify). Returns 1 when the ability is irrelevant.
        /// </summary>
        float AbilityModifier(string abilityIdentifier, ElementType attackType);
    }
}
