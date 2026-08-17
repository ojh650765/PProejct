using System.Collections;
using System.Collections.Generic;
using PokeLab.Cinematics;
using PokeLab.Overworld;
using PokeLab.Overworld.People;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// Stages the starter choice as a scene and takes the answer.
    ///
    /// <see cref="StarterSelection"/> holds the options and waits for someone to call
    /// <c>Choose</c>; <see cref="EpisodeRunner"/>'s ChooseStarter beat waits for <c>Chosen</c>
    /// to be raised, and nothing else in the game raises it. This is the only thing that does.
    ///
    /// It used to raise it from a dialogue box with three text choices. That worked, and it
    /// was wrong: the moment the game hands over its first Pokémon was a list. Everything the
    /// scene needed was already drawn and already authored — the briefcase sprite, the
    /// open/closed capture-ball pair, a <c>Look_Case</c> mark placed at exactly the height the
    /// balls stand at, and a <c>story.case_open</c> signal on the line where Linden opens it.
    /// So the case is now set down in the world, the camera comes down to it, and the choice
    /// is made by moving between three balls that are actually there.
    ///
    /// The split is the usual one. <see cref="StarterCaseStage"/> in PokeLab.Cinematics owns
    /// everything in the world and knows nothing about the choice; this owns the choice, the
    /// cursor and the screen, and is in PokeLab.Boot because the selection is Overworld, the
    /// widgets are UI, the stage is Cinematics and the art manifest is Overworld.People — and
    /// none of those four assemblies can see each other.
    ///
    /// The dialogue-box list survives as the fallback. The episode plays op_choice_prompt
    /// before this beat and op_choice_result after it, and neither is allowed to be left
    /// hanging because a marker went missing in a scene rebuild.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StarterPresenter : MonoBehaviour
    {
        [SerializeField] private StarterSelection _selection;
        [SerializeField] private DialogueView _view;

        [Tooltip("String table keys. The text lives in strings.json so the moment reads in " +
                 "the player's language and not in the one it was written in.")]
        [SerializeField] private string _speakerKey = "starter.speaker";
        [SerializeField] private string _promptKey = "starter.prompt";

        /// <summary>
        /// Marks authored in cast.json and instantiated into the scene by the level builder.
        /// Named rather than assigned because the scene is rebuilt constantly and any
        /// reference serialized here would be to a copy from two rebuilds ago.
        /// </summary>
        private const string CaseMark = "Look_Case";
        private const string ProfessorMark = "Mark_Prof_Case";
        private const string PlayerMark = "Mark_Player_GateStand";

        /// <summary>The line in op_professor_arrives that opens the case. Authored in cast.json.</summary>
        private const string CaseOpenSignal = "story.case_open";

        /// <summary>Above the dialogue overlay's 400: the confirm and the flash sit over the box.</summary>
        private const int StarterSortingOrder = 420;

        private StarterCaseStage _stage;
        private Coroutine _running;
        private bool _offered;

        // The cursor and the two answers the confirm step is waiting on. Ints rather than an
        // enum because they are set from button callbacks and read from a coroutine, and the
        // coroutine's only question is "has anything happened yet".
        private int _cursor;
        private int _opening = -1;
        private int _answer;

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private Image _flash;
        private RectTransform[] _targets;
        private Image[] _rings;
        private TextMeshProUGUI[] _labels;
        private Button[] _buttons;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _blurb;
        private TextMeshProUGUI _hint;
        private RectTransform _confirmRow;
        private Button _takeButton;
        private TextMeshProUGUI _takeLabel;

        private OverworldInputReader _reader;

        private void Awake()
        {
            if (_selection == null) _selection = FindFirstObjectByType<StarterSelection>();
            if (_view == null) _view = FindFirstObjectByType<DialogueView>();
        }

        private void OnEnable() => OverworldEvents.DialogueSignal += OnDialogueSignal;

        private void OnDisable() => OverworldEvents.DialogueSignal -= OnDialogueSignal;

        /// <summary>
        /// The case is opened by the line that says it is being opened, not by the beat that
        /// follows it. Linden sets it down on line 7 of op_professor_arrives and then speaks
        /// two more lines and a prompt over it, which is a third of a minute of him talking to
        /// an open case — and the alternative, opening it when the choice beat starts, throws
        /// that away and makes the case appear out of nowhere on the same frame the cursor
        /// does.
        /// </summary>
        private void OnDialogueSignal(string signalId)
        {
            if (signalId != CaseOpenSignal) return;
            if (_stage != null || _selection == null || _selection.HasChosen) return;
            if (!BuildStage()) return;
            CinematicRunner.Run(_stage.OpenCase());
        }

        private void Update()
        {
            // Re-found rather than resolved once in Awake.
            //
            // DialogueView is built at runtime by DialoguePresenter, so on any frame where this
            // component's Awake ran first the reference was null — and being null was the very
            // first thing Update tested, so the presenter returned every frame and the starter
            // case never opened at all. The runner then timed out after ninety seconds and took
            // a ball on the player's behalf, which is why the beat looked like it was skipped
            // rather than broken.
            if (_selection == null) _selection = FindFirstObjectByType<StarterSelection>();
            if (_view == null) _view = FindFirstObjectByType<DialogueView>();

            if (_offered || _selection == null || _view == null) return;
            if (_selection.HasChosen) return;
            if (!ShouldOffer()) return;

            _offered = true;
            _running = StartCoroutine(RunChoice());
        }

        private void LateUpdate()
        {
            if (_canvas == null) return;
            TrackBalls();
            ReadCursor();
            ReadConfirmButton();
        }

        /// <summary>
        /// Whether the runner is currently waiting on a choice.
        ///
        /// Polled rather than driven by an event because StarterSelection raises
        /// <c>Chosen</c> — the answer — and has no signal for "I am being asked". Adding one
        /// would mean widening a type another worker owns while they are in it.
        /// </summary>
        private bool ShouldOffer()
        {
            var runner = FindFirstObjectByType<EpisodeRunner>();
            return runner != null && runner.IsPlaying && runner.IsAwaitingStarter;
        }

        // --- The beat --------------------------------------------------------------------------

        private IEnumerator RunChoice()
        {
            var options = _selection.Options;
            if (options == null || options.Count == 0)
            {
                Debug.LogError("[Starter] No starters are authored, so there is nothing to " +
                               "offer. The opening will hand the player whatever the runner " +
                               "falls back to.", this);
                yield break;
            }

            if (_stage == null && !BuildStage())
            {
                // No marks and no art: fall back to the list this beat used to be. A worse
                // scene is survivable; an episode stalled on a beat nobody can answer is not.
                OfferAsDialogue(options);
                yield break;
            }

            // Unconditional: OpenCase already returns immediately when the case is open, and
            // the stage is the one place that should know whether it is.
            yield return _stage.OpenCase();

            BuildScreen(options);
            Select(0);

            // Linden's line stays in the box underneath, because the episode's own dialogue
            // opened this moment and will close it — the box is part of the composition, not
            // a thing this replaces.
            _view.Show(PokeLab.Core.Loc.Get(_speakerKey), PokeLab.Core.Loc.Get(_promptKey));

            var chosen = -1;
            while (chosen < 0)
            {
                _opening = -1;
                SetBrowsing(true);
                yield return new WaitUntil(() => _opening >= 0 || _selection.HasChosen);

                // The runner's 90 s timeout fired and picked for the player. Stop rather than
                // stage a reveal for a choice that has already been committed elsewhere.
                if (_selection.HasChosen) break;

                SetBrowsing(false);
                CinematicRunner.Run(FlashScreen());
                yield return _stage.Reveal(_opening);

                ShowConfirm(options[_opening].DisplayName);
                _answer = 0;
                yield return new WaitUntil(() => _answer != 0 || _selection.HasChosen);
                if (_selection.HasChosen) break;

                HideConfirm();
                if (_answer > 0) chosen = _opening;
                else yield return _stage.Unreveal(_opening);
            }

            TearDownScreen();

            // This is the call the runner has been blocked on since the beat began. Everything
            // above it is presentation; this line is the game starting.
            if (chosen >= 0) _selection.Choose(chosen);

            // The creature stays out of its ball while Linden says what he thinks of it —
            // op_choice_result is about the animal standing in front of them, and cutting back
            // to the exploration camera first would have him talking about something off
            // screen. Bounded, because a dialogue that never ends must not strand the case in
            // the world.
            yield return WaitForResultDialogue(20f);
            yield return _stage.Dismiss();
            _stage = null;
            _running = null;
        }

        /// <summary>
        /// Waits for the episode's reaction dialogue to finish.
        ///
        /// Keyed on the sequence id prefix rather than on any sequence ending, because the
        /// runner resolves op_choice_result to op_choice_result_1 / _5 / _10 by species and
        /// there is no single id to compare against.
        /// </summary>
        private IEnumerator WaitForResultDialogue(float timeout)
        {
            var runner = DialogueRunner.Instance != null
                ? DialogueRunner.Instance
                : FindFirstObjectByType<DialogueRunner>();
            if (runner == null) yield break;

            var done = false;
            void OnEnded(string id)
            {
                if (id != null && id.StartsWith("op_choice_result", System.StringComparison.Ordinal))
                    done = true;
            }

            runner.SequenceEnded += OnEnded;
            var elapsed = 0f;
            while (!done && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            runner.SequenceEnded -= OnEnded;
        }

        // --- The world -------------------------------------------------------------------------

        /// <summary>First of <paramref name="names"/> that exists in a loaded scene.</summary>
        private static GameObject FindMark(params string[] names)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var found = GameObject.Find(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Builds the stage from the authored marks and the drawn props. Returns false when
        /// either is missing, which is the fallback's only trigger.
        /// </summary>
        private bool BuildStage()
        {
            // Each mark is looked up with the field's equivalent behind it.
            //
            // These three name the lab doorway, and they are authored into Town and Overworld
            // — which was right when the starter was handed over indoors. The story was
            // restructured so the choice happens on the lake bank instead, out of the
            // professor's own bag, and the marks did not move with it. In the field they
            // resolve to nothing when Town is streamed out, and to a spot in the town square
            // when it is not: either the case had nowhere to stand or it stood a hundred
            // metres behind the player. Both look identical from the player's chair — the
            // choice never appears.
            var caseMark = FindMark(CaseMark, "Prop_ProfessorBag");
            var professorMark = FindMark(ProfessorMark, "Mark_Prof_Return", "Mark_Rival_FieldPost");
            var playerMark = FindMark(PlayerMark);

            // The player themselves, rather than a mark, is the last resort and the best one:
            // wherever this beat is playing, the person choosing is standing there.
            if (playerMark == null)
            {
                var locomotion = FindFirstObjectByType<PokeLab.Overworld.PlayerLocomotion>();
                if (locomotion != null) playerMark = locomotion.gameObject;
            }

            if (caseMark == null || professorMark == null || playerMark == null)
            {
                Debug.LogWarning($"[Starter] Neither '{CaseMark}' nor the field's bag is in any " +
                                 "loaded scene, so the case has nowhere to stand. Their positions " +
                                 "are in cast.json and LevelLayoutBuilder.BuildCast instantiates " +
                                 "them; falling back to the dialogue list.", this);
                return false;
            }

            var options = _selection.Options;
            var stage = StarterCaseStage.Create(caseMark.transform.position,
                professorMark.transform.position.y, professorMark.transform.position,
                playerMark.transform.position);

            var briefcase = PersonSpriteLibrary.Shared.Prop("briefcase");
            if (briefcase == null)
            {
                Destroy(stage.gameObject);
                return false;
            }

            stage.SetCaseArt(Texture(briefcase.texture), Columns(briefcase.Idle),
                Rows(briefcase.Idle), briefcase.FrameHeightMetres, briefcase.groundOrigin);

            var takingBall = PersonSpriteLibrary.Shared.Prop("player_takes_ball");
            if (takingBall != null)
            {
                stage.SetPlayerArt(Texture(takingBall.texture), Columns(takingBall.Idle),
                    Rows(takingBall.Idle), takingBall.FrameHeightMetres, takingBall.groundOrigin);
            }

            var species = new int[options.Count];
            var heights = new float[options.Count];
            for (var i = 0; i < options.Count; i++)
            {
                species[i] = options[i].SpeciesId;
                heights[i] = ResolveHeight(options[i].SpeciesId);
            }
            stage.SetBalls(species, heights);

            _stage = stage;
            return true;
        }

        private static Texture2D Texture(string path) => PersonSpriteLibrary.Shared.Texture(path);

        private static int Columns(PersonClip clip) => clip != null ? clip.columns : 1;

        private static int Rows(PersonClip clip) => clip != null ? clip.rows : 1;

        /// <summary>
        /// How tall the creature stands when it comes out of the ball.
        ///
        /// The dex stores height in decimetres — Bulbasaur is 7, not 0.7 — and using that
        /// number as metres puts a seven-metre Bulbasaur over the town. Divided here rather
        /// than trusted from a caller, because the mistake is silent and enormous.
        /// </summary>
        private static float ResolveHeight(int speciesId)
        {
            if (PokeLab.Core.ServiceHub.TryGet<PokeLab.Core.ICreatureArtRegistry>(out var art) && art != null)
            {
                var authored = art.GetDisplayHeight(speciesId);
                if (authored > 0.05f) return authored;
            }

            if (PokeLab.Core.ServiceHub.TryGet<PokeLab.Core.ISpeciesRegistry>(out var registry)
                && registry != null && registry.TryGet(speciesId, out var species) && species.Height > 0f)
            {
                return species.Height * 0.1f;
            }

            return 0.6f;
        }

        // --- The screen ------------------------------------------------------------------------

        /// <summary>
        /// Builds the cursor, the three hit targets and the name card.
        ///
        /// Built here rather than authored into a scene, for <see cref="DialoguePresenter"/>'s
        /// reason: the scene is regenerated constantly and a serialized copy would have to be
        /// re-saved every time the layout changed.
        ///
        /// <b>The cursor is the EventSystem's own selection.</b> That is what makes a pointer
        /// and a pad work at once without either being a special case: navigation moves the
        /// selection, hovering sets it, clicking activates it, and this reads which object is
        /// selected rather than implementing three input paths that can disagree about which
        /// ball is under the cursor.
        /// </summary>
        private void BuildScreen(IReadOnlyList<StarterOption> options)
        {
            UiBuilder.EnsureEventSystem();

            var canvasGo = new GameObject("StarterCanvas", typeof(RectTransform));
            _canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(_canvas, StarterSortingOrder);
            _canvasRect = (RectTransform)canvasGo.transform;

            _targets = new RectTransform[options.Count];
            _rings = new Image[options.Count];
            _labels = new TextMeshProUGUI[options.Count];
            _buttons = new Button[options.Count];

            for (var i = 0; i < options.Count; i++) BuildTarget(i, options[i]);
            LinkNavigation();
            BuildCard();

            // Last, and on top of everything including the dialogue box: the reveal flash is
            // the loudest thing on screen and it is not allowed to be behind anything.
            _flash = UiBuilder.Image("RevealFlash", _canvasRect, null, new Color(1f, 1f, 1f, 0f));
            UiBuilder.Stretch(_flash.rectTransform);
            _flash.raycastTarget = false;
        }

        private void BuildTarget(int index, StarterOption option)
        {
            var root = UiBuilder.Rect($"Ball{index}", _canvasRect, false);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.zero;
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(170f, 170f);
            _targets[index] = root;

            // Invisible, but a raycast target: the thing being clicked is the ball in the
            // world, and drawing a plate over it would hide what the click is for.
            var hit = UiBuilder.Image("Hit", root, null, new Color(1f, 1f, 1f, 0f));
            UiBuilder.Stretch(hit.rectTransform);
            hit.raycastTarget = true;

            var ring = UiBuilder.OutlinedPanel("Ring", root, UiPalette.ScannerCyan, 84, 3);
            UiBuilder.Stretch(ring.rectTransform);
            ring.enabled = false;
            _rings[index] = ring;

            var label = UiBuilder.Text("Name", root, option.DisplayName, UiTextRole.Caption,
                UiPalette.TextMuted, TMPro.TextAlignmentOptions.Center);
            label.rectTransform.anchorMin = new Vector2(0f, 0f);
            label.rectTransform.anchorMax = new Vector2(1f, 0f);
            label.rectTransform.pivot = new Vector2(0.5f, 1f);
            label.rectTransform.anchoredPosition = new Vector2(0f, -8f);
            label.rectTransform.sizeDelta = new Vector2(0f, 36f);
            _labels[index] = label;

            var captured = index;
            _buttons[index] = UiBuilder.Button("Pick", root, hit, () => OnBallPressed(captured));

            // Hover selects without committing, so a pointer reads the blurb the same way a
            // pad does. The click is the commit, and it is the Button's own.
            var trigger = root.gameObject.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            entry.callback.AddListener(_ => Select(captured));
            trigger.triggers.Add(entry);
        }

        /// <summary>
        /// Wires left/right between the balls explicitly and refuses to wrap.
        ///
        /// Unity's automatic navigation searches by screen direction, and these three rects
        /// move every frame as the camera blends; explicit links cannot drift. No wrap because
        /// the balls are objects on a table rather than a menu — running off the right of the
        /// last one and reappearing on the left is a list behaviour, and it also makes the
        /// stick's auto-repeat cycle the cursor forever instead of resting on the end.
        /// </summary>
        private void LinkNavigation()
        {
            for (var i = 0; i < _buttons.Length; i++)
            {
                var navigation = new Navigation { mode = Navigation.Mode.Explicit };
                if (i > 0) navigation.selectOnLeft = _buttons[i - 1];
                if (i < _buttons.Length - 1) navigation.selectOnRight = _buttons[i + 1];
                _buttons[i].navigation = navigation;
            }
        }

        private void BuildCard()
        {
            // Above the dialogue scrim, which is 336 px tall on the 1920x1080 reference. The
            // name and the blurb belong to the ball, not to the speaker, so they must not sit
            // inside the box the speaker is talking from.
            var card = UiBuilder.Rect("Card", _canvasRect, false);
            card.anchorMin = new Vector2(0.5f, 0f);
            card.anchorMax = new Vector2(0.5f, 0f);
            card.pivot = new Vector2(0.5f, 0f);
            card.anchoredPosition = new Vector2(0f, 372f);
            card.sizeDelta = new Vector2(900f, 168f);

            _name = UiBuilder.Text("Name", card, string.Empty, UiTextRole.Title,
                UiPalette.TextPrimary, TMPro.TextAlignmentOptions.Center);
            _name.rectTransform.anchorMin = new Vector2(0f, 1f);
            _name.rectTransform.anchorMax = new Vector2(1f, 1f);
            _name.rectTransform.pivot = new Vector2(0.5f, 1f);
            _name.rectTransform.sizeDelta = new Vector2(0f, 64f);

            _blurb = UiBuilder.Text("Blurb", card, string.Empty, UiTextRole.Secondary,
                UiPalette.TextSecondary, TMPro.TextAlignmentOptions.Center);
            _blurb.rectTransform.anchorMin = new Vector2(0f, 1f);
            _blurb.rectTransform.anchorMax = new Vector2(1f, 1f);
            _blurb.rectTransform.pivot = new Vector2(0.5f, 1f);
            _blurb.rectTransform.anchoredPosition = new Vector2(0f, -64f);
            _blurb.rectTransform.sizeDelta = new Vector2(0f, 56f);

            _hint = UiBuilder.Text("Hint", card, PokeLab.Core.Loc.Get("starter.hint"),
                UiTextRole.Caption, UiPalette.TextMuted, TMPro.TextAlignmentOptions.Center);
            _hint.rectTransform.anchorMin = new Vector2(0f, 1f);
            _hint.rectTransform.anchorMax = new Vector2(1f, 1f);
            _hint.rectTransform.pivot = new Vector2(0.5f, 1f);
            _hint.rectTransform.anchoredPosition = new Vector2(0f, -124f);
            _hint.rectTransform.sizeDelta = new Vector2(0f, 36f);

            _confirmRow = UiBuilder.Rect("Confirm", card, false);
            _confirmRow.anchorMin = new Vector2(0.5f, 1f);
            _confirmRow.anchorMax = new Vector2(0.5f, 1f);
            _confirmRow.pivot = new Vector2(0.5f, 1f);
            _confirmRow.anchoredPosition = new Vector2(0f, -116f);
            _confirmRow.sizeDelta = new Vector2(680f, 64f);
            UiBuilder.Horizontal(_confirmRow, 24f);
            _confirmRow.gameObject.SetActive(false);

            _takeButton = BuildAnswer("Take", string.Empty, 1, out _takeLabel);
            BuildAnswer("Back", PokeLab.Core.Loc.Get("starter.answer.no"), -1, out _);
        }

        private Button BuildAnswer(string name, string text, int answer, out TextMeshProUGUI label)
        {
            var slab = UiBuilder.Rect(name, _confirmRow, false);
            UiBuilder.Size(slab, 320f, 60f);

            var fill = UiBuilder.Image("Fill", slab, UiSprites.Panel(10),
                answer > 0 ? UiPalette.ScannerCyan : UiPalette.SurfaceRaised);
            UiBuilder.Stretch(fill.rectTransform);

            label = UiBuilder.Text("Label", slab, text, UiTextRole.Body,
                answer > 0 ? UiPalette.TextOnAccent : UiPalette.TextPrimary,
                TMPro.TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);

            UiButtonMotion.Attach(slab, 4);
            return UiBuilder.Button("Click", slab, fill, () => _answer = answer);
        }

        // --- Cursor ----------------------------------------------------------------------------

        /// <summary>
        /// Keeps each hit target over the ball it belongs to.
        ///
        /// Every frame, not once: the camera is blending into the close shot for the first two
        /// seconds of the beat and the balls are rising as it does, so a target placed once is
        /// over empty ground for exactly the period the player is first looking at it.
        /// </summary>
        private void TrackBalls()
        {
            var camera = Camera.main;
            if (camera == null || _stage == null || _targets == null) return;

            var scale = _canvas.scaleFactor > 0.0001f ? _canvas.scaleFactor : 1f;
            for (var i = 0; i < _targets.Length; i++)
            {
                var screen = camera.WorldToScreenPoint(_stage.BallPoint(i));
                // Behind the lens projects to a mirrored point in front of it, which would put
                // the cursor on the wrong ball rather than nowhere.
                var visible = screen.z > 0f;
                _targets[i].gameObject.SetActive(visible);
                if (visible) _targets[i].anchoredPosition = new Vector2(screen.x, screen.y) / scale;
            }
        }

        /// <summary>Reads the EventSystem's selection back as the cursor position.</summary>
        private void ReadCursor()
        {
            var system = EventSystem.current;
            if (system == null || _buttons == null) return;

            var selected = system.currentSelectedGameObject;
            for (var i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i] == null || selected != _buttons[i].gameObject) continue;
                if (i != _cursor) Select(i);
                return;
            }

            // Clicking off a button clears the selection, and a cleared selection is a cursor
            // the pad cannot move — the next Navigate has nothing to move *from*. Put it back.
            if (selected == null && _confirmRow != null && !_confirmRow.gameObject.activeSelf)
                Select(_cursor);
        }

        /// <summary>
        /// Lets the game's own confirm button press whatever the cursor is on.
        ///
        /// The UI module's Submit is Enter and the pad's south face; the button this game has
        /// taught the player to press for the last three minutes of dialogue is Interact — E,
        /// and the pad's north face. Pressing it here does what pressing it anywhere else in
        /// the opening does, which is why it goes through <c>SubmitPressed</c>: that property
        /// exists precisely because it is read before the control gate, and control is held
        /// for the whole of this beat.
        /// </summary>
        private void ReadConfirmButton()
        {
            if (_reader == null) _reader = FindFirstObjectByType<OverworldInputReader>();
            if (_reader == null || !_reader.SubmitPressed) return;

            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            var button = selected != null ? selected.GetComponent<Button>() : null;
            if (button != null && button.IsInteractable()) button.onClick.Invoke();
        }

        private void Select(int index)
        {
            if (_targets == null || index < 0 || index >= _targets.Length) return;
            _cursor = index;
            if (_stage != null) _stage.Highlight(index);

            var options = _selection.Options;
            if (_name != null) _name.SetText(options[index].DisplayName);
            if (_blurb != null) _blurb.SetText(options[index].Blurb ?? string.Empty);

            for (var i = 0; i < _rings.Length; i++)
            {
                _rings[i].enabled = i == index;
                _labels[i].color = i == index ? UiPalette.TextPrimary : UiPalette.TextMuted;
            }

            var system = EventSystem.current;
            if (system != null && _buttons[index] != null
                && system.currentSelectedGameObject != _buttons[index].gameObject)
            {
                system.SetSelectedGameObject(_buttons[index].gameObject);
            }
        }

        private void OnBallPressed(int index)
        {
            if (_opening >= 0) return; // already opening one; a second press is a double click
            Select(index);
            _opening = index;
        }

        private void SetBrowsing(bool browsing)
        {
            if (_buttons == null) return;
            for (var i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i] != null) _buttons[i].interactable = browsing;
                if (_rings[i] != null) _rings[i].enabled = browsing && i == _cursor;
            }
            if (_hint != null) _hint.gameObject.SetActive(browsing);
            if (browsing) Select(_cursor);
        }

        private void ShowConfirm(string speciesName)
        {
            if (_confirmRow == null) return;
            if (_takeLabel != null)
                _takeLabel.SetText(PokeLab.Core.Loc.Get("starter.answer.yes", speciesName));
            if (_blurb != null)
                _blurb.SetText(PokeLab.Core.Loc.Get("starter.confirm", speciesName));

            _confirmRow.gameObject.SetActive(true);
            var system = EventSystem.current;
            if (system != null && _takeButton != null)
                system.SetSelectedGameObject(_takeButton.gameObject);
        }

        private void HideConfirm()
        {
            if (_confirmRow != null) _confirmRow.gameObject.SetActive(false);
        }

        private IEnumerator FlashScreen()
        {
            if (_flash == null) yield break;

            // Timed to land on the ball splitting rather than on the press: the wind-up shiver
            // is 0.35 s and the flash is what covers the swap to the open model.
            yield return CinematicRunner.Wait(0.3f);
            yield return CinematicRunner.Tween(0.08f, CinematicEase.OutQuad,
                t => SetFlash(Mathf.Lerp(0f, 0.92f, t)));
            yield return CinematicRunner.Tween(0.55f, CinematicEase.InQuad,
                t => SetFlash(Mathf.Lerp(0.92f, 0f, t)));
        }

        private void SetFlash(float alpha)
        {
            if (_flash != null) _flash.color = new Color(1f, 1f, 1f, alpha);
        }

        private void TearDownScreen()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            _canvas = null;
            _canvasRect = null;
            _targets = null;
            _rings = null;
            _labels = null;
            _buttons = null;
            _flash = null;
            _confirmRow = null;
            _takeButton = null;
            _takeLabel = null;
            _name = null;
            _blurb = null;
            _hint = null;

            var system = EventSystem.current;
            if (system != null) system.SetSelectedGameObject(null);
        }

        // --- Fallback --------------------------------------------------------------------------

        /// <summary>
        /// The beat as it was: three labelled choices in the dialogue box.
        ///
        /// Kept because the staged version depends on marks that a scene rebuild creates and
        /// on art that lives outside this file's control. If either is missing the opening
        /// still reaches its most important moment and still lets the player answer it — worse
        /// looking, and playable, which is the right way round.
        /// </summary>
        private void OfferAsDialogue(IReadOnlyList<StarterOption> options)
        {
            var labels = new string[options.Count];
            for (var i = 0; i < labels.Length; i++) labels[i] = options[i].DisplayName;

            _view.ShowChoices(PokeLab.Core.Loc.Get(_speakerKey), PokeLab.Core.Loc.Get(_promptKey),
                labels, index =>
                {
                    _selection.Choose(index);
                    _view.Close();
                });
        }

        private void OnDestroy()
        {
            if (_running != null) StopCoroutine(_running);
            TearDownScreen();
        }
    }
}
