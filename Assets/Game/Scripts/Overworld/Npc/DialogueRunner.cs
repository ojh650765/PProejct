using System;
using UnityEngine;
using UnityEngine.Events;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    [Serializable] public sealed class DialogueLineEvent : UnityEvent<DialogueLine> { }
    [Serializable] public sealed class DialogueChoicesEvent : UnityEvent<DialogueChoice[]> { }

    /// <summary>
    /// Steps through a <see cref="DialogueSequence"/> and publishes the current line.
    ///
    /// This is the whole overworld side of dialogue. It renders nothing: it raises
    /// <see cref="LinePresented"/> with a plain data struct and waits for the UI worker to call
    /// <see cref="Advance"/> or <see cref="Choose"/>. The UnityEvents are the coupling-free
    /// hand-off — the UI wires its widget in the inspector and never references this assembly.
    ///
    /// While a sequence is running the flow mode is pushed to <see cref="GameMode.Dialogue"/> and
    /// player input is frozen, so no other system needs to know a conversation is happening.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DialogueRunner : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private OverworldInputReader _input;
        [SerializeField] private PlayerLocomotion _player;
        [SerializeField] private PlayerInteractor _interactor;

        [Header("Behaviour")]
        [Tooltip("Advance on the interact button as well as via the UI's own call. Keeps dialogue "
                 + "working before the UI worker's widget exists.")]
        [SerializeField] private bool _advanceOnInteract = true;
        [Tooltip("Seconds after a line appears before the interact button can advance it. Stops the "
                 + "press that opened the conversation from also skipping its first line.")]
        [SerializeField] private float _inputLockoutSeconds = 0.25f;
        [Tooltip("Pushes GameMode.Dialogue while running, so encounters and autosave stand down.")]
        [SerializeField] private bool _pushesDialogueMode = true;

        [Header("Output")]
        [SerializeField] private DialogueLineEvent _linePresented = new DialogueLineEvent();
        [SerializeField] private DialogueChoicesEvent _choicesOffered = new DialogueChoicesEvent();
        [SerializeField] private UnityEvent _sequenceEnded = new UnityEvent();

        private DialogueSequence _sequence;
        private int _lineIndex = -1;
        private float _lineTimer;
        private float _lockoutTimer;
        private bool _awaitingChoice;
        private bool _pushedMode;
        private Action<string> _completion;
        // The state to put the player back into. Restoring the prior values rather than forcing
        // "unfrozen" matters: dialogue is often nested inside a trainer challenge or a healing
        // sequence that froze the player first, and forcing control back would hand it over
        // mid-cutscene.
        private bool _restoreInputEnabled = true;
        private bool _restoreMotionFrozen;

        private static DialogueRunner _instance;
        public static DialogueRunner Instance => _instance;

        public bool IsPlaying => _sequence != null;

        /// <summary>The GameObject that started the conversation, so the UI can anchor a bubble to it.</summary>
        public GameObject CurrentSpeaker { get; private set; }

        /// <summary>The line currently on screen. Only meaningful while <see cref="IsPlaying"/>.</summary>
        public DialogueLine CurrentLine { get; private set; }

        /// <summary>
        /// Index of <see cref="CurrentLine"/> within the playing sequence, or -1 when idle.
        ///
        /// Exposed because the line's <em>text</em> is not an identity. EpisodeRunner's stall
        /// watchdog used to detect progress by comparing the text on screen against the text it
        /// last saw, and two consecutive authored lines that read the same — "……" beats, a
        /// repeated shout — therefore looked like one line nobody was advancing: the watchdog's
        /// timer kept accumulating across the boundary and it force-advanced a conversation that
        /// was moving perfectly well. The index changes on every advance, including onto an
        /// identical line, so it is the thing progress actually is.
        /// </summary>
        public int CurrentLineIndex => _sequence != null ? _lineIndex : -1;

        /// <summary>
        /// Which sequence is playing, or null.
        ///
        /// The box needs it to stage a scene. A drawn room belongs to a *moment*, not to a
        /// person: Linden speaks in his laboratory during the opening and on the lake bank an
        /// act later, and keying the backdrop on the speaker put the laboratory behind him
        /// while he stood at the water.
        /// </summary>
        public string CurrentSequenceId => _sequence != null ? _sequence.SequenceId : null;

        /// <summary>Raised with each line. C# event for code listeners; the UnityEvent is for the inspector.</summary>
        public event Action<DialogueLine> LinePresented;

        /// <summary>Raised with the sequence id when the conversation finishes.</summary>
        public event Action<string> SequenceEnded;

        /// <summary>
        /// First one wins, and any later arrival removes itself.
        ///
        /// Without the guard an additive scene load leaves two runners alive and points
        /// <see cref="Instance"/> at whichever woke last, while the presenter that draws lines
        /// is still subscribed to the first. The conversation then runs correctly and invisibly:
        /// the player is frozen, the lines advance, and nothing is ever on screen. Keeping the
        /// original rather than the newcomer is what makes that outcome impossible — everything
        /// already holding a reference keeps working.
        /// </summary>
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[Dialogue] A second DialogueRunner arrived on '{name}'. The " +
                                 "first one is still in charge and this copy has been removed; " +
                                 "two of them means half the game talks to the wrong one.", this);
                Destroy(this);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Starts a conversation. <paramref name="onComplete"/> receives the sequence id, which is
        /// how a trainer knows its intro finished and it may start the battle.
        ///
        /// False means one of two things, and callers have to tell them apart. With
        /// <see cref="IsPlaying"/> false the sequence was empty and there was nothing to say;
        /// with it true this runner is busy, and the only correct response is to wait for it
        /// and ask again. Reading the second as "skip this line" is how a scripted beat gets
        /// dropped — permanently, because the scene that owned it has already moved on — and
        /// it is the reason this refusal is not a silent no.
        /// </summary>
        public bool Play(DialogueSequence sequence, GameObject speaker, Action<string> onComplete = null)
        {
            if (sequence == null || sequence.LineCount == 0) return false;
            if (IsPlaying) return false;

            _sequence = sequence;
            CurrentSpeaker = speaker;
            _completion = onComplete;
            _lineIndex = -1;
            _awaitingChoice = false;

            if (_player != null)
            {
                _restoreMotionFrozen = _player.IsMotionFrozen;
                _player.SetMotionFrozen(true);
            }
            if (_input != null)
            {
                _restoreInputEnabled = _input.InputEnabled;
                _input.InputEnabled = false;
            }
            if (_interactor != null) _interactor.ClearPrompt();

            if (_pushesDialogueMode && ServiceHub.TryGet<IGameFlow>(out var flow))
            {
                flow.PushMode(GameMode.Dialogue);
                _pushedMode = true;
            }

            AdvanceToLine(0);
            return true;
        }

        private void Update()
        {
            if (!IsPlaying) return;

            if (_lockoutTimer > 0f) _lockoutTimer -= Time.deltaTime;
            if (_awaitingChoice) return;

            // Only when nobody is there to press the button.
            //
            // Ten lines in the book carry an AutoAdvanceSeconds of three to four seconds, and
            // this used to honour it unconditionally — so the opening read itself aloud and
            // moved on while the player was still on the first sentence, with the box's own
            // 자동 toggle sitting there switched off. A timer that overrides the reader is not
            // an authored pace, it is a line the player never got to finish.
            //
            // Kept for cutscenes, where the player genuinely has no control and a conversation
            // that never advanced would be a soft lock.
            if (CurrentLine.AutoAdvanceSeconds > 0f && !_advanceOnInteract)
            {
                _lineTimer += Time.deltaTime;
                if (_lineTimer >= CurrentLine.AutoAdvanceSeconds)
                {
                    Advance();
                    return;
                }
            }

            // Read the raw action even though input is disabled for movement: the reader's gate
            // stops the player walking, and dialogue still needs the button.
            if (_advanceOnInteract && _lockoutTimer <= 0f && WasAdvancePressed()) Advance();
        }

        /// <summary>
        /// True on the frame the player asked for the next line.
        ///
        /// AdvancePressed, not SubmitPressed: the latter reads the Interact action, which is
        /// bound to E — so the key that starts a conversation was also the key that skipped
        /// through it, and a single held press could open a line and dismiss it. This is the
        /// space bar, which nothing in the world uses.
        ///
        /// Never while somebody is typing. The name prompt sits over this box, and the space
        /// in a two-word name would otherwise both enter a space and advance the question
        /// asking for it.
        /// </summary>
        private bool WasAdvancePressed() =>
            _input != null && _input.AdvancePressed && !PokeLab.Core.TextEntry.IsTyping;

        /// <summary>Moves to the next line, or ends the sequence. Called by the UI's continue button.</summary>
        public void Advance()
        {
            if (!IsPlaying || _awaitingChoice) return;
            AdvanceToLine(_lineIndex + 1);
        }

        /// <summary>Picks a branch. Called by the UI when the player clicks a choice.</summary>
        public void Choose(int choiceIndex)
        {
            if (!IsPlaying || !_awaitingChoice) return;
            var choices = CurrentLine.Choices;
            if (choices == null || choiceIndex < 0 || choiceIndex >= choices.Length) return;

            var choice = choices[choiceIndex];
            _awaitingChoice = false;

            if (!string.IsNullOrEmpty(choice.ResultEventId))
                OverworldEvents.RaiseDialogueSignal(choice.ResultEventId);

            if (choice.GoToLine < 0) End();
            else AdvanceToLine(choice.GoToLine);
        }

        private void AdvanceToLine(int index)
        {
            if (_sequence == null) return;
            if (index < 0 || index >= _sequence.LineCount)
            {
                End();
                return;
            }

            _lineIndex = index;
            CurrentLine = _sequence.Lines[index];
            _lineTimer = 0f;
            _lockoutTimer = _inputLockoutSeconds;

            if (!string.IsNullOrEmpty(CurrentLine.EventId))
                OverworldEvents.RaiseDialogueSignal(CurrentLine.EventId);

            _linePresented.Invoke(CurrentLine);
            LinePresented?.Invoke(CurrentLine);

            if (CurrentLine.HasChoices)
            {
                _awaitingChoice = true;
                _choicesOffered.Invoke(CurrentLine.Choices);
            }
        }

        /// <summary>Ends the conversation immediately, restoring control.</summary>
        public void End()
        {
            if (!IsPlaying) return;

            var sequenceId = _sequence.SequenceId;
            var completion = _completion;

            _sequence = null;
            CurrentSpeaker = null;
            _completion = null;
            _lineIndex = -1;
            _awaitingChoice = false;
            CurrentLine = default;

            if (_pushedMode && ServiceHub.TryGet<IGameFlow>(out var flow))
            {
                flow.PopMode();
                _pushedMode = false;
            }

            if (_player != null) _player.SetMotionFrozen(_restoreMotionFrozen);
            if (_input != null) _input.InputEnabled = _restoreInputEnabled;

            _sequenceEnded.Invoke();
            SequenceEnded?.Invoke(sequenceId);
            // Invoked last so a completion handler that starts a battle sees a fully idle runner.
            completion?.Invoke(sequenceId);
        }
    }
}
