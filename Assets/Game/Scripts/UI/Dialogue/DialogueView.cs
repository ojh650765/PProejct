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
        [SerializeField] private Image _backdrop;
        [SerializeField] private CanvasGroup _portraitGroup;

        [Tooltip("Seconds for a speaker to fade in or out of the frame.")]
        [SerializeField] private float _portraitFadeSeconds = 0.22f;

        private float _portraitTargetAlpha;
        [SerializeField] private TextMeshProUGUI _body;
        [SerializeField] private Image _advanceCaret;
        [SerializeField] private RectTransform _choiceParent;
        [SerializeField] private RectTransform _autoButton;
        [SerializeField] private Image _autoFill;

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
        // Centred, not tucked into the right corner. The portraits are drawn facing the
        // viewer, and a figure looking straight out from the edge of the frame reads as
        // having been pushed aside; the games put the speaker in the middle of the shot.
        // Two framings, because a conversation and a staged scene are not the same shot.
        //
        // Staged is the opening: the professor alone on a drawn room, whole figure in frame,
        // standing clear of the box. It is a composed picture and cropping it would spoil it.
        //
        // Close is every other conversation: the speaker enlarged and stood at the bottom of
        // the screen so the frame cuts them below the knee. That is what makes somebody read
        // as leaning into the shot to talk to you rather than as a doll placed in the middle
        // of it — and at this size their face is large enough to carry the line.
        private const float StagedWidth = 620f;
        private const float StagedBottom = 300f;
        private const float StagedHeight = 720f;

        private const float CloseWidth = 900f;
        private const float CloseHeight = 1180f;
        // Negative: the figure's feet sit below the screen edge, so the cut lands on the shin
        // rather than on the floor beneath them.
        private const float CloseBottom = -230f;
        private const float ChoiceWidth = 820f;
        private const float ChoiceHeight = 62f;
        private const int ChoiceSlant = 12;

        private readonly List<Button> _choiceButtons = new List<Button>(4);
        private TweenHandle _typing;
        private Core.IUiSoundBank _sounds;
        private int _spokenCharacters;
        private TweenHandle _caret;
        private Action _onAdvance;
        private Action<int> _onChoice;
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
        /// <summary>
        /// What sits behind the speaker: a drawn room, a texture grabbed from the game, or
        /// nothing at all.
        ///
        /// Passing null clears it, which is the right answer for a line spoken by something
        /// that is not a person — a sign, a terminal — where a staged background would imply
        /// a face that is not there.
        /// </summary>
        /// <summary>
        /// Chooses how the speaker is framed: the opening's composed full figure, or the
        /// close crop every ordinary conversation uses.
        /// </summary>
        public void SetPortraitFraming(bool staged)
        {
            if (_portraitFrame == null) return;

            UiBuilder.Anchor(_portraitFrame, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0f, staged ? StagedBottom : CloseBottom),
                staged ? new Vector2(StagedWidth, StagedHeight)
                       : new Vector2(CloseWidth, CloseHeight));
        }

        public void SetBackdrop(Sprite room, Texture blurredWorld = null, float dim = 1f)
        {
            if (_backdrop == null) return;

            if (room != null)
            {
                _backdrop.sprite = room;
                _backdrop.material = null;
                _backdrop.color = new Color(dim, dim, dim, 1f);
                _backdrop.enabled = true;
                return;
            }

            if (blurredWorld != null)
            {
                // Wrapped in a Sprite because Image cannot take a bare Texture, and rebuilt
                // each time rather than cached: the capture is a different RenderTexture on
                // every open, and a sprite pointing at a released one draws garbage.
                var texture2d = blurredWorld as Texture2D;
                if (texture2d != null)
                {
                    _backdrop.sprite = Sprite.Create(texture2d,
                        new Rect(0f, 0f, texture2d.width, texture2d.height), new Vector2(0.5f, 0.5f));
                    _backdrop.color = new Color(dim, dim, dim, 1f);
                    _backdrop.enabled = true;
                    return;
                }
            }

            _backdrop.enabled = false;
            _backdrop.sprite = null;
        }

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
        /// Kept so the capture harness still compiles; the overlay has no MENU control any
        /// more, because the trainer menu is Escape's and belongs over the world.
        /// </summary>
        [System.Obsolete("The dialogue overlay no longer has a MENU button.")]
        public void SetMenuHandler(Action onMenu) { }

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
            if (_portraitFrame != null) _portraitFrame.gameObject.SetActive(true);
            if (_portrait != null)
            {
                _portrait.sprite = art;
                _portrait.enabled = art != null;
                _portraitTargetAlpha = art != null ? 1f : 0f;
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
                // Completing a reveal is deliberately silent: the flood of text arriving is
                // its own feedback, and a confirm blip here would make the fast-forward
                // press and the advance press sound like the same action.
                CompleteReveal();
                return;
            }
            if (_pendingChoices != null) return; // Waiting on a choice, not an advance.
            Sounds?.Confirm();
            _onAdvance?.Invoke();
        }

        /// <summary>Hides the overlay.</summary>
        public void Close()
        {
            ClearChoices();
            _autoCountdown = -1f;
            // The speaker leaves with the box rather than vanishing with it: the fade keeps
            // running while the panel slides away, so the figure goes out rather than blinking.
            _portraitTargetAlpha = 0f;
            SetOpen(false);
        }

        /// <summary>
        /// Drives AUTO. Deliberately a plain timer rather than a tween: <see cref="UiTween"/>
        /// completes instantly when motion is reduced, and an auto-advance built on it would
        /// flick through an entire conversation in one frame for anyone with that setting on.
        /// </summary>
        private void Update()
        {
            DrivePortraitFade();
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

            // The typewriter speaks. The bank is told a line is starting so its every-nth
            // cadence restarts on a blip, and then hears every revealed character below —
            // which characters actually sound (skipping spaces, throttling floods) is the
            // bank's decision, not this view's.
            _spokenCharacters = 0;
            Sounds?.BeginTypewriterLine();

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
                var shown = CharactersAt(parsed, t, pauses);
                _body.maxVisibleCharacters = shown;
                SpeakRevealed(parsed, shown);
            }, Ease.Linear, 0f, true, CompleteReveal);
        }

        /// <summary>
        /// Feeds each newly revealed character to the sound bank exactly once.
        ///
        /// Resolved lazily and treated as optional: dialogue must read identically in a
        /// scene where the audio layer never booted, so absence is silence, never a wait.
        /// </summary>
        private void SpeakRevealed(string parsed, int shownCount)
        {
            if (shownCount <= _spokenCharacters) return;
            var bank = Sounds;
            if (bank == null)
            {
                _spokenCharacters = shownCount;
                return;
            }
            for (var i = _spokenCharacters; i < shownCount && i < parsed.Length; i++)
            {
                bank.TypewriterTick(parsed[i]);
            }
            _spokenCharacters = shownCount;
        }

        private Core.IUiSoundBank Sounds
        {
            get
            {
                if (_sounds == null) Core.ServiceHub.TryGet(out _sounds);
                return _sounds;
            }
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
            // A fast-forwarded line must not machine-gun the remaining characters into the
            // bank in one frame — the flood is visual, so the voice just stops here.
            _spokenCharacters = int.MaxValue;
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
            Sounds?.Confirm();
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
            // Whether this call is the box arriving, rather than one already-open line
            // replacing another. Read before IsOpen is overwritten.
            var arriving = open && !IsOpen;

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

            // The slide belongs to the box appearing, not to every line inside it.
            //
            // Show is called once per line and this ran every time, so the panel dropped and
            // rose again under text the player was in the middle of reading — the whole
            // conversation bounced with each press. Now it enters once and then holds still,
            // and only the words change.
            if (arriving && _box != null)
            {
                var target = _box.anchoredPosition;
                _box.anchoredPosition = target + new Vector2(0f, -24f);
                UiTween.AnchoredMove(_box, target, 0.28f, Ease.OutCubic);
            }

            // Likewise the fade: only on the way in or out. Re-fading an already-visible box
            // makes every advance flicker.
            if (open && !arriving) return;

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

            // A full-screen catcher, behind everything, that advances on click.
            //
            // The caret is a 36-pixel target in the corner and the box itself is not clickable,
            // so a player reaching for the mouse had nothing to hit. Built first so it is
            // underneath every real control — the AUTO toggle and the choice buttons consume
            // their own clicks before this ever sees them.
            var catcher = UiBuilder.Image("ClickCatcher", root, null, new Color(0f, 0f, 0f, 0f),
                Image.Type.Simple);
            UiBuilder.Stretch(catcher.rectTransform);
            catcher.raycastTarget = true;
            UiBuilder.Button("ClickCatcher", catcher.rectTransform, catcher, OnClickAnywhere);

            // The speaker is built on the root, before and outside the overlay.
            //
            // It used to be a child of the box, and the box slides up from the bottom of the
            // screen every time a line opens — so the person rode up with the panel and
            // settled with it, as though they were printed on it. They are two different
            // things: the panel is furniture that comes and goes, the speaker is standing in
            // the scene behind it. Separating them is also what puts the box in front, since
            // the speaker is now built first and the whole overlay after.
            BuildSpeakerLayer(root);

            // The overlay was created first and therefore sat at the bottom of the draw order,
            // which put the click catcher, the backdrop and the speaker on top of it — a full
            // screen image covering the scrim, the name plate and every line of text. The
            // conversation was running correctly behind a picture of a room.
            //
            // Moved to the end here rather than built later, because the box has to exist
            // before the pieces below can be parented into it. Everything built after this —
            // the controls, the choice buttons — still lands above the box, which is right.
            box.SetAsLastSibling();

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
            // One continuous ramp from the bottom edge of the screen to nothing.
            //
            // This was three pieces: a flat wash behind the body, a lighter flat wash behind
            // the name, and a short fade on top. Three constant alphas meant two visible steps
            // across the overlay — you could point at where one band ended and the next began,
            // which is exactly what a scrim is supposed to avoid. A single gradient is darkest
            // where the screen ends and gone by the time it reaches the speaker's waist, so
            // the text sits on enough ground to read and nothing draws a horizon.
            //
            // The gamma is what keeps the dark end short: a linear ramp over this height puts
            // half the screen in shadow, while a curved one holds the density near the bottom
            // and lets go quickly.
            var wash = UiBuilder.Image("Scrim", box, UiSprites.VerticalFade(256, 1.9f),
                UiPalette.Scrim.WithAlpha(UiPalette.ScrimBodyAlpha), Image.Type.Simple);
            wash.raycastTarget = false;
            Band(wash.rectTransform, -48f, ScrimHeight + TopFadeHeight + 48f, 0f, 0f);
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

        /// <summary>
        /// Walks the speaker's alpha toward where it should be.
        ///
        /// Unscaled, because a conversation can be running while the world is paused — a fade
        /// on scaled time would simply never finish there.
        /// </summary>
        /// <summary>
        /// A click on empty space.
        ///
        /// Ignored while choices are up: there the player is picking between answers, and a
        /// stray click on the background must not count as taking one.
        /// </summary>
        private void OnClickAnywhere()
        {
            if (!IsOpen || _pendingChoices != null) return;
            Advance();
        }

        private void DrivePortraitFade()
        {
            if (_portraitGroup == null) return;

            var step = _portraitFadeSeconds > 0.001f
                ? Time.unscaledDeltaTime / _portraitFadeSeconds
                : 1f;
            _portraitGroup.alpha = Mathf.MoveTowards(_portraitGroup.alpha, _portraitTargetAlpha, step);
        }

        /// <summary>
        /// The speaker and whatever is behind them, on their own layer.
        ///
        /// Deliberately not part of the dialogue box. The box is a panel that slides in and
        /// out; this is the scene the conversation is happening in, and it holds still while
        /// the panel moves.
        /// </summary>
        private void BuildSpeakerLayer(RectTransform root)
        {
            _backdrop = UiBuilder.Image("Backdrop", root, null, Color.white, Image.Type.Simple);
            UiBuilder.Stretch(_backdrop.rectTransform);
            _backdrop.raycastTarget = false;
            _backdrop.enabled = false;

            var portraitFrame = UiBuilder.Rect("Speaker", root, false);
            UiBuilder.Anchor(portraitFrame, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, CloseBottom),
                new Vector2(CloseWidth, CloseHeight));
            _portraitFrame = portraitFrame;

            _portrait = UiBuilder.Image("Image", portraitFrame, null, Color.white, Image.Type.Simple);
            _portrait.preserveAspect = true;
            _portrait.raycastTarget = false;
            UiBuilder.Stretch(_portrait.rectTransform);

            // Faded rather than switched. A figure that appears between two frames reads as a
            // pop-up; the same figure over a fifth of a second reads as somebody stepping into
            // the shot, which is what it is meant to be.
            _portraitGroup = portraitFrame.gameObject.AddComponent<CanvasGroup>();
            _portraitGroup.alpha = 0f;
            _portraitGroup.blocksRaycasts = false;
            _portraitGroup.interactable = false;
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
            // No MENU slab. The trainer menu is Escape's, opened over the world by
            // StartMenuPresenter, and a second door to it sitting on top of a conversation is
            // both a duplicate and the loudest object on a screen that is meant to be read.
            _autoButton = ControlSlab(bar, "Auto", Core.Loc.Get("ui.auto"), ControlAutoWidth, 0f, out _autoFill,
                () =>
                {
                    // Navigate rather than Confirm: flipping a reading mode is an adjustment,
                    // not a commitment, and the quieter blip keeps it out of the line's way.
                    Sounds?.Navigate();
                    AutoAdvance = !AutoAdvance;
                });
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
            _autoButton.anchoredPosition = Vector2.zero;
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
