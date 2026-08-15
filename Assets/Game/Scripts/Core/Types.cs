using System;

namespace PokeLab.Core
{
    /// <summary>
    /// The 18 elemental types, ordered to match the type chart rows exported from
    /// pokemon_battle_app/domain.py. The integer values are part of the serialized
    /// data contract in StreamingAssets/pokelab — do not reorder.
    /// </summary>
    public enum ElementType
    {
        None = -1,
        Normal = 0,
        Fire = 1,
        Water = 2,
        Electric = 3,
        Grass = 4,
        Ice = 5,
        Fighting = 6,
        Poison = 7,
        Ground = 8,
        Flying = 9,
        Psychic = 10,
        Bug = 11,
        Rock = 12,
        Ghost = 13,
        Dragon = 14,
        Dark = 15,
        Steel = 16,
        Fairy = 17,
    }

    public static class ElementTypes
    {
        public const int Count = 18;

        public static bool TryParse(string raw, out ElementType type)
        {
            type = ElementType.None;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Enum.TryParse(raw.Trim(), true, out type) && type != ElementType.None;
        }
    }

    /// <summary>Battle stats. Order matches the STAT_COLUMNS tuple in backend.py.</summary>
    public enum StatKind
    {
        Hp = 0,
        Attack = 1,
        Defense = 2,
        SpAttack = 3,
        SpDefense = 4,
        Speed = 5,
        // Battle-only stages, never present in the dataset.
        Accuracy = 6,
        Evasion = 7,
    }

    public static class StatKinds
    {
        /// <summary>Stats that exist in the exported species dataset.</summary>
        public const int BaseCount = 6;
    }

    public enum MoveCategory
    {
        Physical = 0,
        Special = 1,
        Status = 2,
    }

    /// <summary>Non-volatile status. At most one may be active on a creature.</summary>
    public enum StatusCondition
    {
        None = 0,
        Burn = 1,
        Freeze = 2,
        Paralysis = 3,
        Poison = 4,
        BadPoison = 5,
        Sleep = 6,
        Fainted = 7,
    }

    [Flags]
    public enum VolatileFlags
    {
        None = 0,
        Confused = 1 << 0,
        Flinched = 1 << 1,
        Charging = 1 << 2,
        Recharging = 1 << 3,
        Protected = 1 << 4,
        LeechSeeded = 1 << 5,
        Substitute = 1 << 6,
    }

    public enum Weather
    {
        Clear = 0,
        Rain = 1,
        Sun = 2,
        Sandstorm = 3,
        Hail = 4,
        Fog = 5,
    }

    public enum TimeOfDay
    {
        Dawn = 0,
        Day = 1,
        Dusk = 2,
        Night = 3,
    }

    /// <summary>How effective an attack was, for presentation and commentary.</summary>
    public enum Effectiveness
    {
        Immune = 0,
        NotVeryEffective = 1,
        Neutral = 2,
        SuperEffective = 3,
    }

    public static class EffectivenessRules
    {
        public static Effectiveness Classify(float multiplier)
        {
            if (multiplier <= 0f) return Effectiveness.Immune;
            if (multiplier < 1f) return Effectiveness.NotVeryEffective;
            if (multiplier > 1f) return Effectiveness.SuperEffective;
            return Effectiveness.Neutral;
        }
    }
}
