using System;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// One of the balls on the professor's desk.
    ///
    /// The species is a *game* id, not a national dex number. The two spaces are not the
    /// same map — only two of the fifty-three species with sprites have game id equal to
    /// dex — and mixing them is the single mistake in this project that produces a
    /// plausible-looking wrong result rather than an error. The roamer tables were
    /// written in dex and stocked a cave with a seal.
    /// </summary>
    [Serializable]
    public sealed class StarterOption
    {
        public int SpeciesId;
        public string DisplayName;
        /// <summary>Shown under the name. Type line, or the professor's one-line opinion.</summary>
        public string Blurb;
        /// <summary>What the rival takes if the player takes this one — the type that beats it.</summary>
        public int RivalCounterSpeciesId;
    }

    /// <summary>
    /// The starter choice, as a piece of state the opening episode drives.
    ///
    /// Deliberately not a UI class and not a MonoBehaviour that owns a canvas. The
    /// episode runner asks it to open, it raises <see cref="Chosen"/> when the player
    /// picks, and whatever is presenting — the dialogue view's choice list today, a
    /// bespoke three-ball screen later — subscribes. That keeps the moment the game
    /// hands over its first Pokémon independent of how it happens to be drawn this week.
    /// </summary>
    public sealed class StarterSelection : MonoBehaviour
    {
        [Tooltip("The balls on the desk, in the order they are presented.")]
        [SerializeField] private List<StarterOption> _options = new List<StarterOption>();

        [Tooltip("Level the starter is handed over at. Five, as it has been since 1996.")]
        [Min(1)][SerializeField] private int _level = 5;

        [Tooltip("Seed for the starter's own IVs and nature, so a given choice is the same " +
                 "creature on every playthrough of the same save seed.")]
        [SerializeField] private int _seed = 424242;

        /// <summary>Raised with the chosen option once the player commits.</summary>
        public event Action<StarterOption> Chosen;

        public IReadOnlyList<StarterOption> Options => _options;
        public int Level => _level;
        public int Seed => _seed;
        public bool HasChosen { get; private set; }
        public StarterOption Choice { get; private set; }

        private void Reset() => _options = DefaultOptions();

        private void Awake()
        {
            if (_options == null || _options.Count == 0) _options = DefaultOptions();
        }

        /// <summary>
        /// The three from <see cref="SliceRoster.Starters"/>, in the order the desk shows
        /// them, each paired with the one the rival will take. That pairing is the whole
        /// point of the scene: the rival's choice is a consequence of yours, which is why
        /// the first battle is always uphill.
        /// </summary>
        private static List<StarterOption> DefaultOptions() => new List<StarterOption>
        {
            new StarterOption
            {
                SpeciesId = SliceRoster.Bulbasaur, DisplayName = "Bulbasaur",
                Blurb = "Grass · Poison — patient, and hard to knock down.",
                RivalCounterSpeciesId = SliceRoster.Charmander,
            },
            new StarterOption
            {
                SpeciesId = SliceRoster.Charmander, DisplayName = "Charmander",
                Blurb = "Fire — fragile early, frightening later.",
                RivalCounterSpeciesId = SliceRoster.Squirtle,
            },
            new StarterOption
            {
                SpeciesId = SliceRoster.Squirtle, DisplayName = "Squirtle",
                Blurb = "Water — steady, and forgiving of mistakes.",
                RivalCounterSpeciesId = SliceRoster.Bulbasaur,
            },
        };

        /// <summary>Commits a choice. Ignored once one has been made — the desk is a one-time thing.</summary>
        public bool Choose(int index)
        {
            if (HasChosen) return false;
            if (_options == null || index < 0 || index >= _options.Count) return false;

            Choice = _options[index];
            HasChosen = true;
            Chosen?.Invoke(Choice);
            return true;
        }

        public bool Choose(StarterOption option) =>
            option != null && Choose(_options.IndexOf(option));

        /// <summary>
        /// Hands the chosen starter to the profile and starts the save.
        ///
        /// Kept here rather than in the episode data because it is the one irreversible
        /// step in the opening: after this the player has a party and the game has begun.
        /// </summary>
        public bool Commit(string trainerName)
        {
            if (!HasChosen) return false;
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile == null)
            {
                Debug.LogError("[Starter] No IPlayerProfile is registered, so the chosen " +
                               "starter has nowhere to go. The opening cannot complete.", this);
                return false;
            }

            if (profile is PlayerProfile concrete)
            {
                concrete.InitialiseNewGame(trainerName, Choice.SpeciesId, _level, _seed);
                return true;
            }

            Debug.LogError($"[Starter] IPlayerProfile is a {profile.GetType().Name}, which " +
                           "does not expose InitialiseNewGame. The starter was chosen but " +
                           "not granted.", this);
            return false;
        }
    }
}
