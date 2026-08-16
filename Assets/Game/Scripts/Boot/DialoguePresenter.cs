using PokeLab.Overworld;
using PokeLab.Overworld.People;
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

        /// <summary>
        /// Builds the canvas the view needs and the view inside it.
        ///
        /// DialogueView.BuildRuntime starts with <c>transform as RectTransform</c> and
        /// returns the moment that is null — so a plain GameObject with the component on it
        /// builds nothing at all, silently, and the box never appears. A capture caught it:
        /// the opening faded to black and ran its whole prologue with control taken and no
        /// text on screen.
        ///
        /// The sorting order is above the screen transition's, because the transition covers
        /// the screen with a quad on the camera's near plane and the prologue is *meant* to
        /// be read over that black. Underneath it, the dialogue would be hidden by exactly
        /// the effect it is supposed to accompany.
        /// </summary>
        private static DialogueView BuildView()
        {
            var canvasGo = new GameObject("DialogueCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(canvas, DialogueSortingOrder);

            var viewGo = new GameObject("DialogueView", typeof(RectTransform));
            viewGo.transform.SetParent(canvasGo.transform, false);
            return viewGo.AddComponent<DialogueView>();
        }

        /// <summary>Above the screen wipe, so text drawn over a fade is visible.</summary>
        private const int DialogueSortingOrder = 400;

        private void OnLine(DialogueLine line)
        {
            // DialogueLine is a struct, so there is no null to test — an unset line arrives
            // as a default one with no text, and drawing that is an empty box the player
            // has to dismiss.
            if (_view == null || string.IsNullOrEmpty(line.Text)) return;

            // The runner owns advancing — it is what the interact button is wired to, and it
            // is what knows whether the sequence has more lines. The view is told what to
            // draw and nothing else, so a click on the box and a press of the button take
            // the same path rather than two that can disagree.
            if (line.Choices != null && line.Choices.Length > 0)
            {
                var labels = new string[line.Choices.Length];
                for (var i = 0; i < labels.Length; i++) labels[i] = line.Choices[i].Text;

                _view.ShowChoices(line.SpeakerName, line.Text, labels,
                    index => _runner.Choose(index), PortraitFor(line), line.SpeakerSubtitle);
                return;
            }

            _view.Show(line.SpeakerName, line.Text, PortraitFor(line),
                () => _runner.Advance(), line.SpeakerSubtitle);
        }

        private readonly System.Collections.Generic.Dictionary<string, Sprite> _portraits =
            new System.Collections.Generic.Dictionary<string, Sprite>();

        /// <summary>
        /// The speaker's face, cut out of the sheet they walk around in.
        ///
        /// No portrait art exists — the frame the box reserves is 116x172 and nothing has
        /// ever been drawn for it, so the opening was a name and a voice with an empty plate
        /// beside it. Their own front-idle cell is real drawn art of the right person, and a
        /// 32-pixel face scaled up in a pixel game reads as a portrait rather than as a
        /// placeholder. It is also guaranteed to match whoever is actually standing in the
        /// world, which a separately drawn portrait would not be.
        ///
        /// Cached per key: this is called once per line, and building a Sprite allocates.
        /// </summary>
        private Sprite PortraitFor(DialogueLine line)
        {
            var key = ResolvePersonKey(line);
            if (string.IsNullOrEmpty(key)) return null;
            if (_portraits.TryGetValue(key, out var cached)) return cached;

            Sprite portrait = null;
            var entry = PersonSpriteLibrary.Shared.Find(key);
            var clip = entry?.frontIdle;
            if (clip != null && clip.IsValid)
            {
                var texture = PersonSpriteLibrary.Shared.Texture(clip.texture);
                if (texture != null)
                {
                    var frame = clip.sequence[0];
                    var cellW = texture.width / Mathf.Max(1, clip.columns);
                    var cellH = texture.height / Mathf.Max(1, clip.rows);
                    var column = frame % clip.columns;
                    var row = frame / clip.columns;

                    // Row 0 is the top of the sheet; Sprite rects count up from the bottom.
                    var rect = new Rect(column * cellW,
                                        texture.height - (row + 1) * cellH,
                                        cellW, cellH);
                    portrait = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), cellH);
                    portrait.name = "~Portrait_" + key;
                }
            }

            _portraits[key] = portrait;
            return portrait;
        }

        /// <summary>
        /// Turns a speaker id into the key the sprite library uses.
        ///
        /// The dialogue names speakers as npc_professor_01 — a role and an instance — while
        /// the art is keyed on the role alone. PortraitKey wins when the book sets one,
        /// which is the escape hatch for a speaker whose face is not their own sprite.
        /// </summary>
        private static string ResolvePersonKey(DialogueLine line)
        {
            if (!string.IsNullOrEmpty(line.PortraitKey)) return line.PortraitKey;

            var id = line.SpeakerId;
            if (string.IsNullOrEmpty(id)) return null;

            if (id.StartsWith("npc_", System.StringComparison.Ordinal)) id = id.Substring(4);
            var tail = id.LastIndexOf('_');
            if (tail > 0 && int.TryParse(id.Substring(tail + 1), out _)) id = id.Substring(0, tail);
            return id;
        }

        private void OnEnded(string sequenceId)
        {
            if (_view != null) _view.Close();
        }
    }
}
