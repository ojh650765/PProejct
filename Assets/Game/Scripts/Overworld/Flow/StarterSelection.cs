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

        /// <summary>
        /// The balls on the desk, with their copy in the player's language.
        ///
        /// Localised here rather than at Awake because neither piece is available that early:
        /// <see cref="ISpeciesRegistry"/> is registered during boot, and the option list itself
        /// may come from a scene rather than from <see cref="DefaultOptions"/>. The desk is
        /// opened exactly once, minutes after boot, and this getter is what opens it — so the
        /// lookup happens at the only moment both halves are certain to exist.
        /// </summary>
        public IReadOnlyList<StarterOption> Options
        {
            get
            {
                Localise();
                return _options;
            }
        }

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

        /// <summary>
        /// Rewrites the option copy in the current language, in place.
        ///
        /// The name comes from the species table rather than from a translated string, because
        /// that table is the one place a species is named and inventing a second spelling of
        /// 이상해씨 here is how the ball on the desk and the creature in the party end up
        /// disagreeing. The blurb comes from the string table under its species id.
        ///
        /// Both are skipped when the lookup fails: <see cref="Loc.Get"/> returns the key itself
        /// for an unknown id, and a ball labelled <c>starter.blurb.1</c> is worse than one still
        /// labelled in English.
        /// </summary>
        private void Localise()
        {
            if (_options == null || _language == Loc.Language) return;
            _language = Loc.Language;

            ServiceHub.TryGet<ISpeciesRegistry>(out var registry);
            for (var i = 0; i < _options.Count; i++)
            {
                var option = _options[i];
                if (option == null) continue;

                if (registry != null && registry.TryGet(option.SpeciesId, out var species))
                    option.DisplayName = species.DisplayName;

                var key = "starter.blurb." + option.SpeciesId;
                var blurb = Loc.Get(key);
                if (!string.Equals(blurb, key, StringComparison.Ordinal)) option.Blurb = blurb;
            }
        }

        // Which language the copy above is currently written in. Nullable rather than a bool so
        // the first read localises even when the game is already in the language it booted in.
        private Language? _language;

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
                var progression = SnapshotProgression(concrete);
                concrete.InitialiseNewGame(trainerName, Choice.SpeciesId, _level, _seed);
                foreach (var flag in progression) concrete.SetFlag(flag.Key, flag.Value);
                return true;
            }

            Debug.LogError($"[Starter] IPlayerProfile is a {profile.GetType().Name}, which " +
                           "does not expose InitialiseNewGame. The starter was chosen but " +
                           "not granted.", this);
            return false;
        }

        /// <summary>Prefix of the flags that describe how far the story has got.</summary>
        private const string ProgressionPrefix = "story.";

        /// <summary>
        /// Copies out the progression flags so they can be put back after the profile is reset.
        ///
        /// <c>InitialiseNewGame</c> clears every flag, which is right for the thing it is named
        /// after — a brand new save — and wrong for when it is actually called. It runs from the
        /// middle of <c>opening_departure</c>, by which point <c>story.opening_seen</c> is set
        /// and the opening's own no-replay guard, the rival encounter's gate and Bram's dialogue
        /// state all depend on it. Wiping it there put the save back to before the prologue while
        /// the player stood at the gate holding a Pokémon: the opening was due again on the next
        /// load, and nothing that gates on having seen it would open.
        ///
        /// Only <c>story.</c> is carried across. Trainer defeats, pickups and everything else
        /// keyed off a flag genuinely should start empty on a new game, and preserving the whole
        /// table would make the reset meaningless.
        /// </summary>
        private static List<KeyValuePair<string, string>> SnapshotProgression(PlayerProfile profile)
        {
            var kept = new List<KeyValuePair<string, string>>();
            foreach (var flag in profile.Flags)
                if (flag.Key != null && flag.Key.StartsWith(ProgressionPrefix, StringComparison.Ordinal))
                    kept.Add(flag);
            return kept;
        }
    }
}
