using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// One authored conversation as it sits on disk. Field-for-field a
    /// <see cref="DialogueSequence"/>, so the file can be read with no hand-written parser:
    /// <see cref="JsonUtility"/> fills <see cref="Lines"/> straight into the same struct the
    /// dialogue UI already renders, and the extra keys the writers keep beside them
    /// ("Note", "ToneName") are ignored rather than having to be stripped.
    /// </summary>
    [Serializable]
    public sealed class DialogueBookEntry
    {
        public string SequenceId;
        public bool PlaysOnlyOnce;
        public DialogueLine[] Lines = Array.Empty<DialogueLine>();
    }

    [Serializable]
    public sealed class DialogueBookFile
    {
        public List<DialogueBookEntry> Sequences = new List<DialogueBookEntry>();
    }

    /// <summary>
    /// Reads <c>Assets/Game/Data/Story/dialogue.json</c> and hands out runtime sequences.
    ///
    /// Authored lines live in a data file rather than in ScriptableObject assets because the
    /// scenes that speak them are authored the same way: an episode names a sequence id, and a
    /// writer who wants to rewrite the opening should not have to open the editor, create an
    /// asset and drag it into a scene to do it.
    ///
    /// Every sequence is built fresh on request and run through a substitution callback on the
    /// way out. That is what makes <c>{PLAYER}</c> work: the token is resolved once, over every
    /// line and every choice label in the sequence, at the moment it is about to be spoken —
    /// substituting only where a caller happened to remember to is how half a conversation ends
    /// up addressing the player as "{PLAYER}".
    /// </summary>
    public sealed class DialogueBook
    {
        private readonly Dictionary<string, DialogueBookEntry> _entries =
            new Dictionary<string, DialogueBookEntry>(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public bool Has(string sequenceId) =>
            !string.IsNullOrEmpty(sequenceId) && _entries.ContainsKey(sequenceId);

        /// <summary>
        /// Reads a book from a path relative to the project root. Never throws and never
        /// returns null: a missing or malformed file yields an empty book, and the caller
        /// degrades to a scene with no words in it rather than to no scene at all.
        /// </summary>
        public static DialogueBook Load(string projectRelativePath)
        {
            var book = new DialogueBook();
            if (string.IsNullOrEmpty(projectRelativePath)) return book;

            var path = Path.Combine(Directory.GetCurrentDirectory(), projectRelativePath);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Dialogue] No dialogue book at {projectRelativePath}. Every " +
                                 "scripted conversation will be skipped with a warning, and the " +
                                 "opening will play as a silent, much shorter thing.");
                return book;
            }

            DialogueBookFile file = null;
            try
            {
                file = JsonUtility.FromJson<DialogueBookFile>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // A trailing comma in a writer's edit must not take the game down with it.
                Debug.LogError($"[Dialogue] {projectRelativePath} could not be parsed: {ex.Message}");
                return book;
            }

            foreach (var entry in file?.Sequences ?? new List<DialogueBookEntry>())
            {
                if (entry == null || string.IsNullOrEmpty(entry.SequenceId)) continue;
                if (entry.Lines == null || entry.Lines.Length == 0)
                {
                    // DialogueRunner.Play refuses an empty sequence, so this would surface much
                    // later as a beat that returned instantly for no visible reason.
                    Debug.LogWarning($"[Dialogue] Sequence '{entry.SequenceId}' has no lines and " +
                                     "was dropped; nothing can play it.");
                    continue;
                }
                book._entries[entry.SequenceId] = entry;
            }

            return book;
        }

        /// <summary>
        /// Builds a playable sequence, or null when the id is not in the book.
        ///
        /// The result is a runtime <see cref="DialogueSequence"/> instance with the token
        /// substitution already applied, and it belongs to the caller — destroy it once the
        /// conversation has ended, or a long session leaks one object per line spoken.
        /// </summary>
        public DialogueSequence Build(string sequenceId, Func<string, string> substitute = null)
        {
            if (!_entries.TryGetValue(sequenceId ?? string.Empty, out var entry)) return null;

            var lines = new DialogueLine[entry.Lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                var line = entry.Lines[i];
                line.Text = Apply(substitute, line.Text);

                if (line.Choices != null && line.Choices.Length > 0)
                {
                    // Copied rather than mutated in place: the array came out of the parsed book
                    // and is shared with every future play of this sequence, so substituting into
                    // it would bake the first player's name into the file's only copy.
                    var choices = new DialogueChoice[line.Choices.Length];
                    for (var c = 0; c < choices.Length; c++)
                    {
                        choices[c] = line.Choices[c];
                        choices[c].Text = Apply(substitute, choices[c].Text);
                    }
                    line.Choices = choices;
                }

                lines[i] = line;
            }

            return DialogueSequence.FromLines(entry.SequenceId, lines);
        }

        private static string Apply(Func<string, string> substitute, string text) =>
            substitute == null || string.IsNullOrEmpty(text) ? text : substitute(text);
    }
}
