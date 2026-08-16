using PokeLab.Overworld;
using PokeLab.UI;
using TMPro;
using UnityEngine;
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
        private GameObject _root;
        private bool _open;

        private void Awake() => _runner = FindFirstObjectByType<EpisodeRunner>();

        private void Update()
        {
            if (_runner == null) _runner = FindFirstObjectByType<EpisodeRunner>();
            if (_runner == null) return;

            if (_runner.IsAwaitingName && !_open) Open();
            else if (!_runner.IsAwaitingName && _open) Close();

            if (!_open || _field == null) return;

            // Enter confirms. Checked here rather than through onSubmit because the input
            // module's submit action is also the dialogue's advance button, and letting the
            // same press do both would answer the prompt and skip the line behind it.
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                Confirm();
        }

        private void Open()
        {
            _open = true;
            Build();

            _field.text = _runner.DefaultName ?? string.Empty;
            _field.characterLimit = Mathf.Max(1, _maxLength);
            _root.SetActive(true);

            // Selected and caret-placed, so the player can type immediately instead of
            // discovering they have to click the box first.
            _field.Select();
            _field.ActivateInputField();
            _field.caretPosition = _field.text.Length;
        }

        private void Close()
        {
            _open = false;
            if (_root != null) _root.SetActive(false);
        }

        private void Confirm()
        {
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

            _root = new GameObject("NameEntry", typeof(RectTransform));
            _root.transform.SetParent(canvasGo.transform, false);
            var root = (RectTransform)_root.transform;
            root.anchorMin = new Vector2(0.5f, 0f);
            root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.anchoredPosition = new Vector2(0f, 210f);
            root.sizeDelta = new Vector2(520f, 64f);

            var background = _root.AddComponent<Image>();
            background.color = new Color(0.03f, 0.04f, 0.07f, 0.94f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(root, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 30f;
            text.alignment = TextAlignmentOptions.Left;
            text.color = Color.white;
            UiBuilder.Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(22f, 6f);
            text.rectTransform.offsetMax = new Vector2(-22f, -6f);

            _field = _root.AddComponent<TMP_InputField>();
            _field.textViewport = root;
            _field.textComponent = text;
            _field.lineType = TMP_InputField.LineType.SingleLine;
            _field.onSubmit.AddListener(_ => Confirm());

            _root.SetActive(false);
        }
    }
}
