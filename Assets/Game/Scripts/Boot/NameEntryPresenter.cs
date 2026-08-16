using PokeLab.Overworld;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// The name prompt.
    ///
    /// <see cref="EpisodeRunner"/>'s AskName beat used to take the script's default and warn
    /// that nothing in the project offered text entry. This is that entry: a field over the
    /// dialogue box, prefilled with the authored name so confirming immediately is a valid
    /// answer rather than a dead end.
    ///
    /// It reuses the dialogue canvas rather than building a screen of its own. The question
    /// is asked by the professor in the middle of a conversation, and lifting the player out
    /// to a separate modal would break the one continuous scene the opening is composed as.
    ///
    /// Lives in PokeLab.Boot for the usual reason: the runner is Overworld, the widgets are
    /// UI, and those two assemblies cannot see each other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NameEntryPresenter : MonoBehaviour
    {
        private const int SortingOrder = 420;   // above the dialogue box, which is 400

        [Tooltip("Longest name accepted. The plate has to hold it beside a subtitle.")]
        [SerializeField] private int _maxLength = 12;

        private EpisodeRunner _runner;
        private TMP_InputField _field;
        private TextMeshProUGUI _placeholder;
        private GameObject _root;
        private GameObject _scrim;
        private bool _open;
        private bool _submitted;

        private void Awake() => _runner = FindFirstObjectByType<EpisodeRunner>();

        private void Update()
        {
            if (_runner == null) _runner = FindFirstObjectByType<EpisodeRunner>();
            if (_runner == null) return;

            // Not reopened while an answer is in flight: the runner clears IsAwaitingName a
            // frame or two after SubmitName, and reopening in that gap is what let the blank
            // second confirm through.
            if (_runner.IsAwaitingName && !_open && !_submitted) Open();
            else if (!_runner.IsAwaitingName)
            {
                if (_open) Close();
                _submitted = false;
            }

            if (!_open || _field == null) return;

            // Enter confirms. Checked here rather than through onSubmit because the input
            // module's submit action is also the dialogue's advance button, and letting the
            // same press do both would answer the prompt and skip the line behind it.
            //
            // Read through Keyboard.current, not UnityEngine.Input: this project is set to the
            // Input System package alone, and the old static class throws on first use there.
            // It threw here every frame the prompt was open, which is why the name prompt
            // could not be typed into or dismissed.
            // Re-asserted every frame it is open. Anything that takes selection — a stray
            // click on the scrim, a canvas rebuilt underneath — would otherwise leave the box
            // on screen and deaf, with no sign that it had stopped listening.
            if (!_field.isFocused)
            {
                EventSystem.current?.SetSelectedGameObject(_field.gameObject);
                _field.ActivateInputField();
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                Confirm();
        }

        private void Open()
        {
            _open = true;
            _submitted = false;
            // Claimed before the field is shown, so the very first space typed cannot also
            // advance the line that asked the question.
            PokeLab.Core.TextEntry.Begin();
            Build();

            // Empty, with the fallback shown as a placeholder rather than typed into the box.
            //
            // It used to be prefilled with "Ren". If the field failed to take keyboard focus —
            // which it can, since this opens over a screen that has just been manipulating the
            // cursor lock — every keystroke went nowhere, the box still read "Ren", and
            // pressing Enter confirmed a name the player never chose while looking like it had
            // accepted theirs. An empty box cannot lie about that: nothing typed means nothing
            // shown, and confirming it still yields the default deliberately.
            _field.text = string.Empty;
            _field.characterLimit = Mathf.Max(1, _maxLength);
            if (_placeholder != null) _placeholder.text = _runner.DefaultName ?? string.Empty;

            _root.SetActive(true);
            if (_scrim != null) _scrim.SetActive(true);

            // Selected and caret-placed, so the player can type immediately instead of
            // discovering they have to click the box first.
            EventSystem.current?.SetSelectedGameObject(_field.gameObject);
            _field.Select();
            _field.ActivateInputField();
            _field.caretPosition = 0;
        }

        private void Close()
        {
            _open = false;
            PokeLab.Core.TextEntry.End();
            if (_root != null) _root.SetActive(false);
            if (_scrim != null) _scrim.SetActive(false);
        }

        private void Confirm()
        {
            // First answer wins, and there is always a second.
            //
            // Enter reaches this twice in one frame: TMP's own onSubmit fires from the event
            // system, and Update sees the same wasPressedThisFrame afterwards. By then Confirm
            // has already closed the prompt — and because the runner has not ticked yet,
            // IsAwaitingName is still true, so Update reopens it, blanks the field and confirms
            // an empty string over the name the player actually typed. The prompt then reported
            // the default. Latched rather than reordered, because either caller may be first
            // depending on where the key is read.
            if (_submitted) return;
            _submitted = true;

            var typed = _field != null ? _field.text : null;
            Close();
            _runner.SubmitName(typed);
        }

        /// <summary>
        /// Builds the field once, on first use.
        ///
        /// Built rather than authored for the same reason the dialogue box is: the scene is
        /// regenerated constantly, and a serialized widget would have to survive every
        /// rebuild in three scenes.
        /// </summary>
        private void Build()
        {
            if (_root != null) return;

            var canvasGo = new GameObject("NameEntryCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(canvas, SortingOrder);

            // A card, centred, rather than a bare grey rectangle sitting on the dialogue box.
            // This is the one moment in the game that asks the player for something of their
            // own, and it read as a debug field: no prompt, no caret, no confirm, nothing to
            // say what would happen when they pressed a key.
            _root = new GameObject("NameEntry", typeof(RectTransform));
            _root.transform.SetParent(canvasGo.transform, false);
            var root = (RectTransform)_root.transform;
            UiBuilder.Anchor(root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 320f));

            // Dims the scene behind without hiding it — the professor is mid-sentence and
            // should still be visible asking the question.
            var scrim = UiBuilder.Image("Scrim", canvasGo.transform, null,
                new Color(0.02f, 0.03f, 0.06f, 0.55f), Image.Type.Simple);
            UiBuilder.Stretch(scrim.rectTransform);
            scrim.rectTransform.SetAsFirstSibling();

            var card = UiBuilder.Panel("Card", root, UiPalette.Surface, 24);
            UiBuilder.Stretch(card.rectTransform);

            var edge = UiBuilder.OutlinedPanel("Edge", root, new Color(0f, 0f, 0f, 0f), 24, 3);
            UiBuilder.Stretch(edge.rectTransform);
            edge.color = UiPalette.ScannerCyan;

            var prompt = UiBuilder.Text("Prompt", root,
                PokeLab.Core.Loc.Pick("What is your name?", "이름을 알려주게."),
                UiTextRole.Heading, UiPalette.TextPrimary, TextAlignmentOptions.Center);
            UiBuilder.Anchor(prompt.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(-56f, 52f));

            // The field itself: a sunken well, so it reads as somewhere to type rather than
            // as another label.
            var well = UiBuilder.Rect("Well", root, false);
            UiBuilder.Anchor(well, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(600f, 88f));
            var wellFill = UiBuilder.Panel("Fill", well, UiPalette.SurfaceSunken, 14);
            UiBuilder.Stretch(wellFill.rectTransform);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(well, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 42f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = UiPalette.TextPrimary;
            UiBuilder.Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(24f, 8f);
            text.rectTransform.offsetMax = new Vector2(-24f, -8f);

            var hint = UiBuilder.Text("Hint", root,
                PokeLab.Core.Loc.Pick("Enter to confirm", "Enter로 결정"),
                UiTextRole.Secondary, UiPalette.TextMuted, TextAlignmentOptions.Center);
            UiBuilder.Anchor(hint.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(-56f, 36f));

            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGo.transform.SetParent(well, false);
            _placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            _placeholder.fontSize = 42f;
            _placeholder.alignment = TextAlignmentOptions.Center;
            _placeholder.color = UiPalette.TextMuted;
            UiBuilder.Stretch(_placeholder.rectTransform);
            _placeholder.rectTransform.offsetMin = new Vector2(24f, 8f);
            _placeholder.rectTransform.offsetMax = new Vector2(-24f, -8f);

            _field = well.gameObject.AddComponent<TMP_InputField>();
            _field.textViewport = well;
            _field.textComponent = text;
            _field.placeholder = _placeholder;
            _field.targetGraphic = wellFill;
            _field.caretWidth = 3;
            _field.customCaretColor = true;
            _field.caretColor = UiPalette.ScannerCyan;
            _field.selectionColor = new Color(UiPalette.ScannerCyan.r, UiPalette.ScannerCyan.g,
                                              UiPalette.ScannerCyan.b, 0.35f);
            _field.lineType = TMP_InputField.LineType.SingleLine;
            _field.onSubmit.AddListener(_ => Confirm());

            _root.SetActive(false);
            scrim.gameObject.SetActive(false);
            _scrim = scrim.gameObject;
        }
    }
}
