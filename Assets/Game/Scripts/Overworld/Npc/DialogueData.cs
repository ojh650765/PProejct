using System;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>Presentation hint for the UI worker. Never affects logic.</summary>
    public enum DialogueTone
    {
        Neutral = 0,
        Friendly = 1,
        Excited = 2,
        Worried = 3,
        Hostile = 4,
        System = 5,
    }

    /// <summary>
    /// One branch offered at the end of a line.
    ///
    /// Branching is by line index rather than by node reference so a sequence is a flat array —
    /// which is what makes it survive <see cref="JsonUtility"/>, inspect cleanly, and be readable
    /// by the UI worker without walking a graph.
    /// </summary>
    [Serializable]
    public struct DialogueChoice
    {
        [Tooltip("Button label.")]
        public string Text;

        [Tooltip("Line index to jump to. -1 ends the sequence.")]
        public int GoToLine;

        [Tooltip("Raised on OverworldEvents when picked, so a choice can unlock a door or start "
                 + "a battle without the dialogue system knowing what any of those are.")]
        public string ResultEventId;
    }

    /// <summary>
    /// One rendered line. This struct is the entire contract between the overworld and the
    /// dialogue UI: the UI worker renders a <see cref="DialogueLine"/> and calls back into
    /// <see cref="DialogueRunner"/>. Nothing in this assembly draws anything.
    /// </summary>
    [Serializable]
    public struct DialogueLine
    {
        [Tooltip("Stable id of the speaker, for portrait and voice lookup.")]
        public string SpeakerId;

        [Tooltip("Name as shown in the name plate.")]
        public string SpeakerName;

        [TextArea(2, 6)]
        public string Text;

        [Tooltip("Portrait key resolved by the UI worker's own art table.")]
        public string PortraitKey;

        public DialogueTone Tone;

        [Tooltip("Seconds before advancing on its own. 0 waits for player input.")]
        public float AutoAdvanceSeconds;

        [Tooltip("Raised on OverworldEvents when this line is shown. Use for one-shot effects.")]
        public string EventId;

        [Tooltip("Choices offered after this line. Empty means a plain advance.")]
        public DialogueChoice[] Choices;

        public bool HasChoices => Choices != null && Choices.Length > 0;
    }

    /// <summary>
    /// An authored conversation.
    ///
    /// Held as a ScriptableObject so writing lines does not require touching a scene, but every
    /// consumer takes the data through <see cref="Lines"/>, so a conversation built in code
    /// (a system message, a generated hint) is equally valid.
    /// </summary>
    [CreateAssetMenu(menuName = "Poké Lab/Overworld/Dialogue Sequence", fileName = "Dialogue")]
    public sealed class DialogueSequence : ScriptableObject
    {
        [Tooltip("Stable id, used by flags to record that this conversation has been seen.")]
        [SerializeField] private string _sequenceId = "dialogue_01";

        [Tooltip("Set once this has played, so an NPC can say something different next time.")]
        [SerializeField] private bool _playsOnlyOnce;

        [SerializeField] private DialogueLine[] _lines = Array.Empty<DialogueLine>();

        public string SequenceId => _sequenceId;
        public bool PlaysOnlyOnce => _playsOnlyOnce;
        public DialogueLine[] Lines => _lines;
        public int LineCount => _lines != null ? _lines.Length : 0;

        /// <summary>Builds a sequence at runtime, for system messages and generated lines.</summary>
        public static DialogueSequence FromLines(string sequenceId, params DialogueLine[] lines)
        {
            var sequence = CreateInstance<DialogueSequence>();
            sequence.name = sequenceId;
            sequence._sequenceId = sequenceId;
            sequence._lines = lines ?? Array.Empty<DialogueLine>();
            sequence.hideFlags = HideFlags.HideAndDontSave;
            return sequence;
        }

        /// <summary>Convenience for a single unattributed line, e.g. a sign.</summary>
        public static DialogueSequence Single(string sequenceId, string speakerName, string text) =>
            FromLines(sequenceId, new DialogueLine
            {
                SpeakerName = speakerName,
                Text = text,
                Tone = DialogueTone.Neutral,
            });

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_lines == null) return;
            for (var i = 0; i < _lines.Length; i++)
            {
                if (_lines[i].Choices == null) continue;
                for (var c = 0; c < _lines[i].Choices.Length; c++)
                {
                    // An out-of-range jump silently ends the conversation at runtime, which is very
                    // hard to spot in a long sequence. Catch it at author time instead.
                    var target = _lines[i].Choices[c].GoToLine;
                    if (target >= _lines.Length)
                        Debug.LogWarning($"[DialogueSequence] '{name}' line {i} choice {c} jumps to "
                                         + $"line {target}, past the end ({_lines.Length}).", this);
                }
            }
        }
#endif
    }
}
