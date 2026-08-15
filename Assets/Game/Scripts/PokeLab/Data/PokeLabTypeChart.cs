using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Intelligence.Data
{
    /// <summary>
    /// The 18x18 chart from pokemon_battle_app/domain.py plus the two ability tables,
    /// loaded from typechart.json.
    ///
    /// Two ability tables exist because they answer different questions. The immunity
    /// table is what the *model* was trained against and drives the scanner's "may be
    /// nullified" warning; the pokerogue modifier table is richer (0 / 0.5 / 1.25) and is
    /// what the battle layer should ask for a real damage multiplier. They are kept apart
    /// so a future pokerogue refresh cannot silently move the model's numbers.
    /// </summary>
    public sealed class PokeLabTypeChart : ITypeChart
    {
        private readonly float[] _multipliers; // row-major, attack * 18 + defend
        private readonly Dictionary<string, int> _immunityMask;
        private readonly Dictionary<string, float[]> _abilityModifiers;

        private PokeLabTypeChart(float[] multipliers, Dictionary<string, int> immunityMask,
                                 Dictionary<string, float[]> abilityModifiers)
        {
            _multipliers = multipliers;
            _immunityMask = immunityMask;
            _abilityModifiers = abilityModifiers;
        }

        public static PokeLabTypeChart Load(string json)
        {
            var root = JsonValue.Parse(json);

            var flat = root["Multipliers"].AsFloatArray();
            var expected = ElementTypes.Count * ElementTypes.Count;
            if (flat.Length != expected)
                throw new InvalidOperationException($"typechart.json: expected {expected} multipliers, got {flat.Length}");

            // One bitmask per ability instead of a set per type: the immunity check runs
            // inside the feature builder, which is on the hot path.
            var immunity = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in root["ImmunityAbilities"].AsArray())
            {
                var ability = entry["Ability"].AsString();
                var type = entry["AttackType"].AsInt(-1);
                if (string.IsNullOrEmpty(ability) || type < 0 || type >= ElementTypes.Count) continue;
                immunity.TryGetValue(ability, out var mask);
                immunity[ability] = mask | (1 << type);
            }

            var modifiers = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var entry in root["AbilityModifiers"].AsArray())
            {
                var ability = entry["Ability"].AsString();
                var type = entry["AttackType"].AsInt(-1);
                if (string.IsNullOrEmpty(ability) || type < 0 || type >= ElementTypes.Count) continue;
                if (!modifiers.TryGetValue(ability, out var row))
                {
                    row = new float[ElementTypes.Count];
                    for (var i = 0; i < row.Length; i++) row[i] = 1f;
                    modifiers[ability] = row;
                }
                row[type] = entry["Multiplier"].AsFloat(1f);
            }

            return new PokeLabTypeChart(flat, immunity, modifiers);
        }

        public float Multiplier(ElementType attack, ElementType defend)
        {
            if (attack == ElementType.None || defend == ElementType.None) return 1f;
            return _multipliers[(int)attack * ElementTypes.Count + (int)defend];
        }

        public float Multiplier(ElementType attack, ElementType defend1, ElementType defend2)
        {
            return Multiplier(attack, defend1) * Multiplier(attack, defend2);
        }

        public float AbilityModifier(string abilityIdentifier, ElementType attackType)
        {
            if (string.IsNullOrEmpty(abilityIdentifier) || attackType == ElementType.None) return 1f;
            return _abilityModifiers.TryGetValue(abilityIdentifier, out var row) ? row[(int)attackType] : 1f;
        }

        /// <summary>
        /// True when the ability appears in domain.py's IMMUNITY_ABILITIES for this attack
        /// type. This is the table the trained model saw, so the feature builder must use
        /// it rather than the broader pokerogue modifiers.
        /// </summary>
        public bool GrantsImmunity(string abilityIdentifier, ElementType attackType)
        {
            if (string.IsNullOrEmpty(abilityIdentifier) || attackType == ElementType.None) return false;
            return _immunityMask.TryGetValue(abilityIdentifier, out var mask) && (mask & (1 << (int)attackType)) != 0;
        }

        /// <summary>Any of the creature's possible abilities blocking this attack type.</summary>
        public bool AnyGrantsImmunity(string[] abilities, ElementType attackType)
        {
            if (abilities == null) return false;
            for (var i = 0; i < abilities.Length; i++)
                if (GrantsImmunity(abilities[i], attackType)) return true;
            return false;
        }
    }
}
