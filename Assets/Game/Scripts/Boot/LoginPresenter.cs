using System.Collections.Generic;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot
{
    /// <summary>
    /// The login screen's brain: what the fields are asked for, what the server said, and where
    /// the player goes next.
    ///
    /// <b>Why the game now opens here.</b> Saving is on the server. The build target is WebGL,
    /// where <c>Application.persistentDataPath</c> is an IndexedDB store the browser discards
    /// whenever the player clears their browsing data — so a local save is not a save, and an
    /// account is the only thing that actually keeps a game. A screen that says so on the way
    /// in is the honest place to ask; a row three deep in the title menu, which is where
    /// <see cref="AccountPanel"/> lives, is not.
    ///
    /// <b>It never becomes a wall.</b> Two of the three exits from this screen skip it: a
    /// player who is already signed in is sent straight on without ever seeing it, and
    /// 계정 없이 둘러보기 leads to the title with the cost written underneath it. The one thing
    /// a first screen must not do is hold a player who only wants to look at the game.
    ///
    /// In PokeLab.Boot rather than PokeLab.UI because it drives <see cref="OnlineSession"/> and
    /// loads scenes, and the UI assembly can see neither.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LoginPresenter : MonoBehaviour
    {
        private const int SortingOrder = 500;

        [Tooltip("Where every exit from this screen leads. The title, in every case.")]
        [SerializeField] private string _titleScene = "MainMenu";

        private LoginView _view;
        private bool _leaving;

        /// <summary>
        /// Set the moment an exit animation starts, which is a quarter of a second before
        /// <see cref="Leave"/> actually runs. <c>_leaving</c> alone could not cover that window:
        /// it is the guard <c>Leave</c> itself checks, so setting it early would have made the
        /// wash finish and then refuse to load anything.
        /// </summary>
        private bool _exiting;

        private void Start()
        {
            var session = OnlineSession.Ensure();

            // Already signed in: the token was restored from PlayerPrefs in the session's own
            // Awake, so this is settled before the first frame is drawn and there is nothing to
            // wait for. Building the screen and then dismissing it would be a flash of a form
            // the player never had to fill in.
            if (session.IsSignedIn)
            {
                Leave();
                return;
            }

            BuildCanvas();

            // The same piece the title plays, started here rather than there.
            //
            // These two screens are one front door with a form on the first page: the player
            // crosses from one to the other in a second, and PlayTrack is a no-op when the
            // track is already the one playing, so asking for it in both places means it
            // starts once and runs straight through the transition. Doing it only on the title
            // left this screen on whatever the world clock had most recently asked for.
            FrontendAudio.TakeOver(PokeLab.Audio.AudioIds.MusicTitle);
        }

        private void BuildCanvas()
        {
            UiBuilder.EnsureEventSystem();

            var host = new GameObject("LoginCanvas", typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler),
                typeof(UnityEngine.UI.GraphicRaycaster));
            host.transform.SetParent(transform, false);
            UiBuilder.ConfigureCanvas(host.GetComponent<Canvas>(), SortingOrder);

            var screen = new GameObject("Login", typeof(RectTransform));
            screen.transform.SetParent(host.transform, false);

            _view = screen.AddComponent<LoginView>();
            _view.Submitted = Submit;
            _view.Skipped = SkipAccount;
            _view.ServerCommitted = value => OnlineConfig.BaseUrl = value;

            UiSound.MenuOpen();

            _view.Build(
                Loc.Pick("POKÉ LAB", "포켓랩"),
                Loc.Pick("Sign in", "로그인 / 회원가입"),
                QuestionPrompts(),
                OnlineConfig.BaseUrl,
                PlayerPrefs.GetString("pokelab.online.trainerName", ""),
                ServerOverrideAllowed);
        }

        /// <summary>
        /// Whether the login screen draws its server-address control.
        ///
        /// Editor and development builds only. <see cref="OnlineConfig"/> ships with the
        /// deployed Worker compiled in, so the field can no longer be the thing that makes a
        /// build work — it is purely a way to point a developer at a local <c>wrangler dev</c>
        /// or a second deployment. On the front door of a shipped game it is a control that
        /// cannot help and can only raise a question, which is why this is a compile-time
        /// constant rather than a setting: in a player the whole block is not in the binary.
        /// </summary>
        private static bool ServerOverrideAllowed =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            true;
#else
            false;
#endif

        /// <summary>
        /// The recovery prompts in the player's language.
        ///
        /// Resolved here rather than in the view because <see cref="SecurityQuestions"/> lives
        /// in PokeLab.Online, which PokeLab.UI does not reference — the same split that keeps
        /// <see cref="MainMenuView"/> from knowing what an account is.
        /// </summary>
        private static List<string> QuestionPrompts()
        {
            var korean = Loc.Language == Language.Korean;
            var prompts = new List<string>(SecurityQuestions.All.Length);
            foreach (var question in SecurityQuestions.All)
                prompts.Add(SecurityQuestions.PromptFor(question.Id, korean));
            return prompts;
        }

        // --- Doing things -------------------------------------------------------------------

        private void Submit(bool create)
        {
            if (_leaving || _exiting) return;

            // Practically unreachable now that OnlineConfig falls back to the compiled default,
            // and kept as a guard rather than a prompt: with the server control gone from a
            // shipped build, "먼저 서버 주소를 입력해 주세요" would be asking the player for
            // something the screen gives them no way to provide.
            if (!OnlineConfig.IsConfigured)
            {
                UiSound.Error();
                _view.Say(OnlineClient.Explain("no_server"), true);
                return;
            }

            var session = OnlineSession.Ensure();
            if (session.Busy) return;

            var trainerName = _view.TrainerName;
            var answer = _view.Answer;
            var index = Mathf.Clamp(_view.QuestionIndex, 0, SecurityQuestions.All.Length - 1);
            var questionId = SecurityQuestions.All[index].Id;

            _view.SetBusy(true);
            _view.Say(create
                ? Loc.Pick("Creating account…", "계정을 만드는 중…")
                : Loc.Pick("Signing in…", "로그인 중…"));

            StartCoroutine(create
                ? session.CreateAccount(trainerName, questionId, answer, OnResult)
                : session.SignIn(trainerName, questionId, answer, OnResult));

            void OnResult(bool ok)
            {
                _view.SetBusy(false);

                if (ok)
                {
                    _view.Say(create
                        ? Loc.Pick("Account created.", "계정을 만들었어요.")
                        : Loc.Pick("Signed in.", "로그인했어요."));
                    // Lime, because lime is what this front end means by "yes". The wash is a
                    // beat of payoff on the one press that matters most on this screen, and it
                    // covers the scene load rather than the load interrupting it.
                    _exiting = true;
                    _view.Launch(UiPalette.AceLime, Leave);
                    return;
                }

                UiSound.Error();
                _view.Say(Explain(session.LastError), true);
            }
        }

        /// <summary>
        /// On to the title without an account, which the screen has already said costs saving.
        ///
        /// No confirmation. The line under the button is the warning, and a dialog on top of a
        /// sentence the player has just read is a second copy of it, not a safeguard — nothing
        /// is destroyed here and the screen is one row away on the title.
        /// </summary>
        private void SkipAccount()
        {
            if (_leaving || _exiting) return;
            _exiting = true;
            UiSound.Cancel();
            _view.Launch(UiPalette.AceGlassLift, Leave);
        }

        private void Leave()
        {
            if (_leaving) return;
            _leaving = true;
            SceneManager.LoadScene(_titleScene, LoadSceneMode.Single);
        }

        /// <summary>
        /// The player-facing sentence for a failure.
        ///
        /// Everything the server can say is <see cref="OnlineClient.Explain"/>'s, and the two
        /// codes the client produces itself are spelled the way <c>AccountPanel.ExplainLocal</c>
        /// spells them — deliberately word for word, because two screens that reject the same
        /// name for the same reason must not phrase it two ways. That method is private to the
        /// panel; making it shared would be better than this copy and is the integrator's call,
        /// not this file's.
        /// </summary>
        private static string Explain(string error)
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

        // --- Input --------------------------------------------------------------------------

        /// <summary>
        /// The two keys the hint bar promises.
        ///
        /// Tab is handled here rather than left to uGUI's own navigation because the default
        /// input actions bind Navigate to the arrows and the stick, not to Tab — so without
        /// this the hint would be a lie. Enter is the input field's own submit and is wired in
        /// the view, where the field that owns it is.
        /// </summary>
        private void Update()
        {
            if (_leaving || _exiting || _view == null) return;

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.tabKey.wasPressedThisFrame) _view.FocusNext();
            if (keyboard.escapeKey.wasPressedThisFrame) SkipAccount();
        }
    }
}
