using System;
using System.Collections.Generic;
using PokeLab.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// The first screen of the game: sign in, make an account, or go on without one.
    ///
    /// <b>It is the title screen with a form in the column.</b> Same left column, same width,
    /// same padding, same header block, same distance down to the first control — the constants
    /// are shared with <see cref="MainMenuView"/> deliberately, so that walking from here to the
    /// title is a continuation rather than the whole layout jumping across the screen. It was
    /// built the other way round first, with the form on the right, and two consecutive screens
    /// with their interactive half on opposite sides made both of them feel arbitrary.
    ///
    /// <b>Both paths are on the screen at once.</b> 로그인 and 회원가입 are two buttons under
    /// the same three fields rather than two tabs, because a tab hides half the screen's purpose
    /// from a player who has not realised there is a choice to make — and the two paths want the
    /// same three answers, so there is nothing to hide.
    ///
    /// <b>Almost nothing is explained.</b> This screen used to open with three paragraphs about
    /// where saves live and what happens when a browser clears its storage, and the user's
    /// verdict on that was the right one: TMI, and not juicy. A front door is not a manual. The
    /// only sentence left is the one warning next to the answer field, which is a real risk to
    /// the player rather than a fact about the backend; the rest of the screen is a name, a
    /// question, an answer and a button, over something alive.
    ///
    /// The view owns no session state. It reports which button was pressed and hands over what
    /// is in the fields; whether an account exists, what the server said and where to go next
    /// are the presenter's business — this assembly cannot see PokeLab.Online at all.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LoginView : MonoBehaviour
    {
        /// <summary>Raised when a submit button is pressed. True for 회원가입, false for 로그인.</summary>
        public Action<bool> Submitted;

        /// <summary>Raised by 계정 없이 둘러보기.</summary>
        public Action Skipped;

        /// <summary>Raised when the player finishes editing the server override.</summary>
        public Action<string> ServerCommitted;

        /// <summary>Raised when the recovery question changes, so the presenter can note it.</summary>
        public Action<int> QuestionChanged;

        // Shared with MainMenuView on purpose: the login column and the title column are the
        // same column, and a screen that shifts by twenty pixels between them looks broken in a
        // way nobody can point at.
        private const float SafeX = 64f;
        private const float SafeY = 44f;
        private const float ColumnWidth = 720f;
        private const float ColumnPad = 40f;
        private const float RowWidth = ColumnWidth - ColumnPad * 2f;
        private const float FormTop = -232f;

        private TMP_InputField _name;
        private TMP_InputField _answer;
        private TMP_InputField _server;
        private RectTransform _serverButton;
        private RectTransform _serverField;
        private TextMeshProUGUI _question;
        private TextMeshProUGUI _status;
        private CanvasGroup _actions;

        private IReadOnlyList<string> _questions = Array.Empty<string>();
        private int _questionIndex;
        private bool _busy;

        /// <summary>What is in the 트레이너 이름 field right now.</summary>
        public string TrainerName => _name != null ? _name.text : "";

        /// <summary>What is in the 답 field right now.</summary>
        public string Answer => _answer != null ? _answer.text : "";

        /// <summary>Which recovery question is showing.</summary>
        public int QuestionIndex => _questionIndex;

        /// <summary>The server override, which is whatever the tucked-away field holds.</summary>
        public string Server => _server != null ? _server.text : "";

        /// <summary>
        /// Builds the screen once.
        ///
        /// Unlike <see cref="MainMenuView"/> this is not rebuilt as state changes: the rows of a
        /// title screen appear and disappear with the save and the account, but a form's fields
        /// do not, and rebuilding one would throw away what the player had already typed.
        /// Everything that moves afterwards moves through <see cref="Say"/> and
        /// <see cref="SetBusy"/>.
        /// </summary>
        /// <param name="wordmark">The game's name, drawn at display size.</param>
        /// <param name="subtitle">The small line under the rule.</param>
        /// <param name="questions">
        /// The recovery-question prompts, already localised. Passed in rather than read here
        /// because they live in PokeLab.Online, which this assembly cannot reference.
        /// </param>
        /// <param name="server">The server address currently in effect.</param>
        /// <param name="savedName">The trainer name this device last used, or empty.</param>
        /// <param name="serverOverride">
        /// Whether to draw the server control at all. False in a shipped player: see
        /// <see cref="BuildServerControl"/>.
        /// </param>
        public void Build(string wordmark, string subtitle, IReadOnlyList<string> questions,
                          string server, string savedName, bool serverOverride)
        {
            var root = transform as RectTransform;
            if (root == null)
            {
                Debug.LogError("LoginView must live on a RectTransform under a Canvas.", this);
                return;
            }

            _questions = questions ?? (IReadOnlyList<string>)Array.Empty<string>();
            _questionIndex = 0;

            UiBuilder.ClearChildren(root);
            UiBuilder.Stretch(root);

            UiJuice.Backdrop(root);

            var safe = UiBuilder.SafeArea(root, SafeX, SafeY);

            BuildMotif(safe);
            BuildColumn(safe, wordmark, subtitle, server, savedName, serverOverride);
            BuildHints(safe);
        }

        // ------------------------------------------------------------------ the motif

        /// <summary>
        /// What is on the right: one very large ball, breathing, with two rings thrown off it as
        /// the screen arrives.
        ///
        /// The right half is deliberately given nothing to read. It used to hold a column of
        /// explanation and that was the whole problem with the screen — so what stands there now
        /// is the game's own mark at a size that makes it scenery rather than an icon, drifting
        /// against the backdrop's lights. It is the difference between a form and a front door,
        /// and it costs no words at all.
        /// </summary>
        private static void BuildMotif(Transform safe)
        {
            var stage = UiBuilder.Rect("Motif", safe, false);
            UiBuilder.Anchor(stage, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-540f, 20f), new Vector2(560f, 560f));

            var halo = UiBuilder.Image("Halo", stage, UiSprites.Glow(256, 1.6f),
                UiPalette.AceCyan.WithAlpha(0.22f), Image.Type.Simple);
            UiBuilder.Stretch(halo.rectTransform, -140f);
            UiIdle.Attach(halo.rectTransform, UiIdleMode.Pulse, 0.06f, 5.2f);

            // Held back from full strength so it reads as scenery rather than as a button, but
            // not much: at 0.62 the red cap turned a washed-out pink and the whole mark looked
            // faded rather than distant.
            var ball = UiJuice.Ball(stage, 430f);
            UiBuilder.Group(ball, 0.86f, false, false);
            UiIdle.Attach(ball, UiIdleMode.Bob, 16f, 5.6f);

            // Thrown off the ball as it lands, not looping. A ring that repeats forever becomes
            // wallpaper; two that fire once say the screen just arrived.
            UiJuice.Shockwave(stage, UiPalette.AceCyan.WithAlpha(0.5f), 430f, 2.2f, 1.1f, 0.42f);
            UiJuice.Shockwave(stage, UiPalette.AceLime.WithAlpha(0.32f), 430f, 2.6f, 1.3f, 0.62f);

            UiJuice.PopScale(ball, 0.2f, 0.7f, 0.62f);

            Mote(stage, new Vector2(-232f, 182f), 58f, 3.4f, 0f);
            Mote(stage, new Vector2(214f, -142f), 44f, 4.1f, 0.35f);
            Mote(stage, new Vector2(108f, 238f), 36f, 4.8f, 0.7f);
            Mote(stage, new Vector2(-146f, -216f), 30f, 5.4f, 0.15f);
        }

        /// <summary>
        /// One drifting light. A soft radial glow, not <see cref="UiSprites.Sparkle"/> — that
        /// sprite is a four-point star, and at this size on a dark field three of them read as
        /// three cyan ✕ marks, which is what the capture showed and which looks like debug
        /// output rather than atmosphere.
        /// </summary>
        private static void Mote(Transform stage, Vector2 at, float size, float period, float phase)
        {
            var mark = UiBuilder.Image("Mote", stage, UiSprites.Glow(64, 2.4f),
                UiPalette.AceCyan.WithAlpha(0.34f), Image.Type.Simple);
            UiBuilder.Anchor(mark.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), at, new Vector2(size, size));
            UiIdle.Attach(mark.rectTransform, UiIdleMode.Bob, 12f, period, phase);
        }

        // ----------------------------------------------------------------- the column

        private void BuildColumn(Transform safe, string wordmark, string subtitle, string server,
                                 string savedName, bool serverOverride)
        {
            var column = UiBuilder.Rect("Column", safe, false);
            UiBuilder.Anchor(column, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                Vector2.zero, new Vector2(ColumnWidth, 0f));
            UiJuice.Pane("Glass", column, UiPalette.AceDepth.WithAlpha(0.78f), 26, true, true, true,
                UiPalette.AceRim, 220);

            BuildHeader(column, wordmark, subtitle);

            var y = FormTop;

            // --- name ---------------------------------------------------------------------
            var nameBlock = Block(column, ref y, 100f, 0.16f);
            Label(nameBlock, 0f, Loc.Pick("TRAINER NAME", "트레이너 이름"));
            _name = Field(Slot(nameBlock, 36f, 64f), savedName, Loc.Pick("2-16 characters", "2~16자"));

            // --- question -----------------------------------------------------------------
            var questionBlock = Block(column, ref y, 100f, 0.22f);
            Label(questionBlock, 0f, Loc.Pick("RECOVERY QUESTION", "본인 확인 질문"));
            BuildQuestionRow(Slot(questionBlock, 36f, 64f));

            // --- answer -------------------------------------------------------------------
            //
            // The field and its label, and nothing else. A caution glyph and the line
            // 다른 곳에서 쓰는 답은 쓰지 마세요 used to sit under here; the user cut it, and it
            // was the same TMI the account copy was cut for. A front door that lectures on the
            // way in is not being careful, it is being slow. The block loses the height that
            // caption occupied -- 138 was sized around a sentence that is gone, and keeping it
            // would leave a hole between this field and the status line.
            var answerBlock = Block(column, ref y, 104f, 0.28f);
            Label(answerBlock, 0f, Loc.Pick("ANSWER", "답"));
            _answer = Field(Slot(answerBlock, 36f, 64f), "",
                Loc.Pick("Your answer", "답을 입력하세요"));

            // --- what the server said -----------------------------------------------------
            _status = UiBuilder.Text("Status", column, "", UiTextRole.Secondary,
                UiPalette.AceCyan, TextAlignmentOptions.TopLeft);
            UiBuilder.Anchor(_status.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(ColumnPad, y), new Vector2(-ColumnPad * 2f, 58f));
            y -= 66f;

            // --- the two paths ------------------------------------------------------------
            var actions = UiBuilder.Rect("Actions", column, false);
            UiBuilder.Anchor(actions, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(ColumnPad, y), new Vector2(-ColumnPad * 2f, 78f));
            _actions = UiBuilder.Group(actions);

            var half = (RowWidth - 16f) * 0.5f;
            Button(actions, Loc.Pick("Create account", "회원가입"), UiPalette.AceLime.WithAlpha(0.92f),
                Vector2.zero, new Vector2(0f, 0.5f), () => Submitted?.Invoke(true),
                new Vector2(half, 78f), UiPalette.AceInk);
            Button(actions, Loc.Pick("Sign in", "로그인"), UiPalette.AceCyan.WithAlpha(0.92f),
                Vector2.zero, new Vector2(1f, 0.5f), () => Submitted?.Invoke(false),
                new Vector2(half, 78f), UiPalette.AceInk);
            UiJuice.PopIn(actions, 0.34f, new Vector2(-160f, 0f));
            y -= 90f;

            // --- the way out ---------------------------------------------------------------
            y = Divider(column, y, Loc.Pick("OR", "또는"));

            var guest = UiBuilder.Rect("Guest", column, false);
            UiBuilder.Anchor(guest, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(ColumnPad, y), new Vector2(-ColumnPad * 2f, 100f));

            Button(guest, Loc.Pick("Look around without an account", "계정 없이 둘러보기"),
                UiPalette.AceGlassLift.WithAlpha(0.5f), Vector2.zero, new Vector2(0.5f, 1f),
                // The wash belongs to the presenter, not here: Escape leaves by the same door
                // and would otherwise get a bare cut while the button got a flourish.
                () => Skipped?.Invoke(), new Vector2(RowWidth, 64f), UiPalette.AceText, false);

            var cost = UiBuilder.Text("Cost", guest, Loc.Pick("Nothing will be saved.",
                                                             "저장은 되지 않아요."),
                UiTextRole.Caption, UiPalette.AceTextFaint, TextAlignmentOptions.Center);
            UiBuilder.Anchor(cost.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -68f), new Vector2(0f, 30f));

            UiJuice.PopIn(guest, 0.4f, new Vector2(-160f, 0f));

            if (serverOverride) BuildServerControl(column, server);

            UiJuice.PopIn(column, 0f, new Vector2(-140f, 0f), 0.5f);
        }

        /// <summary>
        /// The wordmark block, geometry for geometry with the title screen's.
        ///
        /// Metric is 96pt and the box is 124: <see cref="UiType.Apply"/> gives every label
        /// overflowMode Ellipsis, and TMP draws NOTHING AT ALL when the first line is taller
        /// than its rect, so every display box here carries real headroom.
        /// </summary>
        private static void BuildHeader(Transform column, string wordmark, string subtitle)
        {
            var block = UiBuilder.Rect("Header", column, false);
            UiBuilder.Anchor(block, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(ColumnPad, -34f), new Vector2(-ColumnPad * 2f, 176f));

            var lamp = UiBuilder.Image("Lamp", block, UiSprites.Glow(128, 1.7f),
                UiPalette.AceCyan.WithAlpha(0.5f), Image.Type.Simple);
            UiBuilder.Anchor(lamp.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(30f, -52f), new Vector2(150f, 150f));

            var mark = UiJuice.Ball(block, 56f);
            UiBuilder.Anchor(mark, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f),
                new Vector2(30f, -52f), new Vector2(56f, 56f));
            UiIdle.Attach(mark, UiIdleMode.Bob, 4f, 2.4f);

            var name = UiBuilder.Text("Name", block, wordmark, UiTextRole.Metric,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(76f, 6f), new Vector2(-76f, 124f));

            var rule = UiBuilder.Image("Rule", block, UiSprites.FadeRule(256, 0.55f),
                UiPalette.AceCyan.WithAlpha(0.85f), Image.Type.Simple);
            UiBuilder.Anchor(rule.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(4f, -122f), new Vector2(-4f, 4f));

            var under = UiBuilder.Text("Subtitle", block, subtitle, UiTextRole.Overline,
                UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(under.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(6f, -136f), new Vector2(-6f, 30f));
        }

        private void BuildQuestionRow(RectTransform row)
        {
            Well("Well", row, out _);

            _question = UiBuilder.Text("Question", row, CurrentQuestion(), UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_question.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
            _question.rectTransform.offsetMin = new Vector2(22f, 6f);
            _question.rectTransform.offsetMax = new Vector2(-146f, -6f);

            Button(row, "‹", UiPalette.AceCyan.WithAlpha(0.28f), new Vector2(-78f, 0f),
                new Vector2(1f, 0.5f), () => CycleQuestion(-1), new Vector2(58f, 54f));
            Button(row, "›", UiPalette.AceCyan.WithAlpha(0.28f), new Vector2(-12f, 0f),
                new Vector2(1f, 0.5f), () => CycleQuestion(1), new Vector2(58f, 54f));
        }

        /// <summary>
        /// The server override: one small muted button at the foot of the column, and the field
        /// only once it is asked for.
        ///
        /// <b>A player never sees this.</b> It is drawn only in the editor and in a development
        /// build — the caller decides, and <c>LoginPresenter</c> compiles the decision out of a
        /// shipped player. It was the first field on the account panel once, and it had to be:
        /// the Worker's hostname was not knowable when the game was compiled. <c>OnlineConfig</c>
        /// now carries a real deployed default, so the only thing left for this to do is point a
        /// developer at a local <c>wrangler dev</c>, and a debugging tool on the front door of a
        /// game is a debugging tool the player has to wonder about.
        /// </summary>
        private void BuildServerControl(Transform column, string server)
        {
            _serverButton = UiBuilder.Rect("ServerButton", column, false);
            UiBuilder.Anchor(_serverButton, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(ColumnPad, 26f), new Vector2(230f, 52f));

            Button(_serverButton, Loc.Pick("Server", "서버 주소"),
                UiPalette.AceGlass.WithAlpha(0.42f), Vector2.zero, new Vector2(0.5f, 0.5f),
                ShowServerField, new Vector2(230f, 52f), UiPalette.AceTextDim, false);

            _serverField = UiBuilder.Rect("ServerField", column, false);
            UiBuilder.Anchor(_serverField, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(ColumnPad, 22f), new Vector2(-ColumnPad * 2f, 64f));
            _server = Field(_serverField, server, "https://…workers.dev");
            _server.onEndEdit.AddListener(value => ServerCommitted?.Invoke(value));
            _serverField.gameObject.SetActive(false);
        }

        private void ShowServerField()
        {
            if (_serverButton != null) _serverButton.gameObject.SetActive(false);
            if (_serverField == null) return;
            _serverField.gameObject.SetActive(true);
            UiJuice.PopScale(_serverField, 0f, 0.9f, 0.28f);
            if (_server != null) _server.Select();
        }

        private void BuildHints(Transform safe)
        {
            var hints = new[]
            {
                new UiJuice.Hint("Tab", Loc.Pick("next field", "다음 칸")),
                new UiJuice.Hint("Enter", Loc.Pick("sign in", "로그인")),
                new UiJuice.Hint("Esc", Loc.Pick("look around", "둘러보기")),
            };

            var host = UiBuilder.Rect("Hints", safe, false);
            UiBuilder.Anchor(host, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-40f, 14f), new Vector2(620f, 34f));

            var bar = UiJuice.HintBar(host, hints);
            UiBuilder.Stretch(bar);

            UiJuice.PopIn(host, 0.46f, new Vector2(0f, -60f), 0.44f);
        }

        // --------------------------------------------------------------------- state

        /// <summary>
        /// Floods the screen with a colour, runs <paramref name="then"/> under it, and lifts.
        ///
        /// The same move <see cref="MainMenuView"/> makes when a row is taken, for the same
        /// reason: every exit from this screen loads the title, and a cut straight from a form
        /// to a menu is the one moment a front door has to spend something. It lifts again
        /// afterwards so that a caller who did not actually leave is not left under a sheet.
        /// </summary>
        public void Launch(Color colour, Action then)
        {
            var root = transform as RectTransform;
            if (root == null || !UiTween.MotionEnabled) { then?.Invoke(); return; }

            var sheet = UiBuilder.Image("Wash", root, UiSprites.Solid(), colour, Image.Type.Simple);
            UiBuilder.Stretch(sheet.rectTransform);
            sheet.rectTransform.SetAsLastSibling();
            var group = UiBuilder.Group(sheet.rectTransform, 0f, false, false);

            UiTween.Fade(group, 0.94f, 0.24f, Ease.InCubic, 0f, () =>
            {
                then?.Invoke();
                UiTween.Fade(group, 0f, 0.34f, Ease.OutCubic, 0.02f, () =>
                {
                    if (sheet != null) Destroy(sheet.gameObject);
                });
            });
        }

        /// <summary>Puts a sentence under the fields. Errors are red, everything else cyan.</summary>
        public void Say(string message, bool error = false)
        {
            if (_status == null) return;
            _status.text = message ?? "";
            _status.color = error ? UiPalette.AceRed : UiPalette.AceCyan;
            if (!string.IsNullOrEmpty(message)) UiTween.Punch(_status.rectTransform, 0.05f, 0.22f);
        }

        /// <summary>
        /// Dims and disconnects the buttons while a request is in flight.
        ///
        /// A form whose buttons stay live during a round trip is a form somebody presses three
        /// times; <c>OnlineSession</c> refuses the second call, so the only thing the extra
        /// presses produce is a screen that looks like it ignored them.
        /// </summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (_actions == null) return;
            _actions.interactable = !busy;
            _actions.blocksRaycasts = !busy;
            UiTween.Fade(_actions, busy ? 0.45f : 1f, 0.16f);
        }

        /// <summary>Steps the recovery question. Public so a key can drive it as well as a button.</summary>
        public void CycleQuestion(int delta)
        {
            if (_questions.Count == 0) return;
            var count = _questions.Count;
            _questionIndex = ((_questionIndex + delta) % count + count) % count;
            if (_question != null)
            {
                _question.text = CurrentQuestion();
                UiTween.Punch(_question.rectTransform, 0.06f, 0.24f);
            }
            UiSound.Navigate();
            QuestionChanged?.Invoke(_questionIndex);
        }

        /// <summary>Moves focus from the name to the answer, or into the name if nothing has it.</summary>
        public void FocusNext()
        {
            if (_name == null || _answer == null) return;
            if (_name.isFocused) _answer.Select();
            else _name.Select();
        }

        private string CurrentQuestion() =>
            _questionIndex >= 0 && _questionIndex < _questions.Count ? _questions[_questionIndex] : "";

        // ------------------------------------------------------------------- helpers

        /// <summary>
        /// One labelled group, which is also the unit the entrance animates.
        ///
        /// The block exists so the label and its control arrive together: animating them
        /// separately is how a form ends up with three labels in place above three fields still
        /// sliding in behind them.
        /// </summary>
        private static RectTransform Block(Transform column, ref float y, float height, float delay)
        {
            var block = UiBuilder.Rect("Block", column, false);
            UiBuilder.Anchor(block, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(ColumnPad, y), new Vector2(-ColumnPad * 2f, height));
            UiJuice.PopIn(block, delay, new Vector2(-160f, 0f));
            y -= height + 12f;
            return block;
        }

        /// <summary>A full-width rect inside a block, <paramref name="top"/> down from its top.</summary>
        private static RectTransform Slot(Transform block, float top, float height)
        {
            var slot = UiBuilder.Rect("Slot", block, false);
            UiBuilder.Anchor(slot, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -top), new Vector2(0f, height));
            return slot;
        }

        private static void Label(Transform block, float y, string text)
        {
            var label = UiBuilder.Text("Label", block, text, UiTextRole.Overline,
                UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(2f, y), new Vector2(0f, 28f));
        }

        /// <summary>A hairline with a word set into it, so the alternative reads as one.</summary>
        private static float Divider(Transform column, float y, string word)
        {
            var row = UiBuilder.Rect("Divider", column, false);
            UiBuilder.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(ColumnPad, y), new Vector2(-ColumnPad * 2f, 28f));

            var left = UiBuilder.Image("Left", row, UiSprites.Solid(),
                new Color(1f, 1f, 1f, 0.14f), Image.Type.Simple);
            UiBuilder.Anchor(left.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(-46f, 2f));

            var right = UiBuilder.Image("Right", row, UiSprites.Solid(),
                new Color(1f, 1f, 1f, 0.14f), Image.Type.Simple);
            UiBuilder.Anchor(right.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), Vector2.zero, new Vector2(-46f, 2f));

            var label = UiBuilder.Text("Word", row, word, UiTextRole.Caption,
                UiPalette.AceTextFaint, TextAlignmentOptions.Center);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(84f, 28f));

            return y - 40f;
        }

        /// <summary>
        /// A text field, built by hand because this project has no UI prefabs at all — every
        /// screen is constructed from <see cref="UiBuilder"/> at runtime, and one prefab
        /// dependency here would be the single thing in the UI a reimport could break.
        /// </summary>
        private static TMP_InputField Field(RectTransform host, string value, string placeholder)
        {
            var well = Well("Well", host, out var rim);
            // Every generated Image is built with raycasts off; a Selectable whose target
            // graphic cannot be hit is a field that never takes focus when it is clicked.
            well.raycastTarget = true;

            // Inset horizontally, barely inset vertically — and that asymmetry is the point. A
            // uniform 18px inset on a 64px field leaves a 28px viewport, and Body is 28pt: TMP
            // with overflowMode Ellipsis draws NOTHING when a line is taller than its rect, so a
            // field built that way silently swallows both its placeholder and everything the
            // player types. The first capture of this screen showed exactly that.
            var viewport = UiBuilder.Rect("Text Area", host, false);
            UiBuilder.Stretch(viewport);
            viewport.offsetMin = new Vector2(18f, 8f);
            viewport.offsetMax = new Vector2(-18f, -8f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var text = UiBuilder.Text("Text", viewport, "", UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Stretch(text.rectTransform);

            var hint = UiBuilder.Text("Placeholder", viewport, placeholder, UiTextRole.Body,
                UiPalette.AceTextFaint, TextAlignmentOptions.Left);
            UiBuilder.Stretch(hint.rectTransform);

            var input = host.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = viewport;
            input.textComponent = text;
            input.placeholder = hint;
            input.targetGraphic = well;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.text = value ?? "";

            // The field answers being clicked. Without this the only feedback a form gives is a
            // caret appearing somewhere, which is the difference between a screen that responds
            // and a screen that merely accepts.
            input.onSelect.AddListener(_ =>
            {
                Tint(rim, RimFocused);
                UiTween.Punch(host, 0.018f, 0.2f);
                UiSound.Navigate();
            });
            input.onDeselect.AddListener(_ => Tint(rim, RimIdle));

            return input;
        }

        /// <summary>
        /// A recessed well: the field and question-picker background. The rim comes back as
        /// well, because a field that lights up when it takes focus needs something to light.
        /// </summary>
        private static Image Well(string name, Transform parent, out Image rim)
        {
            var fill = UiBuilder.Image(name, parent, UiSprites.Panel(14),
                UiPalette.AceNight.WithAlpha(0.45f));
            UiBuilder.Stretch(fill.rectTransform);
            rim = UiBuilder.Image("Rim", parent, UiSprites.Frame(14, 2), RimIdle);
            UiBuilder.Stretch(rim.rectTransform);
            return fill;
        }

        private static readonly Color RimIdle = UiPalette.AceRim.WithAlpha(0.10f);
        private static readonly Color RimFocused = UiPalette.AceCyan.WithAlpha(0.65f);

        /// <summary>Tweens any graphic to a colour, guarding against it having been torn down.</summary>
        private static void Tint(Graphic graphic, Color to, float seconds = 0.14f)
        {
            if (graphic == null) return;
            var target = graphic;
            UiTween.Color(target.color, to, seconds, c => { if (target != null) target.color = c; });
        }

        /// <summary>A pane, a glow when it carries an accent, and a squash before the action runs.</summary>
        private static void Button(Transform parent, string label, Color accent, Vector2 offset,
                                   Vector2 anchor, Action onClick, Vector2? size = null,
                                   Color? ink = null, bool glow = true)
        {
            var button = UiBuilder.Rect("Button_" + label, parent, false);
            var box = size ?? new Vector2(240f, 74f);
            UiBuilder.Anchor(button, anchor, anchor, anchor, offset, box);

            var lift = UiBuilder.Rect("Lift", button);

            if (glow)
            {
                var bloom = UiBuilder.Image("Glow", lift, UiSprites.Shadow(18, 28),
                    accent.WithAlpha(0.28f));
                UiBuilder.Stretch(bloom.rectTransform, -16f);
            }

            var pane = UiJuice.Pane("Pane", lift, accent, 16, false, true, true,
                accent.WithAlpha(0.5f), (int)box.y);

            // Six pixels of vertical inset, not fourteen. Body is 28pt and its line is about
            // 34px tall; on a 46px button a 14px inset leaves a 32px box and TMP draws the label
            // not at all. Small buttons are exactly where that trap fires, because they are the
            // ones nobody re-measures.
            var caption = UiBuilder.Text("Label", lift, label, UiTextRole.Body,
                ink ?? UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(caption.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-20f, -6f));

            UiBuilder.Button("Take", button, pane.Fill, () =>
            {
                UiSound.Confirm();
                UiJuice.Squash(lift, onClick);
            });
        }
    }
}
