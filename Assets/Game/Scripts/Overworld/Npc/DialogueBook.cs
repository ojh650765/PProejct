using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// One branch as it sits on disk: a <see cref="DialogueChoice"/> carrying both languages.
    /// </summary>
    [Serializable]
    public sealed class DialogueBookChoice
    {
        public string Text;
        public string TextKo;
        public int GoToLine;
        public string ResultEventId;
    }

    /// <summary>
    /// One line as it sits on disk: a <see cref="DialogueLine"/> plus its Korean half.
    ///
    /// The two texts are paired here rather than in <see cref="DialogueLine"/> because that
    /// struct is the contract the runner and the dialogue UI already speak, and widening it
    /// would push the language decision out to every one of them — the exact split
    /// <see cref="Loc"/> exists to prevent. A book line knows both; a
    /// <see cref="DialogueLine"/> is always in one language, and <see cref="ToLine"/> is the
    /// only place that chooses.
    /// </summary>
    [Serializable]
    public sealed class DialogueBookLine
    {
        public string SpeakerId;
        public string SpeakerName;
        public string SpeakerNameKo;
        public string SpeakerSubtitle;
        public string SpeakerSubtitleKo;
        public string PortraitKey;
        public DialogueTone Tone;
        public float AutoAdvanceSeconds;
        public string EventId;
        public string Text;
        public string TextKo;
        public DialogueBookChoice[] Choices = Array.Empty<DialogueBookChoice>();

        /// <summary>
        /// Collapses the pair to the single-language line the UI renders.
        ///
        /// A fresh <see cref="DialogueChoice"/> array is built every call. The book's own array
        /// is shared with every future play of this sequence, so writing a substituted label
        /// back into it would bake the first player's name into the file's only copy.
        /// </summary>
        public DialogueLine ToLine(Func<string, string> substitute)
        {
            var line = new DialogueLine
            {
                SpeakerId = SpeakerId,
                SpeakerName = Loc.Pick(SpeakerName, SpeakerNameKo),
                SpeakerSubtitle = Loc.Pick(SpeakerSubtitle, SpeakerSubtitleKo),
                PortraitKey = PortraitKey,
                Tone = Tone,
                AutoAdvanceSeconds = AutoAdvanceSeconds,
                EventId = EventId,
                Text = Apply(substitute, Loc.Pick(Text, TextKo)),
                Choices = Array.Empty<DialogueChoice>(),
            };

            if (Choices == null || Choices.Length == 0) return line;

            var choices = new DialogueChoice[Choices.Length];
            for (var c = 0; c < choices.Length; c++)
            {
                var source = Choices[c];
                if (source == null) continue;
                choices[c] = new DialogueChoice
                {
                    Text = Apply(substitute, Loc.Pick(source.Text, source.TextKo)),
                    GoToLine = source.GoToLine,
                    ResultEventId = source.ResultEventId,
                };
            }
            line.Choices = choices;
            return line;
        }

        private static string Apply(Func<string, string> substitute, string text) =>
            substitute == null || string.IsNullOrEmpty(text) ? text : substitute(text);
    }

    /// <summary>
    /// One authored conversation as it sits on disk. <see cref="JsonUtility"/> fills
    /// <see cref="Lines"/> directly, and the extra keys the writers keep beside them
    /// ("Note", "ToneName") are ignored rather than having to be stripped.
    /// </summary>
    [Serializable]
    public sealed class DialogueBookEntry
    {
        public string SequenceId;
        public bool PlaysOnlyOnce;
        public DialogueBookLine[] Lines = Array.Empty<DialogueBookLine>();
    }

    /// <summary>
    /// Which sequence a townsperson speaks, and what moves them on to the next one.
    ///
    /// Authored beside the lines rather than on the NPC because the town is generated: a
    /// sequence dragged onto an NPC in the scene does not survive the next rebuild from
    /// slice_layout.json, and the observable result was that not one NPC in the level could be
    /// talked to at all.
    /// </summary>
    [Serializable]
    public sealed class DialogueBookBinding
    {
        public string NpcId;
        public string DefaultSequenceId;
        public NpcDialogueState[] States = Array.Empty<NpcDialogueState>();
    }

    [Serializable]
    public sealed class DialogueBookFile
    {
        public List<DialogueBookEntry> Sequences = new List<DialogueBookEntry>();
        public List<DialogueBookBinding> NpcBindings = new List<DialogueBookBinding>();
    }

    /// <summary>
    /// Reads <c>Assets/Game/Data/Story/dialogue.json</c> and hands out runtime sequences.
    ///
    /// Authored lines live in a data file rather than in ScriptableObject assets because the
    /// scenes that speak them are authored the same way: an episode names a sequence id, and a
    /// writer who wants to rewrite the opening should not have to open the editor, create an
    /// asset and drag it into a scene to do it.
    ///
    /// The book keeps both languages; a built sequence is in one. Every line and every choice
    /// label is resolved through <see cref="Loc.Pick"/> on the way out, which is why a
    /// half-translated book degrades to English on the untranslated lines instead of showing a
    /// blank box, and why switching <see cref="Loc.Language"/> before the next conversation is
    /// all that a language change requires.
    ///
    /// Every sequence is built fresh on request and run through a substitution callback on the
    /// way out. That is what makes <c>{PLAYER}</c> work: the token is resolved once, over every
    /// line and every choice label in the sequence, at the moment it is about to be spoken —
    /// substituting only where a caller happened to remember to is how half a conversation ends
    /// up addressing the player as "{PLAYER}".
    /// </summary>
    public sealed class DialogueBook
    {
        /// <summary>Where the book lives. The one path every consumer defaults to.</summary>
        public const string DefaultPath = "Assets/Game/Data/Story/dialogue.json";

        private readonly Dictionary<string, DialogueBookEntry> _entries =
            new Dictionary<string, DialogueBookEntry>(StringComparer.Ordinal);

        private readonly Dictionary<string, DialogueBookBinding> _bindings =
            new Dictionary<string, DialogueBookBinding>(StringComparer.Ordinal);

        private static DialogueBook _shared;

        /// <summary>
        /// The book at <see cref="DefaultPath"/>, parsed once for the whole scene.
        ///
        /// Five NPCs each calling <see cref="Load"/> would parse a 50 KB file five times on the
        /// first frame anyone walks near a townsperson, and the result would be five identical
        /// copies of it in memory. Sharing is safe because a book is read-only after load and
        /// <see cref="Build"/> allocates everything it hands out.
        /// </summary>
        public static DialogueBook Shared => _shared ??= Load(DefaultPath);

        /// <summary>Drops the shared copy so an edited book is picked up without a restart.</summary>
        public static void ResetShared() => _shared = null;

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

            foreach (var binding in file?.NpcBindings ?? new List<DialogueBookBinding>())
            {
                if (binding == null || string.IsNullOrEmpty(binding.NpcId)) continue;
                book._bindings[binding.NpcId] = binding;
            }

            return book;
        }

        /// <summary>The line sets bound to an NPC id, or null when nothing is authored for it.</summary>
        public DialogueBookBinding BindingFor(string npcId) =>
            !string.IsNullOrEmpty(npcId) && _bindings.TryGetValue(npcId, out var binding) ? binding : null;

        /// <summary>
        /// Builds a playable sequence, or null when the id is not in the book.
        ///
        /// The result is a runtime <see cref="DialogueSequence"/> instance in the language
        /// <see cref="Loc.Language"/> names, with the token substitution already applied, and it
        /// belongs to the caller — destroy it once the conversation has ended, or a long session
        /// leaks one object per line spoken.
        /// </summary>
        public DialogueSequence Build(string sequenceId, Func<string, string> substitute = null)
        {
            if (!_entries.TryGetValue(sequenceId ?? string.Empty, out var entry)) return null;

            var lines = new DialogueLine[entry.Lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                var source = entry.Lines[i];
                if (source == null) continue;
                lines[i] = source.ToLine(substitute);
            }

            return DialogueSequence.FromLines(entry.SequenceId, lines);
        }
    }
}
