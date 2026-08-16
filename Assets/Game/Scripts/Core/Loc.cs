using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Core
{
    public enum Language
    {
        English = 0,
        Korean = 1,
    }

    /// <summary>
    /// The one place that decides which language a string is in.
    ///
    /// The data already carried both — every species and every move ships
    /// <c>NameEn</c> and <c>NameKo</c> — but nothing chose between them, so the choice was
    /// made independently in two places and they disagreed: <c>SpeciesData.DisplayName</c>
    /// preferred Korean while <c>PokeLabStrings.SpeciesName</c> preferred English, which
    /// mixes languages inside one sentence. Both now ask here.
    ///
    /// Everything the player reads that is *not* a name comes from a string table keyed by
    /// id, so a line can be rewritten in one language without touching the other and a
    /// missing translation is visible rather than silently English.
    ///
    /// On Korean: the entries are written as a Korean game would write them, not as English
    /// rendered into Korean. That means dropping the connective padding a literal
    /// translation accumulates — "그것은", "~하는 것이다", "매우" — and letting the sentence
    /// end where Korean ends it. A translated-sounding line is worse than an English one,
    /// because it reads as a machine's work rather than as the game's voice.
    /// </summary>
    public static class Loc
    {
        public const string TablePath = "Assets/Game/Data/Localisation/strings.json";

        /// <summary>Name the table is looked up under when placed in a Resources folder.</summary>
        public const string TableResourceName = "strings";

        private static Dictionary<string, Entry> _entries;
        private static readonly HashSet<string> _missing = new HashSet<string>();

        private static Language _language = Language.Korean;

        /// <summary>
        /// Defaults to Korean because that is the language this game is being made in. A
        /// build for anyone else changes this one value.
        /// </summary>
        public static Language Language
        {
            get => _language;
            set
            {
                if (_language == value) return;
                _language = value;
                LanguageChanged?.Invoke(value);
            }
        }

        public static event Action<Language> LanguageChanged;

        /// <summary>Picks between two already-loaded strings, falling back when one is blank.</summary>
        public static string Pick(string english, string korean)
        {
            if (_language == Language.Korean)
                return string.IsNullOrEmpty(korean) ? english : korean;
            return string.IsNullOrEmpty(english) ? korean : english;
        }

        /// <summary>
        /// A line from the table.
        ///
        /// An unknown key returns the key itself rather than an empty string. A blank label
        /// is a bug nobody notices in a screenshot; a control that reads
        /// <c>ui.bag.close</c> is one that gets fixed.
        /// </summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            EnsureLoaded();

            if (_entries.TryGetValue(key, out var entry))
                return Pick(entry.en, entry.ko);

            if (_missing.Add(key))
                Debug.LogWarning($"[Loc] No string for '{key}'.");
            return key;
        }

        public static string Get(string key, params object[] args)
        {
            var format = Get(key);
            try { return string.Format(format, args); }
            catch (FormatException)
            {
                // A translator's copy of a line can lose a placeholder. Better the raw line
                // than an exception thrown from the middle of a conversation.
                Debug.LogWarning($"[Loc] '{key}' has a placeholder its text does not match.");
                return format;
            }
        }

        /// <summary>Drops the cache so an edited table is picked up without a domain reload.</summary>
        public static void Reset()
        {
            _entries = null;
            _missing.Clear();
        }

        private static void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

            var asset = Resources.Load<TextAsset>(TableResourceName);
            var json = asset != null ? asset.text : ReadFromDisk();
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning($"[Loc] No string table found. Every key will render as " +
                                 "itself, which is ugly and obvious — which is the point.");
                return;
            }

            Table table;
            try { table = JsonUtility.FromJson<Table>(json); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Loc] The string table would not parse: {e.Message}");
                return;
            }

            foreach (var entry in table?.entries ?? Array.Empty<Entry>())
            {
                if (entry == null || string.IsNullOrEmpty(entry.key)) continue;
                _entries[entry.key] = entry;
            }
        }

        /// <summary>
        /// Editor fallback, for the table before it has been moved under Resources. Reading
        /// from disk is not available in a player build, which is why Resources is tried
        /// first rather than as the fallback.
        /// </summary>
        private static string ReadFromDisk()
        {
#if UNITY_EDITOR
            var path = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), TablePath);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
#else
            return null;
#endif
        }

        [Serializable] private sealed class Table { public Entry[] entries; }

        [Serializable]
        private sealed class Entry
        {
            public string key;
            public string en;
            public string ko;
        }
    }
}
