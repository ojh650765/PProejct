using PokeLab.Overworld;
using PokeLab.UI;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Puts the starter choice on screen and takes the answer.
    ///
    /// <see cref="StarterSelection"/> holds the options and waits for someone to call
    /// <c>Choose</c>; <see cref="EpisodeRunner"/>'s ChooseStarter beat waits for
    /// <c>Chosen</c> to be raised. Nothing was ever going to raise it — the presentation
    /// side had no implementation at all — so the opening reached the most important moment
    /// in the game and hung there until a timeout fired and picked the first ball on the
    /// player's behalf.
    ///
    /// It is drawn as a dialogue with choices rather than as its own screen. The choice is
    /// three options with a line of description each, which is exactly what
    /// <see cref="DialogueView.ShowChoices"/> already does well, and it keeps the moment in
    /// the same voice as the conversation that leads into it. A bespoke starter screen with
    /// three rotating models is a good thing to build later; it is not what stands between
    /// this and a playable opening.
    ///
    /// Lives in PokeLab.Boot for the usual reason: the selection is Overworld, the view is
    /// UI, and those two assemblies cannot see each other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StarterPresenter : MonoBehaviour
    {
        [SerializeField] private StarterSelection _selection;
        [SerializeField] private DialogueView _view;

        [Tooltip("Who is offering. Shown on the name plate.")]
        [SerializeField] private string _speaker = "Linden";

        [TextArea(2, 4)]
        [SerializeField] private string _prompt =
            "Three of them. Not one is a better choice than the others — only a different one. Take your time.";

        private bool _offering;

        private void Awake()
        {
            if (_selection == null) _selection = FindFirstObjectByType<StarterSelection>();
            if (_view == null) _view = FindFirstObjectByType<DialogueView>();
        }

        private void Update()
        {
            if (_offering || _selection == null || _view == null) return;
            if (_selection.HasChosen) return;
            if (!ShouldOffer()) return;

            Offer();
        }

        /// <summary>
        /// Whether the runner is currently waiting on a choice.
        ///
        /// Polled rather than driven by an event because StarterSelection raises
        /// <c>Chosen</c> — the answer — and has no signal for "I am being asked". Adding one
        /// would mean widening a type another worker owns while they are in it.
        /// </summary>
        private bool ShouldOffer()
        {
            var runner = FindFirstObjectByType<EpisodeRunner>();
            return runner != null && runner.IsPlaying && runner.IsAwaitingStarter;
        }

        private void Offer()
        {
            _offering = true;

            var options = _selection.Options;
            if (options == null || options.Count == 0)
            {
                Debug.LogError("[Starter] No starters are authored, so there is nothing to " +
                               "offer. The opening will hand the player whatever the runner " +
                               "falls back to.", this);
                return;
            }

            var labels = new string[options.Count];
            for (var i = 0; i < labels.Length; i++) labels[i] = options[i].DisplayName;

            _view.ShowChoices(_speaker, _prompt, labels, index =>
            {
                // Chosen raises through StarterSelection, which is what the runner is
                // waiting on — the view is not told the answer twice.
                _selection.Choose(index);
                _view.Close();
                _offering = false;
            });
        }
    }
}
