using PokeLab.Overworld;
using PokeLab.UI;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Puts conversations on screen.
    ///
    /// <see cref="DialogueRunner"/> sequences lines, waits for input and raises an event per
    /// line; <see cref="DialogueView"/> draws a box, types the text out and offers choices.
    /// Both were finished. Neither had ever been told the other existed, so every
    /// conversation in the game ran to completion invisibly — the runner advancing through
    /// its lines, the player standing frozen watching nothing happen, and no error anywhere
    /// because from each side's point of view it was working.
    ///
    /// They could not simply reference each other: PokeLab.UI and PokeLab.Overworld are
    /// separate assemblies and neither is allowed to see the other, which is what kept them
    /// apart. This lives in PokeLab.Boot, which references both, and is the only place that
    /// knows the two exist together. Neither side gains a dependency.
    ///
    /// The view is created here rather than placed in the scene by hand. It builds its own
    /// hierarchy on Awake, so a scene that carries a serialized copy would have to be
    /// re-saved every time the layout changed, in every scene, and the copies would drift.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class DialoguePresenter : MonoBehaviour
    {
        [Tooltip("Left empty, one is built on Awake. Assign only to use a hand-authored box.")]
        [SerializeField] private DialogueView _view;

        [SerializeField] private DialogueRunner _runner;

        private void Awake()
        {
            if (_view == null) _view = BuildView();
        }

        private void OnEnable()
        {
            if (_runner == null) _runner = DialogueRunner.Instance;
            if (_runner == null) _runner = FindFirstObjectByType<DialogueRunner>();

            if (_runner == null)
            {
                Debug.LogWarning("[Dialogue] No DialogueRunner in this scene, so nothing will " +
                                 "ever ask for a line to be drawn.", this);
                return;
            }

            _runner.LinePresented += OnLine;
            _runner.SequenceEnded += OnEnded;
        }

        private void OnDisable()
        {
            if (_runner == null) return;
            _runner.LinePresented -= OnLine;
            _runner.SequenceEnded -= OnEnded;
        }

        private static DialogueView BuildView()
        {
            var go = new GameObject("DialogueView");
            return go.AddComponent<DialogueView>();
        }

        private void OnLine(DialogueLine line)
        {
            if (_view == null || line == null) return;

            // The runner owns advancing — it is what the interact button is wired to, and it
            // is what knows whether the sequence has more lines. The view is told what to
            // draw and nothing else, so a click on the box and a press of the button take
            // the same path rather than two that can disagree.
            if (line.Choices != null && line.Choices.Length > 0)
            {
                var labels = new string[line.Choices.Length];
                for (var i = 0; i < labels.Length; i++) labels[i] = line.Choices[i].Text;

                _view.ShowChoices(line.SpeakerName, line.Text, labels,
                    index => _runner.Choose(index), null, line.SpeakerSubtitle);
                return;
            }

            _view.Show(line.SpeakerName, line.Text, null,
                () => _runner.Advance(), line.SpeakerSubtitle);
        }

        private void OnEnded(string sequenceId)
        {
            if (_view != null) _view.Close();
        }
    }
}
