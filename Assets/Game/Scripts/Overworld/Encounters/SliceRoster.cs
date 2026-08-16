using System.Collections.Generic;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The twelve species the vertical slice ships with, as fixed by the design brief.
    ///
    /// Ids are KaggleIDs — the primary key
    /// <see cref="PokeLab.Core.SpeciesData.Id"/> uses and the prediction model is indexed by — so
    /// they can be handed straight to <see cref="PokeLab.Core.EncounterRequest.WildSpeciesId"/>
    /// without a lookup.
    ///
    /// This list is the single source of truth for the roster. The built-in encounter tables draw
    /// from it, so adding a species here is the only edit needed to widen the slice.
    /// </summary>
    public static class SliceRoster
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

        // Water. Reachable only by riding a Pokémon across it, which is what makes them a
        // separate roster rather than more of the same: nothing here can be met on foot, so
        // the first surf is a new set of species rather than a new way to reach the old ones.
        // Every one of these has a sprite -- checked against sprite_manifest.json, because a
        // species in a table with no art is an encounter that stages a blank.
        public const int Psyduck = 60;
        public const int Slowpoke = 86;
        public const int Krabby = 107;
        public const int Horsea = 126;
        public const int Goldeen = 128;
        public const int Staryu = 130;
        public const int Magikarp = 140;

        /// <summary>Every species in the slice, in dex order.</summary>
        public static readonly IReadOnlyList<int> All = new[]
        {
            Bulbasaur, Charmander, Squirtle, Pidgey, Rattata, Pikachu,
            Zubat, Oddish, Poliwag, Machop, Geodude, Gastly,
            Psyduck, Slowpoke, Krabby, Horsea, Goldeen, Staryu, Magikarp,
        };

        /// <summary>
        /// Species only ever met on the water. Kept as its own list so the surf tables can be
        /// checked against it rather than restating the ids.
        ///
        /// Gyarados is deliberately absent. It has no sprite in the manifest, and a table
        /// entry with no art stages a battle against a blank -- it is authored as a scripted
        /// river event instead, where a one-off presentation is affordable.
        /// </summary>
        public static readonly IReadOnlyList<int> Aquatic = new[]
        {
            Psyduck, Poliwag, Slowpoke, Krabby, Horsea, Goldeen, Staryu, Magikarp,
        };

        /// <summary>The three starters, offered by the lab and never found in the wild.</summary>
        public static readonly IReadOnlyList<int> Starters = new[] { Bulbasaur, Charmander, Squirtle };

        public static bool IsInSlice(int speciesId)
        {
            for (var i = 0; i < All.Count; i++)
                if (All[i] == speciesId) return true;
            return false;
        }

        /// <summary>
        /// Fallback display name, used only when <see cref="PokeLab.Core.ISpeciesRegistry"/> has
        /// not been registered yet. The registry is authoritative once it exists — this exists so
        /// a half-integrated build shows "Pidgey" rather than "Species 21".
        /// </summary>
        public static string FallbackName(int speciesId)
        {
            switch (speciesId)
            {
                case Bulbasaur: return "Bulbasaur";
                case Charmander: return "Charmander";
                case Squirtle: return "Squirtle";
                case Pidgey: return "Pidgey";
                case Rattata: return "Rattata";
                case Pikachu: return "Pikachu";
                case Zubat: return "Zubat";
                case Oddish: return "Oddish";
                case Poliwag: return "Poliwag";
                case Machop: return "Machop";
                case Geodude: return "Geodude";
                case Gastly: return "Gastly";
                case Psyduck: return "Psyduck";
                case Slowpoke: return "Slowpoke";
                case Krabby: return "Krabby";
                case Horsea: return "Horsea";
                case Goldeen: return "Goldeen";
                case Staryu: return "Staryu";
                case Magikarp: return "Magikarp";
                default: return "Species " + speciesId;
            }
        }

        /// <summary>
        /// Approximate shoulder height in metres, used to scale the roaming creature's collider
        /// and detection radius before <see cref="PokeLab.Core.ICreatureArtRegistry"/> is
        /// registered. The registry overrides this the moment it exists.
        /// </summary>
        public static float FallbackHeight(int speciesId)
        {
            switch (speciesId)
            {
                case Bulbasaur: return 0.7f;
                case Charmander: return 0.6f;
                case Squirtle: return 0.5f;
                case Pidgey: return 0.3f;
                case Rattata: return 0.3f;
                case Pikachu: return 0.4f;
                case Zubat: return 0.8f;
                case Oddish: return 0.5f;
                case Poliwag: return 0.6f;
                case Machop: return 0.8f;
                case Geodude: return 0.4f;
                case Gastly: return 1.3f;
                case Psyduck: return 0.8f;
                case Slowpoke: return 1.2f;
                case Krabby: return 0.4f;
                case Horsea: return 0.4f;
                case Goldeen: return 0.6f;
                case Staryu: return 0.8f;
                case Magikarp: return 0.9f;
                default: return 0.6f;
            }
        }
    }
}
