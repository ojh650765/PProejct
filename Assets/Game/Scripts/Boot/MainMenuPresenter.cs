using System.Collections.Generic;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.Overworld;
using PokeLab.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot
{
    /// <summary>
    /// The title screen's brain: what the rows say, whether each one is open, and where each
    /// one goes.
    ///
    /// <b>Why the game needed one.</b> The build started on Town.unity — <c>GameBuilder</c>
    /// promoted it to first scene explicitly — so a player opened the game already standing in
    /// the plaza, mid-save, with no way to start over and nowhere else to go. That was fine
    /// while the story was the only thing here. It stopped being fine the moment there was a
    /// second mode to choose, an account to sign into and a team to draw.
    ///
    /// <b>Every row is honest about its own state.</b> A mode that needs something the player
    /// does not have yet is drawn disabled with the reason underneath it — no server
    /// configured, not signed in, no team drawn — rather than being offered and then failing.
    /// The one thing a title screen must never do is accept a press and do nothing.
    ///
    /// In PokeLab.Boot because it reads the save, the profile and the online session and builds
    /// PokeLab.UI widgets, and those assemblies cannot see each other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuPresenter : MonoBehaviour
    {
        private const int SortingOrder = 500;

        /// <summary>Which list of rows is on screen. A press of Escape walks back up this.</summary>
        private enum Page { Root, Battle }

        [Tooltip("The scene the story begins in. The town is the game's first playable band.")]
        [SerializeField] private string _storyScene = "Town";

        private MainMenuView _view;
        private AccountPanel _account;
        private GachaPanel _gacha;
        private SettingsPanel _settings;
        private MatchmakingPanel _matchmaking;
        private Canvas _canvas;

        private Page _page = Page.Root;
        private readonly List<System.Action> _actions = new List<System.Action>();

        /// <summary>Set while a confirmation is on screen, so a stray Enter cannot answer it.</summary>
        private bool _confirming;
        private float _repeatCooldown;

        [Tooltip("The title screen's own music. Empty leaves whatever the music director " +
                 "would have chosen for a scene with no biome in it.")]
        [SerializeField] private string _titleTrack = PokeLab.Audio.AudioIds.MusicTitle;

        private void Start()
        {
            var session = OnlineSession.Ensure();
            session.Changed += Refresh;
            BuildCanvas();
            Refresh();
            PlayTitleMusic();

            // Ask whether this account has a story to continue. /save/info is the description
            // WITHOUT the payload, so opening the title screen costs a few hundred bytes rather
            // than a whole save file. The answer raises Changed, which redraws the rows — so
            // 이어하기 appears when it is real, and never appears at all when it is not.
            if (session.IsSignedIn) StartCoroutine(session.FetchSaveInfo(null));
        }

        /// <summary>
        /// Gives the title screen a track of its own.
        ///
        /// Without this it had one anyway, and that was the problem: <c>AvPresenterHost</c>
        /// stands a <c>MusicDirector</c> up in every scene, the director picks an exploration
        /// track from the biome and the time of day, and a menu scene has no biome — so the
        /// title screen opened on the town's daytime theme. The first thing the player hears is
        /// the music of a place they have not arrived at yet, and it is the same loop again
        /// thirty seconds later when they do.
        ///
        /// The attract piece is the default because it is the one written to play before the
        /// game has started rather than inside it. It is deliberately NOT the prologue's track:
        /// that one is the professor's monologue and the player hears it within a minute of
        /// pressing 스토리 모드, so sharing them would make the title feel like a loading
        /// screen for the same cue. Serialized rather than constant so it can be swapped
        /// without a recompile.
        /// </summary>
        private void PlayTitleMusic() => FrontendAudio.TakeOver(_titleTrack);

        private void OnDestroy()
        {
            if (OnlineSession.Instance != null) OnlineSession.Instance.Changed -= Refresh;
        }

        private void BuildCanvas()
        {
            UiBuilder.EnsureEventSystem();

            var host = new GameObject("MainMenuCanvas", typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler),
                typeof(UnityEngine.UI.GraphicRaycaster));
            host.transform.SetParent(transform, false);

            _canvas = UiBuilder.ConfigureCanvas(host.GetComponent<Canvas>(), SortingOrder);

            var screen = new GameObject("Menu", typeof(RectTransform));
            screen.transform.SetParent(host.transform, false);
            _view = screen.AddComponent<MainMenuView>();
            _view.Chosen = Take;

            var accountHost = new GameObject("Account", typeof(RectTransform));
            accountHost.transform.SetParent(host.transform, false);
            _account = accountHost.AddComponent<AccountPanel>();
            _account.Closed = () => { Refresh(); };

            var gachaHost = new GameObject("Gacha", typeof(RectTransform));
            gachaHost.transform.SetParent(host.transform, false);
            _gacha = gachaHost.AddComponent<GachaPanel>();
            _gacha.Closed = () => { Refresh(); };

            var settingsHost = new GameObject("Settings", typeof(RectTransform));
            settingsHost.transform.SetParent(host.transform, false);
            _settings = settingsHost.AddComponent<SettingsPanel>();
            _settings.Closed = () => { Refresh(); };

            var matchHost = new GameObject("Matchmaking", typeof(RectTransform));
            matchHost.transform.SetParent(host.transform, false);
            _matchmaking = matchHost.AddComponent<MatchmakingPanel>();
            _matchmaking.Closed = () => { Refresh(); };
            // The panel closes WITHOUT cancelling the queue on this path — the match is the
            // thing we were queuing for, and BattleModeLauncher reads it straight off
            // PvpSession.
            _matchmaking.Confirmed = () => BattleModeLauncher.Launch("pvp");
        }

        // --- The rows -----------------------------------------------------------------------

        private void Refresh()
        {
            if (_view == null) return;

            _actions.Clear();
            var rows = _page == Page.Root ? BuildRootRows() : BuildBattleRows();

            _view.Build(
                Loc.Pick("POKÉ LAB", "포켓랩"),
                _page == Page.Root
                    ? Loc.Pick("Aster Field", "아스터 필드")
                    : Loc.Pick("Battle", "대전 모드"),
                rows);

            _view.SetCard(CardName(), CardStatus());
            _view.SetTeam(BuildTeamSlots());
            _view.SetFooter(Loc.Pick(
                "↑↓ move    Enter select    Esc back",
                "↑↓ 이동    Enter 선택    Esc 뒤로"));
        }

        private List<MainMenuView.Entry> BuildRootRows()
        {
            var rows = new List<MainMenuView.Entry>();

            // Whether there is a story to continue is now the SERVER's answer, not the disk's.
            // Local saving is gone — the target is WebGL, where persistentDataPath is IndexedDB
            // and is wiped whenever the player clears browsing data. HasCloudSave is a cache
            // filled by FetchSaveInfo in Start; false until it answers, so the row appears when
            // the answer arrives rather than flickering out when it does.
            var hasSave = OnlineSession.Instance != null && OnlineSession.Instance.HasCloudSave;

            // COLLAPSED, not disabled — the user's call, and it is the right one. A greyed-out
            // 이어하기 saying "저장된 게임이 없어요" is a row that exists only to report its own
            // uselessness, and a brand new player meets it before anything else on the screen.
            // Omitting the entry removes it from the layout entirely, exactly as Unreal's
            // Collapsed does: MainMenuView lays its rows out in a VerticalLayoutGroup, so what
            // is not added takes no space and everything below simply moves up.
            //
            // The rows and _actions are index-aligned by construction — each row appends its
            // own action immediately after it — so skipping a row skips its action too and the
            // pairing survives.
            if (hasSave)
            {
                rows.Add(new MainMenuView.Entry(
                    Loc.Pick("Continue", "이어하기"),
                    Loc.Pick("Pick up where you left off.", "저장한 곳에서 이어서 시작해요."),
                    UiPalette.AceCyan));
                _actions.Add(() => LoadStory(false));
            }

            // One line, the same whether or not a save exists.
            //
            // It used to warn about erasure right here — "저장된 게임은 지워져요" under the row
            // — and the user asked for that removed. They are right, and the reason is worth
            // keeping: a warning printed under a row the player has not pressed is a warning
            // read at the wrong moment. It is noise while they are choosing and forgotten by
            // the time it matters. The confirmation below is where that sentence belongs,
            // because that is the instant it is actually about to happen.
            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Story", "스토리 모드"),
                Loc.Pick("Begin in Aster Town.", "아스터 마을에서 시작해요."),
                UiPalette.AceLime));
            _actions.Add(() => { if (hasSave) ConfirmNewGame(); else LoadStory(true); });

            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Battle", "대전 모드"),
                Loc.Pick("Fight the AI, or another trainer online.",
                         "AI와 겨루거나, 다른 트레이너와 온라인으로 겨뤄요."),
                UiPalette.AceGold));
            _actions.Add(() => { _page = Page.Battle; Refresh(); });

            var online = OnlineConfig.IsConfigured;
            var signedIn = OnlineSession.Instance != null && OnlineSession.Instance.IsSignedIn;

            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Gacha", "가챠"),
                // Same rule as the battle rows: not signed in opens 계정 rather than refusing.
                // Only "no server" stays dead, because nothing in a shipped build can fix it.
                !online
                    ? Loc.Pick("No server configured.", "서버가 설정되지 않았어요.")
                    : !signedIn
                        ? Loc.Pick("Sign in to draw. Opens the account screen.",
                                   "로그인이 필요해요. 계정 화면으로 가요.")
                        : OnlineSession.Instance.HasTeam
                            ? Loc.Pick("Your team is drawn. Look at it, or draw again.",
                                       "팀이 완성되어 있어요. 확인하거나 다시 뽑을 수 있어요.")
                            : Loc.Pick("Draw your team of six.", "여섯 마리를 뽑아 팀을 만들어요."),
                UiPalette.AceViolet, online));
            _actions.Add(() =>
            {
                var session = OnlineSession.Instance;
                if (session == null || !session.IsSignedIn) OpenAccount();
                else OpenGacha();
            });

            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Account", "계정"),
                signedIn
                    ? Loc.Pick("Signed in. Sign out or change server.",
                               "로그인되어 있어요. 로그아웃하거나 서버를 바꿀 수 있어요.")
                    : Loc.Pick("Create an account, or sign in.", "계정을 만들거나 로그인해요."),
                UiPalette.AceMint));
            _actions.Add(OpenAccount);

            // Always available, and never disabled. The build ships at 50% master volume, so
            // this is the only way a player can turn the game back up — a settings row that
            // could be greyed out would sometimes trap them at a volume somebody else chose.
            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Settings", "설정"),
                Loc.Pick("Volume and screen effects.", "소리와 화면 효과를 조절해요."),
                UiPalette.AceMint));
            _actions.Add(OpenSettings);

            if (CanQuit)
            {
                rows.Add(new MainMenuView.Entry(
                    Loc.Pick("Quit", "종료"),
                    Loc.Pick("Close the game.", "게임을 종료해요."),
                    UiPalette.AceRed));
                _actions.Add(Quit);
            }

            return rows;
        }

        private List<MainMenuView.Entry> BuildBattleRows()
        {
            var rows = new List<MainMenuView.Entry>();
            var online = OnlineConfig.IsConfigured;
            var session = OnlineSession.Instance;
            var signedIn = session != null && session.IsSignedIn;
            var hasTeam = signedIn && session.HasTeam;

            // A row that cannot do its job TAKES YOU TO WHAT WOULD UNBLOCK IT, rather than
            // going grey and telling you to go and do it yourself.
            //
            // The user's call, and it is the better rule: "먼저 팀을 뽑아 주세요" on a dead row
            // is an instruction the player then has to carry back up the menu and follow by
            // hand, and every step of that is a chance to give up. Pressing 대전 with no team
            // should open the gacha — the player asked to battle, and drawing a team is simply
            // the first move of doing that.
            //
            // Only ONE state stays disabled: no server. Nothing inside the game can fix that
            // (the address field is debug-only in a shipped build), so a row that led somewhere
            // would be lying about it.
            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Free Battle (AI)", "자유 대전 (AI)"),
                !signedIn
                    ? Loc.Pick("Sign in to play. Opens the account screen.",
                               "로그인이 필요해요. 계정 화면으로 가요.")
                    : !hasTeam
                        ? Loc.Pick("Draw a team first. Opens the gacha.",
                                   "먼저 팀이 필요해요. 가챠로 가요.")
                        : Loc.Pick("Your gacha team against the computer. Everyone earns experience.",
                                   "뽑은 팀으로 컴퓨터와 겨뤄요. 경험치를 얻어요."),
                UiPalette.AceGold, online));
            _actions.Add(() => EnterBattle("ai"));

            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Online Battle", "온라인 대전"),
                !online
                    ? Loc.Pick("No server configured.", "서버가 설정되지 않았어요.")
                    : !signedIn
                        ? Loc.Pick("Sign in to play. Opens the account screen.",
                                   "로그인이 필요해요. 계정 화면으로 가요.")
                        : !hasTeam
                            ? Loc.Pick("Draw a team first. Opens the gacha.",
                                       "먼저 팀이 필요해요. 가챠로 가요.")
                            : Loc.Pick("Find another trainer and battle them.",
                                       "다른 트레이너를 찾아 대전해요."),
                UiPalette.AceLime, online));
            _actions.Add(() => EnterBattle("pvp"));

            rows.Add(new MainMenuView.Entry(
                Loc.Pick("Back", "뒤로"),
                Loc.Pick("Return to the title.", "타이틀로 돌아가요."),
                UiPalette.AceTextFaint));
            _actions.Add(GoBack);

            return rows;
        }

        /// <summary>
        /// The six cells on the trainer card, from whatever the account actually owns.
        ///
        /// Null when there is no team, and that is not the same as six blanks by accident: the
        /// view draws empty sockets for null deliberately, and empty sockets are the honest
        /// guest state — they say "this is where your six go", which is the screen's own
        /// argument for the 가챠 row sitting below them.
        ///
        /// Health is a flat 1. The roster carries level and experience and nothing damages a
        /// creature outside a battle, so a bar drawn at anything else would be inventing a
        /// number. The experience fraction IS real.
        /// </summary>
        private List<MainMenuView.TeamSlot> BuildTeamSlots()
        {
            var session = OnlineSession.Instance;
            if (session == null || !session.HasTeam) return null;

            var slots = new List<MainMenuView.TeamSlot>(6);
            foreach (var entry in session.Roster)
            {
                if (entry == null) continue;
                slots.Add(new MainMenuView.TeamSlot(
                    SpeciesName(entry.speciesId),
                    entry.level,
                    1f,
                    ExperienceFraction(entry.level, entry.experience),
                    RarityColour(entry.rarity)));
            }

            return slots;
        }

        private static string SpeciesName(int speciesId) =>
            ServiceHub.TryGet<ISpeciesRegistry>(out var species) && species.TryGet(speciesId, out var data)
                ? data.DisplayName
                : "#" + speciesId;

        /// <summary>
        /// How far through its current level a creature is, on the engine's own curve.
        ///
        /// Experience for a level is level cubed (see PokeLab.Battle.StatMath), so the fraction
        /// is (exp - L^3) / ((L+1)^3 - L^3). This is the THIRD copy of that formula — the engine
        /// has it, the Worker restates it in TypeScript because it settles battles against it,
        /// and the UI needs it without being able to reference either. Every copy carries this
        /// note; change one and change all three, or a bar will disagree with the level beside it.
        /// </summary>
        private static float ExperienceFraction(int level, int experience)
        {
            if (level >= 100) return 1f;
            var floor = (long)level * level * level;
            var ceiling = (long)(level + 1) * (level + 1) * (level + 1);
            if (ceiling <= floor) return 0f;
            return Mathf.Clamp01((experience - floor) / (float)(ceiling - floor));
        }

        /// <summary>The rarity's identity colour, matching the gacha reveal's own table.</summary>
        private static Color RarityColour(string rarity)
        {
            switch (rarity)
            {
                case "legendary": return UiPalette.AceGold;
                case "epic": return UiPalette.AceViolet;
                case "rare": return UiPalette.AceCyan;
                case "uncommon": return UiPalette.AceLime;
                default: return UiPalette.AceMint;
            }
        }

        private string CardName()
        {
            var session = OnlineSession.Instance;
            if (session != null && session.IsSignedIn && !string.IsNullOrEmpty(session.TrainerName))
                return session.TrainerName;
            return Loc.Pick("Guest", "게스트");
        }

        private string CardStatus()
        {
            var session = OnlineSession.Instance;

            if (!OnlineConfig.IsConfigured)
                return Loc.Pick(
                    "Offline build.\nStory mode works. Set a server address under Account to\nunlock gacha and online battles.",
                    "오프라인 빌드예요.\n스토리 모드는 그대로 즐길 수 있어요. 계정에서 서버 주소를\n설정하면 가챠와 온라인 대전이 열려요.");

            if (session == null || !session.IsSignedIn)
                return Loc.Pick("Not signed in.\nGacha and online battles need an account.",
                                "로그인하지 않았어요.\n가챠와 온라인 대전에는 계정이 필요해요.");

            if (!session.HasTeam)
                return Loc.Pick("Signed in. No team drawn yet.\nOpen Gacha to draw six.",
                                "로그인됨. 아직 팀이 없어요.\n가챠에서 여섯 마리를 뽑아 주세요.");

            var levels = 0;
            foreach (var entry in session.Roster) if (entry != null) levels += entry.level;
            var average = session.Roster.Length > 0 ? levels / session.Roster.Length : 0;

            return Loc.Pick($"Signed in.\nTeam of {session.Roster.Length}, average level {average}.",
                            $"로그인됨.\n{session.Roster.Length}마리 편성, 평균 레벨 {average}.");
        }

        // --- Doing things -------------------------------------------------------------------

        private void Take(int index)
        {
            if (_confirming) return;
            if (index < 0 || index >= _actions.Count) return;
            _actions[index]?.Invoke();
        }

        private void GoBack()
        {
            if (_page == Page.Root) return;
            _page = Page.Root;
            Refresh();
        }

        /// <summary>
        /// Leaves the menu for the story.
        ///
        /// A new game deletes the save first and nothing else: <c>PlayerProfileHost</c> loads
        /// on Start only when a file exists, so removing it is the whole of "start over". The
        /// deletion happens here rather than in the town, where a half-loaded profile would
        /// already have been built from the file being deleted.
        /// </summary>
        private void LoadStory(bool fresh)
        {
            if (fresh) SaveSystem.Delete();
            SceneManager.LoadScene(_storyScene, LoadSceneMode.Single);
        }

        /// <summary>
        /// Asks before erasing a save, because there is no undo for it.
        ///
        /// Modal and keyboard-answerable, and it blocks the row list underneath — the failure
        /// this guards is a player pressing Enter twice on a menu they have just opened and
        /// losing an afternoon.
        /// </summary>
        private void ConfirmNewGame()
        {
            _confirming = true;
            ConfirmDialog.Show(_canvas.transform,
                Loc.Pick("Start a new game?", "새로 시작할까요?"),
                Loc.Pick("The existing save will be erased.\nThis cannot be undone.",
                         "저장된 게임이 모두 지워져요.\n되돌릴 수 없어요."),
                // 확인 / 취소 — the user's wording. Plain and symmetrical: the sentence above
                // already says exactly what is about to be erased, so the button does not have
                // to restate it, and a pair of ordinary words is easier to answer under a
                // warning than a verb that has to be re-read.
                Loc.Pick("OK", "확인"),
                Loc.Pick("Cancel", "취소"),
                confirmed =>
                {
                    _confirming = false;
                    if (confirmed) LoadStory(true);
                });
        }

        private void OpenAccount()
        {
            _view.SetFooter("");
            _account.Open();
        }

        private void OpenGacha()
        {
            _view.SetFooter("");
            _gacha.Open();
        }

        private void OpenSettings()
        {
            _view.SetFooter("");
            _settings.Open();
        }

        /// <summary>
        /// Hands off to a battle.
        ///
        /// Deliberately one method for both modes: the difference between them is who chooses
        /// the opposing team, and everything after that — the stage, the HUD, the experience
        /// report — is identical. <see cref="BattleModeLauncher"/> owns that difference.
        /// </summary>
        /// <summary>
        /// Hands off to a battle.
        ///
        /// The two modes diverge here and nowhere else. An AI fight can start immediately —
        /// the opponent is drawn locally. A PvP fight cannot: there is no opponent until
        /// somebody else has queued, so 온라인 대전 opens the matchmaking screen and the battle
        /// is launched from ITS confirmation, once <see cref="PvpSession"/> is holding a real
        /// match id, seed and opposing roster.
        ///
        /// Launching PvP directly from here is what the code used to do, and it produced a
        /// locally-generated team wearing a stranger's job title: a fake match that then failed
        /// to pay any experience because the server refuses a PvP result with no match id.
        /// </summary>
        /// <summary>
        /// What pressing a battle row does, whatever state the account is in.
        ///
        /// Routes to the missing prerequisite instead of refusing: no account opens 계정, no
        /// team opens 가챠, and only a ready account reaches a fight. The player pressed 대전
        /// because they want to battle, and each of these IS the next step of doing that — so
        /// the row moves them along rather than sending them away with an instruction.
        ///
        /// Both screens raise Changed / Closed when they finish, which redraws these rows, so
        /// the player comes back to a 대전 row that now works.
        /// </summary>
        private void EnterBattle(string mode)
        {
            var session = OnlineSession.Instance;

            if (session == null || !session.IsSignedIn) { OpenAccount(); return; }
            if (!session.HasTeam) { OpenGacha(); return; }

            StartBattle(mode);
        }

        private void StartBattle(string mode)
        {
            if (mode == "pvp")
            {
                _view.SetFooter("");
                _matchmaking.Open();
                return;
            }

            BattleModeLauncher.Launch(mode);
        }

        private static bool CanQuit =>
#if UNITY_WEBGL && !UNITY_EDITOR
            false;
#else
            true;
#endif

        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // --- Input --------------------------------------------------------------------------

        private void Update()
        {
            if (_confirming) return;
            if (_account != null && _account.IsOpen) return;
            if (_gacha != null && _gacha.IsOpen) return;
            if (_settings != null && _settings.IsOpen) return;
            if (_matchmaking != null && _matchmaking.IsOpen) return;

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            _repeatCooldown -= Time.unscaledDeltaTime;

            var down = keyboard.downArrowKey.isPressed || keyboard.sKey.isPressed;
            var up = keyboard.upArrowKey.isPressed || keyboard.wKey.isPressed;

            if ((down || up) && _repeatCooldown <= 0f)
            {
                _view.Move(down ? 1 : -1);
                // Held keys repeat, but slowly enough to land on a row rather than scroll past
                // it, and the first step is immediate so a tap is never swallowed.
                _repeatCooldown = 0.16f;
            }

            if (!down && !up) _repeatCooldown = 0f;

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame
                || keyboard.spaceKey.wasPressedThisFrame)
            {
                _view.Take();
            }

            if (keyboard.escapeKey.wasPressedThisFrame) GoBack();
        }
    }
}
