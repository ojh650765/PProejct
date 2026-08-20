using System;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// Making an account, and signing back into one, without a password.
    ///
    /// <b>The scheme, and the user's reasoning for it.</b> A trainer name identifies the
    /// account; one of a fixed list of recovery questions, plus its answer, is what proves you
    /// are the same person on a new device. 비밀번호까지는 오바 — a game asking a player to
    /// invent and remember a password is asking for more ceremony than the thing is worth, and
    /// most of them would reuse one anyway.
    ///
    /// <b>What the screen therefore has to say out loud.</b> This is weaker than a password and
    /// the player is told so, on the screen, before they choose: a birthplace has a few thousand
    /// plausible answers and somebody who knows the trainer name can work through them. The
    /// Worker rate-limits attempts and stores only a salted hash of the answer, and nothing
    /// outside this game is ever behind it. Saying that here costs four lines and is the
    /// difference between an informed trade and a misleading one.
    ///
    /// <b>The server address lives here too.</b> The Worker is deployed to the developer's own
    /// Cloudflare account, so its hostname cannot be compiled in; without a field for it an
    /// otherwise finished build has no way to reach its own backend. It sits at the top,
    /// because nothing below it can work until it is filled in.
    ///
    /// <b>It is drawn in the same register as the title.</b> A form is the least exciting
    /// screen in any game and the one most likely to be styled as a settings dialog by
    /// accident. This one is a single pane of blue glass on the same lit navy field, with the
    /// cyan section labels, the recessed wells and the inverting buttons the rest of the front
    /// end uses — the shared kit lives in <see cref="UiJuice"/>, so making the form match the
    /// title screen is a matter of calling the same builders rather than of repeating the same
    /// colours.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AccountPanel : MonoBehaviour
    {
        /// <summary>Raised when the panel closes, so the menu behind it can redraw.</summary>
        public Action Closed;

        public bool IsOpen { get; private set; }

        private const float CardWidth = 900f;
        private const float CardHeight = 960f;
        private const float Gutter = 44f;

        private TMP_InputField _server;
        private TMP_InputField _name;
        private TMP_InputField _answer;
        private TextMeshProUGUI _question;
        private TextMeshProUGUI _status;

        private int _questionIndex;

        private void Awake() => gameObject.SetActive(false);

        public void Open()
        {
            OnlineSession.Ensure();
            gameObject.SetActive(true);
            IsOpen = true;
            UiSound.MenuOpen();
            Build();
        }

        public void Close()
        {
            IsOpen = false;
            UiSound.MenuClose();
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        private void Build()
        {
            var root = (RectTransform)transform;
            UiBuilder.ClearChildren(root);
            UiBuilder.Stretch(root);

            // Nothing the sky draws takes raycasts, so the panel needs one invisible catcher
            // underneath it or clicks land on the title screen it is covering.
            var blocker = UiBuilder.Backdrop("Blocker", root, null, new Color(0f, 0f, 0f, 0f), true);
            UiBuilder.Stretch(blocker.rectTransform);

            UiJuice.Backdrop(root, UiPalette.AceHalo, UiPalette.AceMint);

            var card = UiBuilder.Rect("Card", root, false);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CardWidth, CardHeight));

            UiJuice.Pane("Glass", card, UiPalette.AceGlass.WithAlpha(0.88f), 26, true, true, true,
                UiPalette.AceRim, 240);

            // A cyan bar along the top edge, the same mark the trainer card carries. It is the
            // one thing that says these two panes belong to the same product.
            var stripe = UiBuilder.Image("Stripe", card, UiSprites.Pill(8), UiPalette.AceCyan);
            UiBuilder.Anchor(stripe.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(-520f, 8f));

            var mark = UiJuice.Ball(card, 56f);
            UiBuilder.Anchor(mark, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f),
                new Vector2(72f, -60f), new Vector2(56f, 56f));
            UiIdle.Attach(mark, UiIdleMode.Bob, 4f, 2.4f);

            var title = UiBuilder.Text("Title", card, Loc.Pick("Account", "계정"), UiTextRole.Title,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(112f, -26f), new Vector2(-160f, 68f));

            var y = -104f;

            // --- Server -------------------------------------------------------------------
            y = Label(card, y, Loc.Pick("SERVER", "서버 주소"));
            _server = Field(card, ref y, OnlineConfig.BaseUrl,
                "https://pokelab-online.<subdomain>.workers.dev");
            y = Hint(card, y, Loc.Pick(
                "The Cloudflare Worker this build talks to. Leave blank to play story mode offline.",
                "이 빌드가 사용할 Cloudflare Worker 주소예요. 비워 두면 스토리 모드만 오프라인으로 즐겨요."));

            var session = OnlineSession.Instance;
            if (session != null && session.IsSignedIn) BuildSignedIn(card, ref y, session);
            else BuildSignedOut(card, ref y);

            _status = UiBuilder.Text("Status", card, "", UiTextRole.Secondary,
                UiPalette.AceCyan, TextAlignmentOptions.TopLeft);
            UiBuilder.Anchor(_status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(Gutter, 104f), new Vector2(-Gutter * 2f, 80f));

            BuildButton(card, Loc.Pick("Close", "닫기"), UiPalette.AceGlassLift,
                new Vector2(-Gutter, 32f), new Vector2(1f, 0f),
                () => { OnlineConfig.BaseUrl = _server.text; Close(); });

            UiJuice.PopScale(card, 0f, 0.88f, 0.4f);
        }

        private void BuildSignedOut(Transform card, ref float y)
        {
            y = Label(card, y, Loc.Pick("TRAINER NAME", "트레이너 이름"));
            _name = Field(card, ref y, PlayerPrefs.GetString("pokelab.online.trainerName", ""),
                Loc.Pick("2-16 characters", "2~16자"));

            y = Label(card, y, Loc.Pick("RECOVERY QUESTION", "본인 확인 질문"));

            var row = UiBuilder.Rect("QuestionRow", card, false);
            UiBuilder.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(Gutter, y), new Vector2(-Gutter * 2f, 64f));

            Well("Well", row);

            _question = UiBuilder.Text("Question", row, CurrentQuestionPrompt(), UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_question.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
            _question.rectTransform.offsetMin = new Vector2(22f, 6f);
            _question.rectTransform.offsetMax = new Vector2(-146f, -6f);

            BuildButton(row, "‹", UiPalette.AceCyan.WithAlpha(0.28f),
                new Vector2(-78f, 0f), new Vector2(1f, 0.5f), () => CycleQuestion(-1),
                new Vector2(58f, 52f));
            BuildButton(row, "›", UiPalette.AceCyan.WithAlpha(0.28f),
                new Vector2(-12f, 0f), new Vector2(1f, 0.5f), () => CycleQuestion(1),
                new Vector2(58f, 52f));

            y -= 80f;

            y = Label(card, y, Loc.Pick("ANSWER", "답"));
            _answer = Field(card, ref y, "", Loc.Pick("Your answer", "답을 입력하세요"));

            // The lecture that used to stand here -- 다른 곳에서 쓰는 답은 사용하지 마세요, plus
            // two lines explaining that this is weaker than a password -- is gone at the
            // user's instruction. The same sentence was cut from LoginView in the same pass,
            // so both screens ask the question and neither explains itself.

            y -= 12f;

            BuildButton(card, Loc.Pick("Create account", "계정 만들기"), UiPalette.AceLime.WithAlpha(0.9f),
                new Vector2(Gutter, y), new Vector2(0f, 1f),
                () => Submit(true), new Vector2(384f, 74f), UiPalette.AceInk);
            BuildButton(card, Loc.Pick("Sign in", "로그인"), UiPalette.AceCyan.WithAlpha(0.9f),
                new Vector2(-Gutter, y), new Vector2(1f, 1f),
                () => Submit(false), new Vector2(384f, 74f), UiPalette.AceInk);
        }

        private void BuildSignedIn(Transform card, ref float y, OnlineSession session)
        {
            y = Label(card, y, Loc.Pick("SIGNED IN AS", "로그인 계정"));

            var name = UiBuilder.Text("Name", card, session.TrainerName, UiTextRole.Title,
                UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(Gutter, y), new Vector2(-Gutter * 2f, 68f));
            y -= 82f;

            y = Hint(card, y, session.HasTeam
                ? Loc.Pick($"Team of {session.Roster.Length} drawn.",
                           $"{session.Roster.Length}마리로 팀이 편성되어 있어요.")
                : Loc.Pick("No team drawn yet. Open Gacha from the title screen.",
                           "아직 팀이 없어요. 타이틀 화면의 가챠에서 뽑아 주세요."));

            y -= 20f;

            BuildButton(card, Loc.Pick("Sign out", "로그아웃"), UiPalette.AceRed.WithAlpha(0.9f),
                new Vector2(Gutter, y), new Vector2(0f, 1f),
                () =>
                {
                    // Signing out only forgets the token on this device — the account, its
                    // roster and its levels are on the server and come back with the question.
                    OnlineSession.Instance.SignOut();
                    Build();
                    Say(Loc.Pick("Signed out on this device.", "이 기기에서 로그아웃했어요."));
                },
                new Vector2(384f, 74f));
        }

        // --- Layout helpers -------------------------------------------------------------------

        private static float Label(Transform card, float y, string text)
        {
            var label = UiBuilder.Text("Label_" + text, card, text, UiTextRole.Overline,
                UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(Gutter, y), new Vector2(-Gutter * 2f, 28f));
            return y - 32f;
        }

        private static float Hint(Transform card, float y, string text)
        {
            var hint = UiBuilder.Text("Hint", card, text, UiTextRole.Caption,
                UiPalette.AceTextDim, TextAlignmentOptions.TopLeft);
            UiBuilder.Anchor(hint.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(Gutter, y), new Vector2(-Gutter * 2f, 60f));
            return y - 66f;
        }

        /// <summary>
        /// A text field.
        ///
        /// Built by hand rather than from a prefab because this project has no UI prefabs at
        /// all — every screen in it is constructed from <see cref="UiBuilder"/> at runtime, and
        /// a single prefab dependency here would be the one thing in the UI that a level
        /// rebuild could break.
        ///
        /// The well is darker than the pane it sits on and rimmed at a lower contrast than a
        /// button: on a card made of translucent glass, "pressed in" has to be read from value
        /// alone, and a field styled like everything else turns a form into a row of buttons.
        /// </summary>
        private static TMP_InputField Field(Transform card, ref float y, string value,
                                            string placeholder)
        {
            var host = UiBuilder.Rect("Field", card, false);
            UiBuilder.Anchor(host, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(Gutter, y), new Vector2(-Gutter * 2f, 64f));

            var well = Well("Well", host);
            // Every generated Image is built with raycasts off; a Selectable whose target
            // graphic cannot be hit is a field that never takes focus when it is clicked.
            well.raycastTarget = true;

            var viewport = UiBuilder.Rect("Text Area", host, false);
            // 18 horizontally, 8 vertically — NOT a uniform 18.
            //
            // The well is 64px and Body is 28pt, which lays out at roughly 34px. A uniform 18px
            // inset leaves a 28px viewport, the line does not fit, and UiType's
            // overflowMode = Ellipsis makes TMP draw NOTHING rather than clip — so the
            // placeholder and everything the player typed were both invisible. The field looked
            // like it was ignoring the keyboard. Found on a capture of the login screen, which
            // is built from this same helper.
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

            y -= 80f;
            return input;
        }

        /// <summary>A recessed well: the field and question-picker background.</summary>
        private static Image Well(string name, Transform parent)
        {
            var fill = UiBuilder.Image(name, parent, UiSprites.Panel(14),
                UiPalette.AceNight.WithAlpha(0.45f));
            UiBuilder.Stretch(fill.rectTransform);
            var rim = UiBuilder.Image("Rim", parent, UiSprites.Frame(14, 2),
                UiPalette.AceRim.WithAlpha(0.10f));
            UiBuilder.Stretch(rim.rectTransform);
            return fill;
        }

        /// <summary>
        /// A button: a pane, a glow when it carries an accent, and a squash on the press before
        /// the action runs.
        /// </summary>
        private static void BuildButton(Transform parent, string label, Color accent,
                                        Vector2 offset, Vector2 anchor, Action onClick,
                                        Vector2? size = null, Color? ink = null)
        {
            var button = UiBuilder.Rect("Button_" + label, parent, false);
            var box = size ?? new Vector2(240f, 74f);
            UiBuilder.Anchor(button, anchor, anchor, anchor, offset, box);

            var lift = UiBuilder.Rect("Lift", button);

            var glow = UiBuilder.Image("Glow", lift, UiSprites.Shadow(18, 28), accent.WithAlpha(0.28f));
            UiBuilder.Stretch(glow.rectTransform, -16f);

            var pane = UiJuice.Pane("Pane", lift, accent, 16, false, true, true,
                accent.WithAlpha(0.5f), (int)box.y);

            var caption = UiBuilder.Text("Label", lift, label, UiTextRole.Body,
                ink ?? UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(caption.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-20f, -14f));

            UiBuilder.Button("Take", button, pane.Fill, () =>
            {
                UiSound.Confirm();
                UiJuice.Squash(lift, onClick);
            });
        }

        // --- Behaviour ------------------------------------------------------------------------

        private string CurrentQuestionPrompt() =>
            SecurityQuestions.PromptFor(SecurityQuestions.All[_questionIndex].Id,
                                        Loc.Language == Language.Korean);

        private void CycleQuestion(int delta)
        {
            var count = SecurityQuestions.All.Length;
            _questionIndex = ((_questionIndex + delta) % count + count) % count;
            if (_question != null)
            {
                _question.text = CurrentQuestionPrompt();
                UiTween.Punch(_question.rectTransform, 0.06f, 0.24f);
            }
            UiSound.Navigate();
        }

        private void Submit(bool create)
        {
            OnlineConfig.BaseUrl = _server != null ? _server.text : "";

            if (!OnlineConfig.IsConfigured)
            {
                UiSound.Error();
                Say(Loc.Pick("Fill in the server address first.", "먼저 서버 주소를 입력해 주세요."));
                return;
            }

            var session = OnlineSession.Ensure();
            if (session.Busy) return;

            var trainerName = _name != null ? _name.text : "";
            var answer = _answer != null ? _answer.text : "";
            var questionId = SecurityQuestions.All[_questionIndex].Id;

            Say(create
                ? Loc.Pick("Creating account…", "계정을 만드는 중…")
                : Loc.Pick("Signing in…", "로그인 중…"));

            var routine = create
                ? session.CreateAccount(trainerName, questionId, answer, OnResult)
                : session.SignIn(trainerName, questionId, answer, OnResult);

            StartCoroutine(routine);

            void OnResult(bool ok)
            {
                if (ok)
                {
                    Build();
                    Say(create
                        ? Loc.Pick("Account created. Draw your team from the title screen.",
                                   "계정을 만들었어요. 타이틀 화면의 가챠에서 팀을 뽑아 주세요.")
                        : Loc.Pick("Signed in.", "로그인했어요."));
                    return;
                }

                UiSound.Error();
                Say(ExplainLocal(session.LastError));
            }
        }

        /// <summary>
        /// The two failures the client itself produces, plus everything the server can say.
        ///
        /// Kept apart from <see cref="OnlineClient.Explain"/> because these two never leave the
        /// device — they are the field validation, and putting them in the shared table would
        /// imply the server can return them.
        /// </summary>
        private static string ExplainLocal(string error)
        {
            switch (error)
            {
                case "bad_name":
                    return Loc.Pick("A trainer name is 2-16 characters.",
                                    "트레이너 이름은 2~16자여야 해요.");
                case "bad_answer":
                    return Loc.Pick("Choose a question and type an answer.",
                                    "질문을 고르고 답을 입력해 주세요.");
                default:
                    return OnlineClient.Explain(error);
            }
        }

        private void Say(string message)
        {
            if (_status != null) _status.text = message ?? "";
        }
    }
}
