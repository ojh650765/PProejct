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

        /// <summary>Every species in the slice, in dex order.</summary>
        public static readonly IReadOnlyList<int> All = new[]
        {
            Bulbasaur, Charmander, Squirtle, Pidgey, Rattata, Pikachu,
            Zubat, Oddish, Poliwag, Machop, Geodude, Gastly,
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
                default: return 0.6f;
            }
        }
    }
}
