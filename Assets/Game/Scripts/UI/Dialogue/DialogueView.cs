using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// The conversation overlay: speaker name, affiliation, typewriter body, and choices.
    ///
    /// There is no box. The dialogue is a pair of translucent bands laid across the bottom of
    /// the screen with a bright hairline between them, because a conversation in this game
    /// happens in front of the NPC the player is talking to and an opaque panel covers the
    /// one thing they are looking at. Contrast comes from the scrim plus a shadow tied to the
    /// glyphs themselves, which is what keeps white text readable when a line lands on a
    /// bright patch of the scene.
    ///
    /// Two rules make the typewriter feel good rather than tedious. First, a press while text
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
        [SerializeField] private TextMeshProUGUI _speakerSubtitle;
        [SerializeField] private RectTransform _speakerPlate;
        [SerializeField] private Image _speakerTab;
        [SerializeField] private Image _rule;
        [SerializeField] private Image _portrait;
        [SerializeField] private RectTransform _portraitFrame;
        [SerializeField] private TextMeshProUGUI _body;
        [SerializeField] private Image _advanceCaret;
        [SerializeField] private RectTransform _choiceParent;
        [SerializeField] private RectTransform _autoButton;
        [SerializeField] private Image _autoFill;
        [SerializeField] private RectTransform _menuButton;

        [Header("Tuning")]
        [SerializeField] private float _secondsPerCharacter = 0.022f;
        [Tooltip("Extra dwell after sentence-ending punctuation, in character-times.")]
        [SerializeField] private float _punctuationDwell = 7f;
        [Tooltip("Base wait before AUTO moves on, once the line has finished revealing.")]
        [SerializeField] private float _autoDwellSeconds = 1.3f;
        [Tooltip("Added to the AUTO wait per revealed character, so long lines get read.")]
        [SerializeField] private float _autoDwellPerCharacter = 0.028f;
        [SerializeField] private bool _buildOnAwake = true;

        // ------------------------------------------------------------------- layout
        // Authored against the 1920x1080 reference canvas the scaler matches by height.
        // These are deliberately constants rather than serialized fields: the composition is
        // a fixed set of proportions copied from the reference, and half of them only work
        // relative to each other — a designer nudging one in the inspector would break the
        // alignment between the name, the rule and the body copy, which is the whole idea.

        private const float Indent = 168f;
        private const float ScrimHeight = 336f;
        private const float TopFadeHeight = 148f;
        private const float RuleY = 246f;
        private const float RuleThickness = 2f;
        private const float RuleRightMargin = 132f;
        private const float NameRowY = 262f;
        private const float NameRowHeight = 74f;
        private const float BodyTopY = 218f;
        private const float BodyBottomY = 46f;
        private const float BodyRightMargin = 268f;
        private const float TabWidth = 18f;
        private const float TabHeight = 36f;
        private const int TabSlant = 3;
        private const float TabGap = 38f;
        private const float CaretSize = 36f;
        private const float CaretRightMargin = 150f;
        private const float CaretY = 84f;
        // The character illustration. Sized and placed as a drawn half-body standing at the
        // right of frame, not as the 116x172 pixel bust that used to sit in the left margin:
        // the two are different pictures doing different jobs, and a 32px overworld sprite blown
        // up to speaking size is a mosaic. Right rather than left because the name plate, the
        // rule and the body copy all share the left edge — a figure there would have to be
        // small enough to stay out of them, which is the composition this replaces.
        private const float PortraitRight = 60f;
        private const float PortraitWidth = 440f;
        private const float PortraitBottom = 232f;
        private const float PortraitHeight = 700f;
        private const float ChoiceWidth = 820f;
        private const float ChoiceHeight = 62f;
        private const int ChoiceSlant = 12;

        private readonly List<Button> _choiceButtons = new List<Button>(4);
        private TweenHandle _typing;
        private TweenHandle _caret;
        private Action _onAdvance;
        private Action<int> _onChoice;
        private Action _onMenu;
        private IReadOnlyList<string> _pendingChoices;
        private bool _revealing;
        private bool _built;
        private bool _autoAdvance;
        private float _autoCountdown = -1f;
        private float _caretHomeY = CaretY;

        /// <summary>True while the overlay is on screen.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>True while text is still being revealed.</summary>
        public bool IsRevealing => _revealing;

        /// <summary>
        /// AUTO mode: once a line has finished revealing, move on by itself after a dwell
        /// scaled to the line's length. Persists across lines, the way a visual novel's
        /// does — it is a reading mode, not a per-line property.
        /// </summary>
        public bool AutoAdvance
        {
            get => _autoAdvance;
            set
            {
                if (_autoAdvance == value) return;
                _autoAdvance = value;
                RefreshAutoVisual();
                if (!_autoAdvance) _autoCountdown = -1f;
                else if (IsOpen && !_revealing && _pendingChoices == null) ArmAutoAdvance();
            }
        }

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            SetOpen(false, true);
        }

        /// <summary>
        /// Wires the MENU control. The overlay has no idea what a menu is, so the button is
        /// hidden until someone owns it — an affordance that does nothing is worse than an
        /// absent one.
        /// </summary>
        public void SetMenuHandler(Action onMenu)
        {
            _onMenu = onMenu;
            if (_menuButton != null) _menuButton.gameObject.SetActive(onMenu != null);
            LayOutControls();
        }

        /// <summary>
        /// Shows a line. <paramref name="onAdvance"/> fires when the player presses past a
        /// fully revealed line.
        ///
        /// <paramref name="subtitle"/> is the small label beside the name — an affiliation, a
        /// role, or the place the line is spoken from. It sits on the name's baseline at two
        /// thirds the size and in the accent colour, so it reads as an annotation on the name
        /// rather than as a second name.
        ///
        /// It is a trailing optional parameter rather than an overload on purpose: a
        /// <c>Show(name, text, subtitle)</c> overload sitting beside
        /// <c>Show(name, text, portrait)</c> makes <c>Show(name, text, null)</c> ambiguous,
        /// and the caller discovers that as a compile error with no obvious fix.
        /// </summary>
        public void Show(string speaker, string body, Sprite portrait = null, Action onAdvance = null,
            string subtitle = null)
        {
            _onAdvance = onAdvance;
            _onChoice = null;
            ShowInternal(speaker, subtitle, body, portrait, null);
        }

        /// <summary>
        /// Shows a line with choices. The choices appear only once the line has finished
        /// revealing, so the player is never asked to decide before they have read the
        /// question.
        /// </summary>
        public void ShowChoices(string speaker, string body, IReadOnlyList<string> choices,
            Action<int> onChoice, Sprite portrait = null, string subtitle = null)
        {
            _onAdvance = null;
            _onChoice = onChoice;
            ShowInternal(speaker, subtitle, body, portrait, choices);
        }

        /// <summary>
        /// The single show path. The pending choices are latched *before* the reveal starts,
        /// because with reduced motion the reveal completes synchronously and a completion
        /// handler that ran before the choices were assigned would drop them silently.
        /// </summary>
        private void ShowInternal(string speaker, string subtitle, string body, Sprite portrait,
            IReadOnlyList<string> choices)
        {
            ClearChoices();
            _autoCountdown = -1f;

            // The rule goes with the name, not with the body. It exists to separate the two;
            // on an unattributed line — a sign, a system message — it would be a line drawn
            // under nothing, and narration reads better as plain text on the scrim anyway.
            var named = !string.IsNullOrWhiteSpace(speaker);
            if (_speakerPlate != null) _speakerPlate.gameObject.SetActive(named);
            if (_speakerTab != null) _speakerTab.enabled = named;
            if (_rule != null) _rule.enabled = named;
            if (_speaker != null) _speaker.SetText(speaker ?? string.Empty);

            // An empty subtitle must collapse rather than sit as a zero-width gap, or the
            // name's trailing space changes depending on data the player cannot see.
            var hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
            if (_speakerSubtitle != null)
            {
                _speakerSubtitle.gameObject.SetActive(hasSubtitle);
                _speakerSubtitle.SetText(subtitle ?? string.Empty);
            }

            // Resolved here when the caller did not supply one. The component that drives this
            // view hands over a name and a body and nothing else — it lives in another assembly
            // and is not this file's to widen — so the name is what the lookup gets. A caller
            // that does pass a sprite still wins, which is what keeps a one-off scripted shot
            // able to override the cast.
            var art = portrait != null ? portrait : DialoguePortraits.For(speaker);
            if (_portraitFrame != null) _portraitFrame.gameObject.SetActive(art != null);
            if (_portrait != null)
            {
                _portrait.sprite = art;
                _portrait.enabled = art != null;
            }

            SetOpen(true);
            _pendingChoices = choices;
            Reveal(body ?? string.Empty);
        }

        /// <summary>
        /// Player input. Completes the reveal if it is running, otherwise advances.
        /// Route the confirm button here; the view decides what the press means.
        /// </summary>
        public void Advance()
        {
            _autoCountdown = -1f;
            if (_revealing)
            {
                CompleteReveal();
                return;
            }
            if (_pendingChoices != null) return; // Waiting on a choice, not an advance.
            _onAdvance?.Invoke();
        }

        /// <summary>Hides the overlay.</summary>
        public void Close()
        {
            ClearChoices();
            _autoCountdown = -1f;
            SetOpen(false);
        }

        /// <summary>
        /// Drives AUTO. Deliberately a plain timer rather than a tween: <see cref="UiTween"/>
        /// completes instantly when motion is reduced, and an auto-advance built on it would
        /// flick through an entire conversation in one frame for anyone with that setting on.
        /// </summary>
        private void Update()
        {
            if (_autoCountdown < 0f) return;
            _autoCountdown -= Time.unscaledDeltaTime;
            if (_autoCountdown > 0f) return;
            _autoCountdown = -1f;
            _onAdvance?.Invoke();
        }

        private void ArmAutoAdvance()
        {
            if (!_autoAdvance || _onAdvance == null) { _autoCountdown = -1f; return; }
            var length = _body != null ? _body.GetParsedText().Length : 0;
            _autoCountdown = _autoDwellSeconds + length * _autoDwellPerCharacter;
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
                if (IsPause(parsed[i])) pauses++;
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
        /// Characters that earn extra dwell. The ideographic full stop and comma are in here
        /// because the game is written in Korean as well as English, and a Korean line
        /// punctuated with "…" and "!" would otherwise type at a flat machine pace.
        /// </summary>
        private static bool IsPause(char c)
        {
            return c == '.' || c == '!' || c == '?' || c == ',' ||
                   c == '。' || c == '、' || c == '…' || c == '！' || c == '？';
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
                if (IsPause(parsed[i])) units += _punctuationDwell;
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
                _autoCountdown = -1f;
            }
            else
            {
                SetCaretVisible(true);
                ArmAutoAdvance();
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
                var bob = Mathf.Sin(t * Mathf.PI * 2f) * 4f;
                _advanceCaret.rectTransform.anchoredPosition = new Vector2(
                    _advanceCaret.rectTransform.anchoredPosition.x, _caretHomeY + bob);
                _advanceCaret.color = UiPalette.ScannerCyan.WithAlpha(
                    Mathf.Lerp(0.55f, 1f, Mathf.Abs(Mathf.Sin(t * Mathf.PI))));
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
                UiBuilder.Size(root, preferredHeight: ChoiceHeight, minHeight: ChoiceHeight, flexibleWidth: 1f);

                var background = UiBuilder.Image("Bg", root, UiSprites.Slant((int)ChoiceHeight, ChoiceSlant),
                    UiPalette.Surface.WithAlpha(0.93f), Image.Type.Sliced, true);
                UiBuilder.Stretch(background.rectTransform);

                var rim = UiBuilder.Image("Rim", root, UiSprites.SlantFrame((int)ChoiceHeight, ChoiceSlant, 2),
                    UiPalette.ScannerCyan.WithAlpha(0.30f));
                UiBuilder.Stretch(rim.rectTransform);

                var label = UiBuilder.Text("Label", root, choices[i], UiTextRole.Body, UiPalette.TextPrimary);
                label.fontSize = 24f;
                UiBuilder.Stretch(label.rectTransform);
                label.rectTransform.offsetMin = new Vector2(34f, 0f);
                label.rectTransform.offsetMax = new Vector2(-28f, 0f);
                UiType.ApplyShadow(label);

                UiButtonMotion.Attach(root, 4);
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

        private void RefreshAutoVisual()
        {
            if (_autoFill == null) return;
            _autoFill.color = _autoAdvance ? UiPalette.ScannerCyan : UiPalette.ControlSlab;
        }

        // ------------------------------------------------------------------- build

        /// <summary>
        /// Builds the overlay: two translucent bands across the bottom of the screen, a
        /// hairline between them, and the small controls that sit outside the reading area.
        /// </summary>
        public void BuildRuntime()
        {
            _built = true;
            var root = transform as RectTransform;
            if (root == null) return;

            // Korean and English land in the same line, so the font has to carry both before
            // anything else here is worth looking at.
            UiType.EnsureFont();

            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(this, 0f, false, false);

            // The overlay is anchored to the screen edges, not inside a safe area: the scrim
            // is full-bleed by design and a margin would turn it back into a panel. Only the
            // text inside it is inset.
            var box = UiBuilder.Rect("Overlay", root, false);
            UiBuilder.Anchor(box, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(0f, ScrimHeight));
            _box = box;

            BuildScrim(box);

            _rule = UiBuilder.Image("Rule", box, UiSprites.FadeRule(256, 0.70f), UiPalette.RuleBright,
                Image.Type.Simple);
            Band(_rule.rectTransform, RuleY, RuleThickness, Indent, RuleRightMargin);

            BuildSpeakerRow(box);
            BuildBody(box);

            _advanceCaret = UiBuilder.Image("Caret", box, UiSprites.Chevron(40), UiPalette.ScannerCyan,
                Image.Type.Simple);
            _advanceCaret.preserveAspect = true;
            UiBuilder.Anchor(_advanceCaret.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0f), new Vector2(-CaretRightMargin, CaretY),
                new Vector2(CaretSize, CaretSize));
            // The chevron points up by default; rotate it to point down as an advance cue.
            _advanceCaret.rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
            _advanceCaret.enabled = false;
            _caretHomeY = CaretY;

            BuildControls(root);
            BuildChoiceParent(root);

            LayOutControls();
            RefreshAutoVisual();
            UiBuilder.EnsureEventSystem();
        }

        /// <summary>
        /// The two bands and the ramp above them.
        ///
        /// The body band is dragged below the screen edge on purpose: <see cref="SetOpen"/>
        /// slides the whole overlay up from -24, and a band that stopped exactly at y=0 would
        /// flash a strip of undimmed scene under the text for the length of that slide.
        /// </summary>
        private static void BuildScrim(RectTransform box)
        {
            var body = UiBuilder.Image("BodyWash", box, UiSprites.Solid(),
                UiPalette.Scrim.WithAlpha(UiPalette.ScrimBodyAlpha), Image.Type.Simple);
            Band(body.rectTransform, -48f, RuleY + 48f, 0f, 0f);

            var name = UiBuilder.Image("NameWash", box, UiSprites.Solid(),
                UiPalette.Scrim.WithAlpha(UiPalette.ScrimNameAlpha), Image.Type.Simple);
            Band(name.rectTransform, RuleY, ScrimHeight - RuleY, 0f, 0f);

            // A ramp, not an edge. See UiSprites.VerticalFade for why the top of the overlay
            // must not be a line anyone can point at.
            var fade = UiBuilder.Image("TopFade", box, UiSprites.VerticalFade(96, 1.35f),
                UiPalette.Scrim.WithAlpha(UiPalette.ScrimNameAlpha), Image.Type.Simple);
            Band(fade.rectTransform, ScrimHeight, TopFadeHeight, 0f, 0f);
        }

        private void BuildSpeakerRow(RectTransform box)
        {
            // The tab leads the eye into the name from the margin and is the only piece of
            // chrome outside the text indent. It shares the lean of the AUTO/MENU slabs, so
            // the overlay has one geometric idea rather than three.
            _speakerTab = UiBuilder.Image("SpeakerTab", box, UiSprites.Slant((int)TabHeight, TabSlant),
                UiPalette.ScannerCyan.WithAlpha(0.85f), Image.Type.Sliced);
            UiBuilder.Anchor(_speakerTab.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(Indent - TabGap, NameRowY + 10f),
                new Vector2(TabWidth, TabHeight));

            var plate = UiBuilder.Rect("SpeakerRow", box, false);
            Band(plate, NameRowY, NameRowHeight, Indent, RuleRightMargin);
            // Bottom-aligned rather than centred: the subtitle is smaller, and aligning the
            // two boxes at their bottoms is what puts their baselines on the same line.
            UiBuilder.Horizontal(plate, 16f, new RectOffset(0, 0, 0, 10), TextAnchor.LowerLeft);
            _speakerPlate = plate;

            _speaker = UiBuilder.Text("Name", plate, string.Empty, UiTextRole.Title, UiPalette.TextPrimary);
            _speaker.fontSize = 36f;
            _speaker.characterSpacing = -0.5f;
            _speaker.textWrappingMode = TextWrappingModes.NoWrap;
            UiType.ApplyShadow(_speaker, offsetX: 0.35f, offsetY: -0.45f, softness: 0.3f, dilate: 0.14f);

            _speakerSubtitle = UiBuilder.Text("Subtitle", plate, string.Empty, UiTextRole.Body,
                UiPalette.ScannerCyan);
            _speakerSubtitle.fontSize = 22f;
            _speakerSubtitle.fontStyle = FontStyles.Bold;
            _speakerSubtitle.textWrappingMode = TextWrappingModes.NoWrap;
            UiType.ApplyShadow(_speakerSubtitle, offsetX: 0.3f, offsetY: -0.4f, softness: 0.3f, dilate: 0.1f);
            _speakerSubtitle.gameObject.SetActive(false);
        }

        private void BuildBody(RectTransform box)
        {
            // Unframed, and it rises well above the scrim rather than sitting inside it. Both
            // are the same decision: the figure is meant to be standing in the scene the player
            // is looking at, and a border around it — or a crop that stops at the band — turns
            // it back into an inset picture of a person instead of the person.
            //
            // It starts above the body band, so a line reads at exactly the same measure whether
            // or not a character has art. That is what lets the whole cast ship without
            // illustrations and the layout still be the final one.
            var portraitFrame = UiBuilder.Rect("Portrait", box, false);
            UiBuilder.Anchor(portraitFrame, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-PortraitRight, PortraitBottom), new Vector2(PortraitWidth, PortraitHeight));
            _portraitFrame = portraitFrame;

            _portrait = UiBuilder.Image("Image", portraitFrame, null, Color.white, Image.Type.Simple);
            _portrait.preserveAspect = true;
            UiBuilder.Stretch(_portrait.rectTransform);
            portraitFrame.gameObject.SetActive(false);

            _body = UiBuilder.Text("Body", box, string.Empty, UiTextRole.Body, UiPalette.TextPrimary,
                TextAlignmentOptions.TopLeft);
            _body.fontSize = 30f;
            _body.lineSpacing = 14f;
            _body.overflowMode = TextOverflowModes.Overflow;
            Band(_body.rectTransform, BodyBottomY, BodyTopY - BodyBottomY, Indent, BodyRightMargin);
            UiType.ApplyShadow(_body);
        }

        /// <summary>
        /// The two slabs in the top corner.
        ///
        /// Anchored one by one from the right edge rather than run through a layout group.
        /// A group here has to be paired with a size fitter to hug two fixed-width children,
        /// and the pair resolves in the wrong order often enough that the first slab
        /// collapses to a sliver — which is exactly what it did. Two constants are less code
        /// than the workaround, and the arrangement is only ever these two items.
        /// </summary>
        private void BuildControls(RectTransform root)
        {
            var bar = UiBuilder.Rect("Controls", root, false);
            UiBuilder.Anchor(bar, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-96f, -40f), new Vector2(ControlAutoWidth + ControlMenuWidth + ControlGap,
                    ControlHeight));

            // Both labels come from the table rather than from a literal here. The slabs are
            // the loudest thing in the composition and they sit directly above a Korean
            // dialogue line, so two English words there is the one place the mixed-language
            // seam would be impossible not to see.
            _menuButton = ControlSlab(bar, "Menu", Core.Loc.Get("ui.menu"), ControlMenuWidth, 0f, out _,
                () => _onMenu?.Invoke());
            _menuButton.gameObject.SetActive(false);

            _autoButton = ControlSlab(bar, "Auto", Core.Loc.Get("ui.auto"), ControlAutoWidth, 0f, out _autoFill,
                () => AutoAdvance = !AutoAdvance);
        }

        private const float ControlHeight = 46f;
        private const float ControlAutoWidth = 118f;
        private const float ControlMenuWidth = 128f;
        private const float ControlGap = 12f;

        /// <summary>
        /// Slides AUTO into the corner when MENU is not there, so a single control never sits
        /// with a hole beside it where the other one would have been.
        /// </summary>
        private void LayOutControls()
        {
            if (_autoButton == null) return;
            var menuShown = _menuButton != null && _menuButton.gameObject.activeSelf;
            _autoButton.anchoredPosition = new Vector2(menuShown ? -(ControlMenuWidth + ControlGap) : 0f, 0f);
        }

        /// <summary>
        /// One of the small leaning slabs in the top corner. Near-white with dark italic
        /// type, which is the reference's one deliberately loud element — everything else in
        /// the composition is trying to disappear, so these have to not.
        /// </summary>
        private static RectTransform ControlSlab(RectTransform parent, string name, string label,
            float width, float offsetX, out Image fill, Action onClick)
        {
            var slab = UiBuilder.Rect(name, parent, false);
            UiBuilder.Anchor(slab, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(offsetX, 0f), new Vector2(width, ControlHeight));

            fill = UiBuilder.Image("Fill", slab, UiSprites.Slant((int)ControlHeight, 9),
                UiPalette.ControlSlab, Image.Type.Sliced, true);
            UiBuilder.Stretch(fill.rectTransform);

            var text = UiBuilder.Text("Label", slab, label, UiTextRole.Body, UiPalette.TextOnAccent,
                TextAlignmentOptions.Center);
            text.fontSize = 21f;
            text.fontStyle = FontStyles.Bold | FontStyles.Italic;
            text.characterSpacing = 2f;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Stretch(text.rectTransform);

            UiButtonMotion.Attach(slab, 4);
            UiBuilder.Button("Click", slab, fill, onClick);
            return slab;
        }

        private void BuildChoiceParent(RectTransform root)
        {
            var choices = UiBuilder.Rect("Choices", root, false);
            UiBuilder.Anchor(choices, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(Indent, ScrimHeight + 24f), new Vector2(ChoiceWidth, 0f));
            UiBuilder.Vertical(choices, 12f, null, TextAnchor.LowerLeft);
            UiBuilder.Fit(choices);
            _choiceParent = choices;
            choices.gameObject.SetActive(false);
        }

        /// <summary>
        /// Places a rect as a horizontal band inside the bottom-anchored overlay: measured up
        /// from the screen's bottom edge, inset from both sides. Every element in this
        /// composition is one of these, which is what keeps the name, the rule and the body
        /// copy on the same left edge at any aspect ratio.
        /// </summary>
        private static void Band(RectTransform rect, float bottom, float height, float left, float right)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, bottom + height);
        }
    }
}
