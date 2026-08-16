using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>Bitmask of <see cref="TimeOfDay"/> so an entry can span several phases.</summary>
    [Flags]
    public enum TimeOfDayMask
    {
        None = 0,
        Dawn = 1 << 0,
        Day = 1 << 1,
        Dusk = 1 << 2,
        Night = 1 << 3,
        Daylight = Dawn | Day | Dusk,
        Any = Dawn | Day | Dusk | Night,
    }

    /// <summary>Bitmask of <see cref="Weather"/>.</summary>
    [Flags]
    public enum WeatherMask
    {
        None = 0,
        Clear = 1 << 0,
        Rain = 1 << 1,
        Sun = 1 << 2,
        Sandstorm = 1 << 3,
        Hail = 1 << 4,
        Fog = 1 << 5,
        Any = Clear | Rain | Sun | Sandstorm | Hail | Fog,
    }

    public static class EncounterMasks
    {
        public static TimeOfDayMask ToMask(this TimeOfDay time)
        {
            switch (time)
            {
                case TimeOfDay.Dawn: return TimeOfDayMask.Dawn;
                case TimeOfDay.Day: return TimeOfDayMask.Day;
                case TimeOfDay.Dusk: return TimeOfDayMask.Dusk;
                default: return TimeOfDayMask.Night;
            }
        }

        public static WeatherMask ToMask(this Weather weather)
        {
            switch (weather)
            {
                case Weather.Clear: return WeatherMask.Clear;
                case Weather.Rain: return WeatherMask.Rain;
                case Weather.Sun: return WeatherMask.Sun;
                case Weather.Sandstorm: return WeatherMask.Sandstorm;
                case Weather.Hail: return WeatherMask.Hail;
                default: return WeatherMask.Fog;
            }
        }
    }

    /// <summary>One weighted row of an encounter table.</summary>
    [Serializable]
    public struct EncounterEntry
    {
        [Tooltip("KaggleID, matching SpeciesData.Id. Use the SliceRoster constants.")]
        public int SpeciesId;

        [Min(1)] public int MinLevel;
        [Min(1)] public int MaxLevel;

        [Tooltip("Relative weight. Weights are summed per roll, so they need not total anything.")]
        [Min(0f)] public float Weight;

        [Tooltip("Phases this species appears in. Nocturnal species clear the daylight bits.")]
        public TimeOfDayMask Times;

        [Tooltip("Weather this species appears in. Rain-only species clear everything else.")]
        public WeatherMask Weathers;

        [Tooltip("Weight multiplier applied when the gating conditions are met and it is night. "
                 + "Lets a species be common at night without being absent by day.")]
        [Min(0f)] public float NightBoost;

        [Tooltip("Weight multiplier applied while it is raining. Same reasoning as NightBoost.")]
        [Min(0f)] public float RainBoost;

        /// <summary>Effective weight under the supplied conditions. Zero means "cannot appear".</summary>
        public float EffectiveWeight(TimeOfDay time, Weather weather)
        {
            if ((Times & time.ToMask()) == 0) return 0f;
            if ((Weathers & weather.ToMask()) == 0) return 0f;

            var weight = Weight;
            if (time == TimeOfDay.Night && NightBoost > 0f) weight *= NightBoost;
            if (weather == Weather.Rain && RainBoost > 0f) weight *= RainBoost;
            return weight;
        }
    }

    /// <summary>
    /// A weighted, condition-gated pool of wild species for one zone and traversal state.
    ///
    /// Authored as an asset by the integrator, but never required: <see cref="BuiltInFor"/>
    /// returns a hand-tuned table for each of the slice's four areas, so the world is playable
    /// before a single asset exists and an unassigned field is a soft failure rather than a
    /// silent one.
    /// </summary>
    [CreateAssetMenu(menuName = "Poké Lab/Overworld/Encounter Table", fileName = "EncounterTable")]
    public sealed class EncounterTable : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] private string _tableId = "route_grass";

        [Header("Rates")]
        [Tooltip("Metres of movement through the source needed before an encounter becomes likely. "
                 + "Higher is rarer. Tune this rather than the per-step probability.")]
        [Min(1f)][SerializeField] private float _metresPerEncounter = 26f;

        [Tooltip("Minimum metres before an encounter can fire at all, so you never get jumped on "
                 + "the first footstep after a battle.")]
        [Min(0f)][SerializeField] private float _graceMetres = 6f;

        [Header("Entries")]
        [SerializeField] private List<EncounterEntry> _entries = new List<EncounterEntry>();

        public string TableId => _tableId;
        public float MetresPerEncounter => _metresPerEncounter;
        public float GraceMetres => _graceMetres;
        public IReadOnlyList<EncounterEntry> Entries => _entries;

        /// <summary>
        /// Weighted draw under the supplied conditions.
        ///
        /// The RNG is passed by reference so the caller owns the stream — that is what makes an
        /// encounter reproducible from a world seed plus a step counter rather than from whatever
        /// order systems happened to tick in.
        /// </summary>
        public bool TryRoll(ref DeterministicRandom rng, TimeOfDay time, Weather weather,
                            out int speciesId, out int level)
        {
            speciesId = 0;
            level = 5;
            if (_entries == null || _entries.Count == 0) return false;

            var total = 0f;
            for (var i = 0; i < _entries.Count; i++) total += _entries[i].EffectiveWeight(time, weather);
            if (total <= 0f) return false;

            var roll = rng.NextFloat() * total;
            for (var i = 0; i < _entries.Count; i++)
            {
                var weight = _entries[i].EffectiveWeight(time, weather);
                if (weight <= 0f) continue;
                roll -= weight;
                if (roll > 0f) continue;

                speciesId = _entries[i].SpeciesId;
                var min = Mathf.Max(1, _entries[i].MinLevel);
                var max = Mathf.Max(min, _entries[i].MaxLevel);
                level = rng.RangeInclusive(min, max);
                return true;
            }

            // Floating-point residue can drop through the loop; fall back to the last valid row.
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].EffectiveWeight(time, weather) <= 0f) continue;
                speciesId = _entries[i].SpeciesId;
                level = Mathf.Max(1, _entries[i].MinLevel);
                return true;
            }
            return false;
        }

        /// <summary>Distinct species this table can ever produce, for seeding roamer populations.</summary>
        public List<int> SpeciesUnder(TimeOfDay time, Weather weather)
        {
            var results = new List<int>();
            if (_entries == null) return results;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].EffectiveWeight(time, weather) <= 0f) continue;
                if (!results.Contains(_entries[i].SpeciesId)) results.Add(_entries[i].SpeciesId);
            }
            return results;
        }

        // ---- Built-in slice tables -------------------------------------------------------

        private static EncounterTable _routeGrass, _routeWater, _townGrass, _cave, _lakesideGrass, _lakesideWater;

        /// <summary>
        /// Drops the cached built-in tables. Called on play mode start, because with domain reload
        /// disabled these static references survive into the next session pointing at destroyed
        /// ScriptableObjects.
        /// </summary>
        internal static void ResetBuiltIns()
        {
            _routeGrass = _routeWater = _townGrass = _cave = _lakesideGrass = _lakesideWater = null;
        }

        /// <summary>
        /// Hand-tuned default table for a zone kind. Created in memory, never saved as an asset,
        /// so it cannot conflict with anything the integrator authors — assigning a real table on
        /// the zone shadows this entirely.
        /// </summary>
        public static EncounterTable BuiltInFor(ZoneKind kind, TraversalState traversal)
        {
            if (traversal == TraversalState.Water)
            {
                switch (kind)
                {
                    case ZoneKind.Lakeside: return _lakesideWater ??= BuildLakesideWater();
                    default: return _routeWater ??= BuildRouteWater();
                }
            }

            switch (kind)
            {
                case ZoneKind.Cave: return _cave ??= BuildCave();
                case ZoneKind.Town: return _townGrass ??= BuildTown();
                case ZoneKind.Lakeside: return _lakesideGrass ??= BuildLakesideGrass();
                default: return _routeGrass ??= BuildRouteGrass();
            }
        }

        private static EncounterTable Create(string id, float metresPerEncounter, params EncounterEntry[] entries)
        {
            var table = CreateInstance<EncounterTable>();
            table.name = "BuiltIn_" + id;
            table._tableId = id;
            table._metresPerEncounter = metresPerEncounter;
            table._entries = new List<EncounterEntry>(entries);
            // Runtime-only: leaving it in the scene's object graph would have it saved into
            // whatever asset happens to reference it.
            table.hideFlags = HideFlags.HideAndDontSave;
            return table;
        }

        private static EncounterEntry Entry(int species, int min, int max, float weight,
            TimeOfDayMask times = TimeOfDayMask.Any, WeatherMask weathers = WeatherMask.Any,
            float nightBoost = 0f, float rainBoost = 0f) =>
            new EncounterEntry
            {
                SpeciesId = species, MinLevel = min, MaxLevel = max, Weight = weight,
                Times = times, Weathers = weathers, NightBoost = nightBoost, RainBoost = rainBoost,
            };

        private static EncounterTable BuildRouteGrass() => Create("builtin_route_grass", 26f,
            Entry(SliceRoster.Pidgey, 3, 6, 30f, TimeOfDayMask.Daylight),
            Entry(SliceRoster.Rattata, 3, 6, 28f, TimeOfDayMask.Any, WeatherMask.Any, nightBoost: 1.6f),
            Entry(SliceRoster.Oddish, 4, 7, 18f, TimeOfDayMask.Dusk | TimeOfDayMask.Night | TimeOfDayMask.Dawn, WeatherMask.Any, rainBoost: 1.5f),
            Entry(SliceRoster.Pikachu, 4, 7, 5f, TimeOfDayMask.Any),
            Entry(SliceRoster.Machop, 5, 8, 9f, TimeOfDayMask.Daylight),
            Entry(SliceRoster.Gastly, 5, 8, 10f, TimeOfDayMask.Night, WeatherMask.Any, nightBoost: 1.4f));

        private static EncounterTable BuildTown() => Create("builtin_town_grass", 40f,
            // Town encounters are deliberately thin — the town is for people, not for grinding.
            Entry(SliceRoster.Rattata, 2, 4, 45f),
            Entry(SliceRoster.Pidgey, 2, 4, 45f, TimeOfDayMask.Daylight),
            Entry(SliceRoster.Oddish, 3, 5, 10f, TimeOfDayMask.Dawn | TimeOfDayMask.Dusk));

        private static EncounterTable BuildCave() => Create("builtin_cave", 20f,
            // Underground the clock still matters: the same Zubat colony is denser after dark.
            Entry(SliceRoster.Zubat, 5, 9, 42f, TimeOfDayMask.Any, WeatherMask.Any, nightBoost: 1.5f),
            Entry(SliceRoster.Geodude, 5, 9, 30f),
            Entry(SliceRoster.Machop, 6, 10, 14f),
            Entry(SliceRoster.Gastly, 6, 10, 14f, TimeOfDayMask.Any, WeatherMask.Any, nightBoost: 2.0f));

        private static EncounterTable BuildLakesideGrass() => Create("builtin_lakeside_grass", 30f,
            Entry(SliceRoster.Oddish, 4, 7, 30f, TimeOfDayMask.Any, WeatherMask.Any, rainBoost: 1.8f),
            Entry(SliceRoster.Poliwag, 4, 8, 22f, TimeOfDayMask.Any, WeatherMask.Any, rainBoost: 2.0f),
            Entry(SliceRoster.Pidgey, 3, 6, 24f, TimeOfDayMask.Daylight),
            Entry(SliceRoster.Pikachu, 5, 8, 12f, TimeOfDayMask.Any, WeatherMask.Rain | WeatherMask.Clear | WeatherMask.Fog, rainBoost: 2.2f),
            Entry(SliceRoster.Gastly, 6, 9, 12f, TimeOfDayMask.Night | TimeOfDayMask.Dusk, WeatherMask.Any, nightBoost: 1.5f));

        /// <summary>
        /// The lake. Still water, so a slower, stranger cast than the river's -- the things
        /// that sit on a bottom rather than the things that fight a current.
        /// </summary>
        private static EncounterTable BuildLakesideWater() => Create("builtin_lakeside_water", 22f,
            Entry(SliceRoster.Poliwag, 5, 10, 40f, TimeOfDayMask.Any, WeatherMask.Any, rainBoost: 1.4f),
            Entry(SliceRoster.Magikarp, 5, 12, 22f),
            Entry(SliceRoster.Slowpoke, 8, 13, 14f, TimeOfDayMask.Daylight),
            Entry(SliceRoster.Staryu, 8, 13, 12f, TimeOfDayMask.Night | TimeOfDayMask.Dusk, WeatherMask.Any, nightBoost: 1.8f),
            Entry(SliceRoster.Goldeen, 6, 11, 12f),
            Entry(SliceRoster.Squirtle, 8, 12, 6f),
            Entry(SliceRoster.Pidgey, 4, 7, 24f, TimeOfDayMask.Daylight));

        /// <summary>
        /// The river, met only from the back of a Pokémon.
        ///
        /// Deliberately a different cast from anything walkable, not a reshuffle of it. The
        /// first time the player crosses water is the first time the roster changes, and if
        /// the surface just held more Poliwag then earning the crossing bought nothing. It
        /// also gives the water an identity the grass cannot: Magikarp is the commonest
        /// thing in the river and the least worth catching, which is the joke the series has
        /// told since 1996 and it only lands if the player meets it constantly.
        ///
        /// Denser than the route -- 18 m against 26 -- because open water has nowhere to
        /// hide and the crossing is short.
        /// </summary>
        private static EncounterTable BuildRouteWater() => Create("builtin_route_water", 18f,
            Entry(SliceRoster.Magikarp, 5, 12, 55f),
            Entry(SliceRoster.Goldeen, 6, 11, 16f, TimeOfDayMask.Daylight),
            Entry(SliceRoster.Horsea, 6, 11, 12f, TimeOfDayMask.Any, WeatherMask.Any, rainBoost: 1.4f),
            Entry(SliceRoster.Psyduck, 7, 12, 9f, TimeOfDayMask.Any, WeatherMask.Rain | WeatherMask.Fog, rainBoost: 2.2f),
            Entry(SliceRoster.Krabby, 6, 10, 8f, TimeOfDayMask.Dusk | TimeOfDayMask.Night | TimeOfDayMask.Dawn));

#if UNITY_EDITOR
        /// <summary>Editor-only sanity pass so a malformed table is caught at author time.</summary>
        private void OnValidate()
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.MaxLevel < entry.MinLevel) entry.MaxLevel = entry.MinLevel;
                if (entry.Times == TimeOfDayMask.None) entry.Times = TimeOfDayMask.Any;
                if (entry.Weathers == WeatherMask.None) entry.Weathers = WeatherMask.Any;
                _entries[i] = entry;
            }
        }
#endif
    }
}
