using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// The dialogue box: speaker name, portrait slot, typewriter body, and choice prompts.
    ///
    /// Two rules make a typewriter feel good rather than tedious. First, a press while text
    /// is still revealing completes the line instead of advancing — players learn this in one
    /// press and it removes all frustration with the pace. Second, punctuation gets extra
    /// dwell, so the reveal has the rhythm of speech instead of the rhythm of a printer.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DialogueView : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _box;
        [SerializeField] private TextMeshProUGUI _speaker;
        [SerializeField] private RectTransform _speakerPlate;
        [SerializeField] private Image _portrait;
        [SerializeField] private RectTransform _portraitFrame;
        [SerializeField] private TextMeshProUGUI _body;
        [SerializeField] private Image _advanceCaret;
        [SerializeField] private RectTransform _choiceParent;

        [Header("Tuning")]
        [SerializeField] private float _secondsPerCharacter = 0.022f;
        [Tooltip("Extra dwell after sentence-ending punctuation, in character-times.")]
        [SerializeField] private float _punctuationDwell = 7f;
        [SerializeField] private bool _buildOnAwake = true;

        private readonly List<Button> _choiceButtons = new List<Button>(4);
        private TweenHandle _typing;
        private TweenHandle _caret;
        private Action _onAdvance;
        private Action<int> _onChoice;
        private bool _revealing;
        private bool _built;

        /// <summary>True while the box is on screen.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>True while text is still being revealed.</summary>
        public bool IsRevealing => _revealing;

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            SetOpen(false, true);
        }

        /// <summary>
        /// Shows a line. <paramref name="onAdvance"/> fires when the player presses past a
        /// fully revealed line.
        /// </summary>
        public void Show(string speaker, string body, Sprite portrait = null, Action onAdvance = null)
        {
            _onAdvance = onAdvance;
            _onChoice = null;
            ShowInternal(speaker, body, portrait, null);
        }

        /// <summary>
        /// Shows a line with choices. The choices appear only once the line has finished
        /// revealing, so the player is never asked to decide before they have read the
        /// question.
        /// </summary>
        public void ShowChoices(string speaker, string body, IReadOnlyList<string> choices,
            Action<int> onChoice, Sprite portrait = null)
        {
            _onAdvance = null;
            _onChoice = onChoice;
            ShowInternal(speaker, body, portrait, choices);
        }

        /// <summary>
        /// The single show path. The pending choices are latched *before* the reveal starts,
        /// because with reduced motion the reveal completes synchronously and a completion
        /// handler that ran before the choices were assigned would drop them silently.
        /// </summary>
        private void ShowInternal(string speaker, string body, Sprite portrait, IReadOnlyList<string> choices)
        {
            ClearChoices();

            if (_speakerPlate != null) _speakerPlate.gameObject.SetActive(!string.IsNullOrWhiteSpace(speaker));
            if (_speaker != null) _speaker.SetText(speaker ?? string.Empty);

            if (_portraitFrame != null) _portraitFrame.gameObject.SetActive(portrait != null);
            if (_portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.enabled = portrait != null;
            }

            SetOpen(true);
            _pendingChoices = choices;
            Reveal(body ?? string.Empty);
        }

        private IReadOnlyList<string> _pendingChoices;

        /// <summary>
        /// Player input. Completes the reveal if it is running, otherwise advances.
        /// Route the confirm button here; the view decides what the press means.
        /// </summary>
        public void Advance()
        {
            if (_revealing)
            {
                CompleteReveal();
                return;
            }
            if (_pendingChoices != null) return; // Waiting on a choice, not an advance.
            _onAdvance?.Invoke();
        }

        /// <summary>Hides the box.</summary>
        public void Close()
        {
            ClearChoices();
            SetOpen(false);
        }

        private void Reveal(string text)
        {
            if (_body == null) return;

            _body.SetText(text);
            _body.maxVisibleCharacters = 0;
            _revealing = true;
            SetCaretVisible(false);

            var visible = Mathf.Max(1, _body.GetParsedText().Length);
            var parsed = _body.GetParsedText();

            // Duration is padded for punctuation so the eased walk below lands in the right
            // place; the per-character mapping then reproduces the pauses.
            var pauses = 0;
            for (var i = 0; i < parsed.Length; i++)
            {
                if (parsed[i] == '.' || parsed[i] == '!' || parsed[i] == '?' || parsed[i] == ',') pauses++;
            }
            var duration = (visible + pauses * _punctuationDwell) * _secondsPerCharacter;

            UiTween.Kill(ref _typing);
            _typing = UiTween.Run(duration, t =>
            {
                if (_body == null) return;
                _body.maxVisibleCharacters = CharactersAt(parsed, t, pauses);
            }, Ease.Linear, 0f, true, CompleteReveal);
        }

        /// <summary>
        /// Maps normalised time to a character count, spending extra time on punctuation.
        /// Walking the string is cheap at dialogue lengths and avoids a per-frame allocation.
        /// </summary>
        private int CharactersAt(string parsed, float t, int pauses)
        {
            var totalUnits = parsed.Length + pauses * _punctuationDwell;
            var consumed = t * totalUnits;
            var units = 0f;
            for (var i = 0; i < parsed.Length; i++)
            {
                units += 1f;
                var c = parsed[i];
                if (c == '.' || c == '!' || c == '?' || c == ',') units += _punctuationDwell;
                if (units >= consumed) return i + 1;
            }
            return parsed.Length;
        }

        private void CompleteReveal()
        {
            UiTween.Kill(ref _typing);
            _revealing = false;
            if (_body != null) _body.maxVisibleCharacters = int.MaxValue;

            if (_pendingChoices != null)
            {
                BuildChoices(_pendingChoices);
                SetCaretVisible(false);
            }
            else
            {
                SetCaretVisible(true);
            }
        }

        private void SetCaretVisible(bool visible)
        {
            if (_advanceCaret == null) return;
            UiTween.Kill(ref _caret);
            _advanceCaret.enabled = visible;
            if (!visible) return;

            // A gently bobbing caret. It re-arms from its own completion callback, guarded on
            // MotionEnabled — without that guard a reduced-motion tween would complete on the
            // frame it starts and recurse until the stack gave out.
            _caret = UiTween.Run(0.9f, t =>
            {
                if (_advanceCaret == null) return;
                var bob = Mathf.Sin(t * Mathf.PI * 2f) * 3f;
                _advanceCaret.rectTransform.anchoredPosition = new Vector2(
                    _advanceCaret.rectTransform.anchoredPosition.x, -14f + bob);
                _advanceCaret.color = UiPalette.ScannerCyan.WithAlpha(Mathf.Lerp(0.5f, 1f, Mathf.Abs(Mathf.Sin(t * Mathf.PI))));
            }, Ease.Linear, 0f, true, () =>
            {
                if (_advanceCaret != null && _advanceCaret.enabled && UiTween.MotionEnabled) SetCaretVisible(true);
            });
        }

        private void BuildChoices(IReadOnlyList<string> choices)
        {
            ClearChoices();
            if (choices == null || choices.Count == 0) return;

            for (var i = 0; i < choices.Count; i++)
            {
                var index = i;
                var root = UiBuilder.Rect("Choice_" + i, _choiceParent);
                UiBuilder.Size(root, preferredHeight: 42f, minHeight: 42f, flexibleWidth: 1f);

                var background = UiBuilder.Image("Bg", root, UiSprites.Panel(10), UiPalette.SurfaceRaised,
                    Image.Type.Sliced, true);
                UiBuilder.Stretch(background.rectTransform);

                var label = UiBuilder.Text("Label", root, choices[i], UiTextRole.Body, UiPalette.TextPrimary);
                UiBuilder.Stretch(label.rectTransform);
                label.rectTransform.offsetMin = new Vector2(16f, 0f);
                label.rectTransform.offsetMax = new Vector2(-16f, 0f);

                UiButtonMotion.Attach(root, 10);
                _choiceButtons.Add(UiBuilder.Button("Click", root, background, () => Choose(index)));

                // Stagger the entrance so the option list reads top to bottom.
                var group = UiBuilder.Group(root, 0f, true, true);
                UiTween.Fade(group, 1f, 0.2f, Ease.OutCubic, i * 0.05f);
            }

            _choiceParent?.gameObject.SetActive(true);
        }

        private void Choose(int index)
        {
            var callback = _onChoice;
            ClearChoices();
            callback?.Invoke(index);
        }

        private void ClearChoices()
        {
            _pendingChoices = null;
            _choiceButtons.Clear();
            UiBuilder.ClearChildren(_choiceParent);
            if (_choiceParent != null) _choiceParent.gameObject.SetActive(false);
        }

        private void SetOpen(bool open, bool immediate = false)
        {
            IsOpen = open;
            if (_group == null) return;
            if (open) gameObject.SetActive(true);
            _group.interactable = open;
            _group.blocksRaycasts = open;

            if (immediate)
            {
                _group.alpha = open ? 1f : 0f;
                gameObject.SetActive(open);
                return;
            }

            if (open && _box != null)
            {
                var target = _box.anchoredPosition;
                _box.anchoredPosition = target + new Vector2(0f, -24f);
                UiTween.AnchoredMove(_box, target, 0.28f, Ease.OutCubic);
            }

            UiTween.Fade(_group, open ? 1f : 0f, open ? 0.2f : 0.16f, open ? Ease.OutCubic : Ease.InCubic, 0f,
                () => { if (!open && this != null) gameObject.SetActive(false); });
        }

        /// <summary>Builds the dialogue box, anchored across the bottom of the screen.</summary>
        public void BuildRuntime()
        {
            _built = true;
            var root = transform as RectTransform;
            if (root == null) return;

            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(this, 0f, false, false);

            var safe = UiBuilder.SafeArea(root, 96f, 44f);

            var box = UiBuilder.Rect("Box", safe, false);
            UiBuilder.Anchor(box, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(0f, 190f));
            _box = box;

            UiBuilder.Panel("Shell", box, UiPalette.Surface.WithAlpha(0.97f), 18, true, 26, 0.6f);
            var rim = UiBuilder.Image("Rim", box, UiSprites.Frame(18, 1), Color.white.WithAlpha(0.07f));
            UiBuilder.Stretch(rim.rectTransform);

            // Speaker plate overhangs the top edge — the standard treatment, and it keeps the
            // name out of the body's measure.
            // The plate is anchored, not in a layout group, so a fitter is legitimate here —
            // and it needs a layout group of its own for the fitter to have a size to read.
            var plate = UiBuilder.Rect("SpeakerPlate", box, false);
            UiBuilder.Anchor(plate, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f),
                new Vector2(26f, -6f), new Vector2(240f, 34f));
            UiBuilder.Horizontal(plate, 0f, new RectOffset(14, 14, 0, 0), TextAnchor.MiddleLeft);
            UiBuilder.Fit(plate, ContentSizeFitter.FitMode.PreferredSize, ContentSizeFitter.FitMode.Unconstrained);
            _speakerPlate = plate;

            UiBuilder.Backdrop("Bg", plate, UiSprites.Panel(10), UiPalette.ScannerCyan.WithAlpha(0.18f));

            _speaker = UiBuilder.Text("Name", plate, string.Empty, UiTextRole.Heading, UiPalette.ScannerCyan);
            _speaker.fontSize = 17f;
            _speaker.textWrappingMode = TextWrappingModes.NoWrap;

            var content = UiBuilder.Rect("Content", box);
            UiBuilder.Stretch(content, 22f);
            UiBuilder.Horizontal(content, 16f, null, TextAnchor.UpperLeft, false, true);

            var portraitFrame = UiBuilder.Rect("PortraitFrame", content, false);
            UiBuilder.Size(portraitFrame, preferredWidth: 118f, minWidth: 118f, flexibleHeight: 1f);
            _portraitFrame = portraitFrame;
            var portraitBg = UiBuilder.Image("Bg", portraitFrame, UiSprites.Panel(12), UiPalette.SurfaceSunken);
            UiBuilder.Stretch(portraitBg.rectTransform);
            _portrait = UiBuilder.Image("Image", portraitFrame, null, Color.white, Image.Type.Simple);
            _portrait.preserveAspect = true;
            UiBuilder.Stretch(_portrait.rectTransform, 6f);
            portraitFrame.gameObject.SetActive(false);

            var textColumn = UiBuilder.Rect("TextColumn", content);
            UiBuilder.Size(textColumn, flexibleWidth: 1f, flexibleHeight: 1f);

            _body = UiBuilder.Text("Body", textColumn, string.Empty, UiTextRole.Body, UiPalette.TextPrimary,
                TextAlignmentOptions.TopLeft);
            _body.fontSize = 19f;
            _body.lineSpacing = 12f;
            UiBuilder.Stretch(_body.rectTransform);

            _advanceCaret = UiBuilder.Image("Caret", box, UiSprites.Chevron(20), UiPalette.ScannerCyan,
                Image.Type.Simple);
            _advanceCaret.preserveAspect = true;
            UiBuilder.Anchor(_advanceCaret.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0f), new Vector2(-22f, -14f), new Vector2(14f, 14f));
            // The chevron points up by default; rotate it to point down as an advance cue.
            _advanceCaret.rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
            _advanceCaret.enabled = false;

            var choices = UiBuilder.Rect("Choices", safe, false);
            UiBuilder.Anchor(choices, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 202f), new Vector2(380f, 0f));
            UiBuilder.Vertical(choices, 8f, null, TextAnchor.LowerRight);
            UiBuilder.Fit(choices);
            _choiceParent = choices;
            choices.gameObject.SetActive(false);

            UiBuilder.EnsureEventSystem();
        }
    }
}
