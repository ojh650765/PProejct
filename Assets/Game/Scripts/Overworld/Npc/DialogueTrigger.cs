using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>How a <see cref="DialogueTrigger"/> fires.</summary>
    public enum DialogueTriggerMode
    {
        /// <summary>Player presses interact while in range. Signs, notice boards, item balls.</summary>
        Interact = 0,
        /// <summary>Fires the moment the player enters the volume. Story beats and warnings.</summary>
        Enter = 1,
    }

    /// <summary>
    /// Attaches a conversation to a sign, a doorway or a story beat without needing an NPC.
    ///
    /// The once-only case is persisted as a profile flag rather than as a local bool, because a
    /// local bool resets on a reload and the player would walk into the same cutscene twice.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DialogueTrigger : MonoBehaviour, IInteractable
    {
        [Header("Content")]
        [SerializeField] private DialogueSequence _sequence;
        [Tooltip("String table key, not the text. Signs and notice boards are the surface a " +
                 "player reads first, so the verb on them has to be in the game's language.")]
        [SerializeField] private string _promptKey = "sign.read";

        [Header("Trigger")]
        [SerializeField] private DialogueTriggerMode _mode = DialogueTriggerMode.Interact;
        [Tooltip("Fires only once per save. Persisted as a flag so a reload does not replay it.")]
        [SerializeField] private bool _oncePerSave;
        [Tooltip("Only fires when this flag is set. Empty means no gate.")]
        [SerializeField] private string _requiredFlag = "";
        [Tooltip("Set when the sequence completes. Use to unlock the next beat.")]
        [SerializeField] private string _setsFlagOnComplete = "";

        [Header("Wiring")]
        [SerializeField] private DialogueRunner _dialogueRunner;

        private bool _firedThisSession;

        public string InteractionPrompt => Loc.Get(_promptKey);

        public bool CanInteract(GameObject instigator) =>
            _mode == DialogueTriggerMode.Interact && _sequence != null && IsAvailable();

        private void Start()
        {
            if (_dialogueRunner == null) _dialogueRunner = DialogueRunner.Instance;
        }

        private bool IsAvailable()
        {
            if (_firedThisSession && _oncePerSave) return false;
            if (_dialogueRunner != null && _dialogueRunner.IsPlaying) return false;

            var profile = ResolveProfile();
            if (profile == null) return true;

            if (!string.IsNullOrEmpty(_requiredFlag) && !profile.GetFlagBool(_requiredFlag)) return false;
            if (_oncePerSave && profile.GetFlagBool(FiredFlagKey)) return false;
            return true;
        }

        private string FiredFlagKey =>
            "dialogue_fired_" + (_sequence != null ? _sequence.SequenceId : name);

        private static PlayerProfile ResolveProfile() =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) ? profile as PlayerProfile : null;

        public void Interact(GameObject instigator) => Fire();

        private void OnTriggerEnter(Collider other)
        {
            if (_mode != DialogueTriggerMode.Enter) return;
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;
            Fire();
        }

        private void Fire()
        {
            if (_sequence == null || _dialogueRunner == null || !IsAvailable()) return;

            // Marked before playing, not after: an interrupted cutscene must not become repeatable.
            _firedThisSession = true;
            var profile = ResolveProfile();
            if (_oncePerSave) profile?.SetFlagBool(FiredFlagKey, true);

            _dialogueRunner.Play(_sequence, gameObject, OnComplete);
        }

        private void OnComplete(string sequenceId)
        {
            if (string.IsNullOrEmpty(_setsFlagOnComplete)) return;
            ResolveProfile()?.SetFlagBool(_setsFlagOnComplete, true);
        }
    }
}
