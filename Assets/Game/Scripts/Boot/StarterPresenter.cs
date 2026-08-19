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
    /// The dialogue-box list survives as the fallback, because the staged version depends on
    /// marks a scene rebuild creates and on art outside this file's control, and the beat in
    /// front of it is the point of no return for the whole save.
    ///
    /// <b>Nobody speaks over the choice.</b> The act was rewritten so this happens on the lake
    /// bank, out of a bag whose owner has walked off, with the player and their friend ringed
    /// by the flock that came out of the grass — they are choosing in secret and at speed. The
    /// professor's prompt that used to sit in the box with his name on it is gone with the
    /// scene it belonged to; what is left is one speaker-less line, which is how this script
    /// writes a thought.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StarterPresenter : MonoBehaviour
    {
        [SerializeField] private StarterSelection _selection;
        [SerializeField] private DialogueView _view;

        [Tooltip("String table key for the line in the box while the player is choosing. The " +
                 "text lives in strings.json so the moment reads in the player's language and " +
                 "not in the one it was written in.")]
        [SerializeField] private string _promptKey = "starter.prompt";

        // There is deliberately no speaker key beside the prompt. One used to point at
        // 'starter.speaker', which is Linden's name, and putting a name plate on this moment
        // is the one thing the scene cannot have — see the note on the class. The scenes still
        // carry the old serialized value; Unity drops a property nothing declares.

        /// <summary>
        /// Marks authored in cast.json and instantiated into the scene by the level builder.
        /// Named rather than assigned because the scene is rebuilt constantly and any
        /// reference serialized here would be to a copy from two rebuilds ago.
        ///
        /// <see cref="CaseMark"/> belongs to the version of this scene that happened at the lab
        /// door and is authored into Town; <see cref="BagProp"/> is where the choice happens
        /// now. Both are listed because both may be loaded — see <see cref="BuildStage"/>.
        /// </summary>
        private const string CaseMark = "Look_Case";
        private const string BagProp = "Prop_ProfessorBag";

        /// <summary>Whoever is crouched over the bag with the player. The friend, not the professor.</summary>
        private const string CompanionActor = "NPC_Rival";

        /// <summary>Metres within which the companion counts as being at this choice.</summary>
        private const float CompanionRange = 8f;

        /// <summary>
        /// Metres above the ground the three balls stand at — the lid of the case this stage
        /// draws, which is 0.59 m tall at <see cref="StarterCaseStage.WorldSpriteScale"/>. The
        /// same number cast.json authored Look_Case's height from, and it belongs to the drawn
        /// case rather than to whatever object happens to mark the spot.
        /// </summary>
        private const float CaseLidHeightMetres = 0.53f;

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
        private Button[] _buttons;
        private Image _portrait;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _blurb;
        private TextMeshProUGUI _hint;
        private RectTransform _chipRow;
        private Image[] _chipFrames;
        private TextMeshProUGUI[] _chipLabels;
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

            // Nobody is speaking over this, and that is the point of the moment.
            //
            // Linden's own prompt used to sit in the box with his name on it for the whole of
            // the choice, left over from the version where he opened a case in front of the
            // player and talked them through it. The act was rewritten so this happens on the
            // lake bank, out of a bag that is not theirs, with its owner already gone — the
            // player is choosing in secret and there is no one standing over them to narrate
            // it. A name plate here is the professor watching, which is the one thing the
            // scene cannot have.
            //
            // The box stays for the length of the choice because the moment still wants a
            // line; what goes in it is the player's own thought, with no speaker, the way
            // every other piece of narration in the script is written. It is closed the
            // moment the choice commits — see the note above _view.Close() below.
            _view.Show(string.Empty, PokeLab.Core.Loc.Get(_promptKey));

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

            // The prompt was this presenter's own Show, and nothing else will ever close it —
            // DialoguePresenter only closes sequences the DialogueRunner ran. Left open, the
            // box holds UiFocus above the battle command panel and its canvas (order 400,
            // raycast-blocking) sits over the battle HUD (order 380), so the staged battle the
            // very next beat starts swallows every key and every click: the player picks a
            // starter and the game visibly stops. Closed on every path out, including the
            // runner's timeout pick.
            if (_view != null) _view.Close();

            // This is the call the runner has been blocked on since the beat began. Everything
            // above it is presentation; this line is the game starting.
            if (chosen >= 0)
            {
                var taken = _selection.Options[chosen];
                Debug.Log($"[Starter] Commit: '{taken.DisplayName}' (game species {taken.SpeciesId}) " +
                          "— prompt closed, choice handed to StarterSelection.", this);
                _selection.Choose(chosen);
            }

            // The creature stays out of its ball for a moment, and then the case goes.
            //
            // This used to wait up to twenty seconds for 'op_choice_result' — Linden's read on
            // the one you took. That sequence went with the version of the act he was present
            // for and nothing has played it since, so the wait timed out in full every single
            // time: a 1.35 m close-up on the case, held at priority 150, over the top of the
            // wild battle the very next beat starts. There is no line here any more, so what
            // the moment needs is a beat to look at what it got and then the camera back.
            yield return new WaitForSeconds(TakenHoldSeconds);
            yield return _stage.Dismiss();
            _stage = null;
            _running = null;
        }

        /// <summary>Seconds the creature is looked at before the shot is handed back.</summary>
        private const float TakenHoldSeconds = 1.6f;

        // --- The world -------------------------------------------------------------------------

        /// <summary>
        /// Whichever of <paramref name="names"/> exists and is nearest to
        /// <paramref name="near"/>.
        ///
        /// Nearest rather than first, and the difference is the whole bug this replaced. Both
        /// halves of the map are loaded at once — WorldStreamer preloads Town and Field and
        /// never unloads either — so a name-ordered lookup happily returns the town's copy of a
        /// mark while the beat is playing at the lake, a hundred metres away. Where the player
        /// is standing is the only thing in the scene that knows which of two marks this run of
        /// the scene means.
        /// </summary>
        private static GameObject FindNearestMark(Vector3 near, params string[] names)
        {
            GameObject best = null;
            var bestDistance = float.MaxValue;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var found = GameObject.Find(name);
                if (found == null) continue;

                var distance = (found.transform.position - near).sqrMagnitude;
                if (distance >= bestDistance) continue;

                best = found;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>Where the person doing the choosing is standing.</summary>
        private static Vector3 ResolvePlayerPosition()
        {
            var locomotion = FindFirstObjectByType<PokeLab.Overworld.PlayerLocomotion>();
            if (locomotion != null) return locomotion.transform.position;

            var tagged = GameObject.FindGameObjectWithTag(PokeLab.Overworld.OverworldNames.PlayerTag);
            if (tagged != null) return tagged.transform.position;

            // No player at all is not a case worth staging for, but the camera at least knows
            // roughly where the scene is, which keeps the nearest-mark test meaningful.
            return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        }

        /// <summary>
        /// Builds the stage from the authored marks and the drawn props. Returns false when
        /// either is missing, which is the fallback's only trigger.
        /// </summary>
        private bool BuildStage()
        {
            // The player, rather than a mark, is the fixed point of this whole method. Wherever
            // the beat is playing, the person choosing is standing there.
            //
            // The marks are not fixed points, because the map is two scenes held in one world
            // space and both of them are loaded. 'Look_Case' is authored into Town for the
            // version of this scene that happened at the lab door, and it was the first name
            // tried — so at the lake it won, and the case, the three balls and the shot camera
            // were all built in the town square behind the player. What that looks like from
            // the player's chair is three labelled rings floating over nothing, which is
            // exactly how it was reported.
            var here = ResolvePlayerPosition();

            var caseMark = FindNearestMark(here, BagProp, CaseMark);
            if (caseMark == null)
            {
                Debug.LogWarning($"[Starter] Neither '{BagProp}' nor '{CaseMark}' is in any loaded " +
                                 "scene, so the case has nowhere to stand. Their positions are in " +
                                 "cast.json and LevelLayoutBuilder.BuildCast instantiates them; " +
                                 "falling back to the dialogue list.", this);
                return false;
            }

            // The ground comes off the object, and the ball line comes off the ground.
            //
            // Both numbers used to be taken from marks — the case's own Y for the balls and
            // the professor's mark for the ground — and the professor's mark is in the other
            // band, so the ground came back as the town's height and the case stood three
            // metres over the lake. The object standing here knows better: a drawn prop's
            // bounds say exactly what it is sitting on, and an empty needs a trace.
            //
            // The lid height is not read from the prop, though, and that is deliberate. The
            // balls stand in the briefcase this stage draws, not on top of whatever object
            // marks the spot — the bag prop is a squashed crate 0.34 m tall standing in for a
            // satchel, and putting the row on its top face buries it inside the drawn case.
            // cast.json authored Look_Case at 0.53 m over the ground for precisely this
            // reason: that is where the lid of a 0.59 m case is.
            var mark = caseMark.transform.position;
            var groundY = mark.y;
            var ballY = mark.y;

            var drawn = caseMark.GetComponentInChildren<Renderer>();
            if (drawn != null)
            {
                var bounds = drawn.bounds;
                mark = new Vector3(bounds.center.x, mark.y, bounds.center.z);
                groundY = bounds.min.y;
                ballY = groundY + CaseLidHeightMetres;
            }
            else
            {
                // An empty: its authored Y is already the ball line, and only the ground has
                // to be found.
                if (Physics.Raycast(mark + Vector3.up * 3f, Vector3.down, out var underfoot,
                                    30f, ~0, QueryTriggerInteraction.Ignore))
                    groundY = underfoot.point.y;
                else groundY = mark.y - CaseLidHeightMetres;
            }

            var ballLine = new Vector3(mark.x, ballY, mark.z);

            // The two flanking the case are the player and whoever came with them — not the
            // professor. He is not at this choice any more: the bag is his and he has gone.
            // With nobody else there the player is passed twice, which puts the camera on the
            // far side of the case from them and is the right shot for one person alone.
            var companion = GameObject.Find(CompanionActor);
            var flank = companion != null
                        && (companion.transform.position - here).sqrMagnitude < CompanionRange * CompanionRange
                ? companion.transform.position
                : here;

            var options = _selection.Options;
            var stage = StarterCaseStage.Create(ballLine, groundY, flank, here);

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

            // A hollow frame, emphatically not UiBuilder.OutlinedPanel: that sprite fills its
            // centre at 55% alpha, and at 170 px over the ball row it is an opaque-enough teal
            // slab covering the very ball being chosen — which is how the focused choice came
            // to be reported as "a plain teal rounded rect where the Pokémon should be". The
            // ring's whole job is to point at the ball, so the ball must stay visible inside it.
            var ring = UiBuilder.Image("Ring", root, UiSprites.Frame(84, 3), UiPalette.ScannerCyan);
            UiBuilder.Stretch(ring.rectTransform);
            ring.enabled = false;
            _rings[index] = ring;

            // No caption under the target any more. It printed the focused option's name a
            // second time directly under the card's own title — the middle ball projects to
            // the exact screen region the card occupied — and the side captions floated
            // through the blurb as bare text. The names now live in the fixed chip row under
            // the card, where they cannot chase the camera.

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
            var options = _selection.Options;

            // Top-centre, because bottom-centre was the bug. The close shot puts the ball row
            // at the exact centre of the screen, and the old card — bottom-anchored at 372
            // with 168 of height — put its title on the very same line: the focused ball's
            // 170 px target landed on the card, printed the name a second time under the
            // card's own title, and dragged the side captions through the blurb. Everything
            // the player reads now lives at the top of the frame, where the browse shot keeps
            // only sky, and the bottom band keeps a single hint clear of the dialogue scrim.
            var body = UiBuilder.Panel("Card", _canvasRect, UiPalette.Surface.WithAlpha(0.78f),
                20, shadow: false);
            var card = (RectTransform)body.transform.parent;
            UiBuilder.Anchor(card, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(560f, 250f));

            // The creature itself, resolved from the same sheets the battle billboards draw.
            // The balls are closed while the player browses — that is the fiction — so the
            // card is the one place the player can actually see what they are about to take.
            _portrait = UiBuilder.Image("Portrait", body.transform, null, Color.white);
            _portrait.rectTransform.anchorMin = new Vector2(0.5f, 1f);
            _portrait.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            _portrait.rectTransform.pivot = new Vector2(0.5f, 1f);
            _portrait.rectTransform.anchoredPosition = new Vector2(0f, -10f);
            _portrait.rectTransform.sizeDelta = new Vector2(132f, 132f);
            _portrait.preserveAspect = true;
            _portrait.enabled = false;

            _name = UiBuilder.Text("Name", body.transform, string.Empty, UiTextRole.Heading,
                UiPalette.TextPrimary, TMPro.TextAlignmentOptions.Center);
            _name.rectTransform.anchorMin = new Vector2(0f, 1f);
            _name.rectTransform.anchorMax = new Vector2(1f, 1f);
            _name.rectTransform.pivot = new Vector2(0.5f, 1f);
            _name.rectTransform.anchoredPosition = new Vector2(0f, -148f);
            _name.rectTransform.sizeDelta = new Vector2(0f, 46f);

            _blurb = UiBuilder.Text("Blurb", body.transform, string.Empty, UiTextRole.Secondary,
                UiPalette.TextSecondary, TMPro.TextAlignmentOptions.Center);
            _blurb.rectTransform.anchorMin = new Vector2(0f, 1f);
            _blurb.rectTransform.anchorMax = new Vector2(1f, 1f);
            _blurb.rectTransform.pivot = new Vector2(0.5f, 1f);
            _blurb.rectTransform.anchoredPosition = new Vector2(0f, -196f);
            _blurb.rectTransform.sizeDelta = new Vector2(-32f, 44f);

            // The three options as fixed chips under the card, in ball order, the focused one
            // framed. Fixed rather than tracked to the balls, so they can never drift into the
            // card while the camera blends; the hollow ring on the ball itself is what ties a
            // chip to the thing in the world.
            _chipRow = UiBuilder.Rect("Options", body.transform, false);
            UiBuilder.Anchor(_chipRow, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -262f), new Vector2(920f, 48f));

            _chipFrames = new Image[options.Count];
            _chipLabels = new TextMeshProUGUI[options.Count];
            var mid = (options.Count - 1) * 0.5f;
            for (var i = 0; i < options.Count; i++)
            {
                var chip = UiBuilder.Rect($"Chip{i}", _chipRow, false);
                UiBuilder.Anchor(chip, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2((i - mid) * 300f, 0f),
                    new Vector2(260f, 48f));

                var back = UiBuilder.Image("Back", chip, UiSprites.Panel(12),
                    UiPalette.SurfaceRaised.WithAlpha(0.85f));
                UiBuilder.Stretch(back.rectTransform);

                var frame = UiBuilder.Image("Focus", chip, UiSprites.Frame(12, 2),
                    UiPalette.ScannerCyan);
                UiBuilder.Stretch(frame.rectTransform);
                frame.enabled = false;
                _chipFrames[i] = frame;

                _chipLabels[i] = UiBuilder.Text("Label", chip, options[i].DisplayName,
                    UiTextRole.Caption, UiPalette.TextMuted, TMPro.TextAlignmentOptions.Center);
                UiBuilder.Stretch(_chipLabels[i].rectTransform);
            }

            // The hint alone stays at the bottom, just above the dialogue scrim (336 px on the
            // 1080 reference) and well below the ball row at screen centre.
            _hint = UiBuilder.Text("Hint", _canvasRect, PokeLab.Core.Loc.Get("starter.hint"),
                UiTextRole.Caption, UiPalette.TextMuted, TMPro.TextAlignmentOptions.Center);
            UiBuilder.Anchor(_hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 344f), new Vector2(900f, 32f));

            // The confirm takes the chip row's spot, directly under the question that has
            // replaced the blurb, so the ask and both answers read as one block — and none of
            // it sits over the creature standing at screen centre in the reveal framing.
            _confirmRow = UiBuilder.Rect("Confirm", body.transform, false);
            UiBuilder.Anchor(_confirmRow, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -262f), new Vector2(600f, 56f));
            UiBuilder.Horizontal(_confirmRow, 24f, alignment: TextAnchor.MiddleCenter);
            _confirmRow.gameObject.SetActive(false);

            _takeButton = BuildAnswer("Take", string.Empty, 1, out _takeLabel);
            BuildAnswer("Back", PokeLab.Core.Loc.Get("starter.answer.no"), -1, out _);
        }

        private Button BuildAnswer(string name, string text, int answer, out TextMeshProUGUI label)
        {
            var slab = UiBuilder.Rect(name, _confirmRow, false);
            UiBuilder.Size(slab, 280f, 56f);

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

        // --- Creature art ----------------------------------------------------------------------

        /// <summary>
        /// Cut sprites, cached per species: <c>Sprite.Create</c> allocates every call and
        /// <see cref="Select"/> runs on every cursor move. Destroyed with the screen.
        /// </summary>
        private readonly Dictionary<int, Sprite> _frontSprites = new Dictionary<int, Sprite>();

        /// <summary>
        /// The front view of a species, cut from the same sheet the battle billboards draw.
        ///
        /// Resolved through <see cref="CreatureSpriteLibrary"/> — the GAME id into
        /// sprite_manifest.json into Resources/PokeLabSprites — because that is the one
        /// resolution path in this project known to work: it is how every creature in a battle
        /// gets its picture. The frame is cell 0 of the idle sheet, cut the same way
        /// DialoguePresenter cuts a speaker portrait; a manifest with no entry, or an entry
        /// whose texture is missing, degrades to a card with a name and no picture rather
        /// than to a blank rectangle.
        /// </summary>
        private Sprite FrontSprite(int speciesId)
        {
            if (_frontSprites.TryGetValue(speciesId, out var cached)) return cached;

            Sprite sprite = null;
            var entry = CreatureSpriteLibrary.Shared.Entry(speciesId);
            if (entry != null)
            {
                var sheet = entry.frontAnim != null && entry.frontAnim.IsUsable ? entry.frontAnim : null;
                var texture = CreatureSpriteLibrary.Shared.Texture(
                    sheet != null ? sheet.texture : entry.front);
                if (texture != null)
                {
                    var columns = Mathf.Max(1, sheet != null ? sheet.columns : 1);
                    var rows = Mathf.Max(1, sheet != null ? sheet.rows : 1);
                    var cell = sheet != null ? sheet.CellAt(0) : 0;
                    var cellW = texture.width / columns;
                    var cellH = texture.height / rows;
                    var column = cell % columns;
                    var row = cell / columns;

                    // Row 0 is the top of the sheet; Sprite rects count up from the bottom.
                    var rect = new Rect(column * cellW, texture.height - (row + 1) * cellH,
                        cellW, cellH);
                    sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), cellH);
                    sprite.name = "~StarterFront_" + speciesId;
                }
            }

            if (sprite == null)
            {
                Debug.LogWarning($"[Starter] No front sprite resolved for game species " +
                                 $"{speciesId}, so the card shows the name without a picture. " +
                                 "The entry belongs in sprite_manifest.json under the GAME id " +
                                 "(not the dex number), with its texture under " +
                                 "Resources/PokeLabSprites.", this);
            }

            _frontSprites[speciesId] = sprite;
            return sprite;
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
            if (_portrait != null)
            {
                var sprite = FrontSprite(options[index].SpeciesId);
                _portrait.sprite = sprite;
                // Disabled rather than left as a Solid-sprite fill: an Image with no real
                // sprite draws a flat rectangle, which is the placeholder this fix removes.
                _portrait.enabled = sprite != null;
            }

            for (var i = 0; i < _rings.Length; i++)
            {
                _rings[i].enabled = i == index;
                if (_chipFrames != null && i < _chipFrames.Length && _chipFrames[i] != null)
                    _chipFrames[i].enabled = i == index;
                if (_chipLabels != null && i < _chipLabels.Length && _chipLabels[i] != null)
                    _chipLabels[i].color = i == index ? UiPalette.TextPrimary : UiPalette.TextMuted;
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
            // The chips belong to browsing; the confirm row takes their place while a ball is
            // open, so the two can never stack in the same band the way the old hint and
            // confirm row did.
            if (_chipRow != null) _chipRow.gameObject.SetActive(browsing);
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
            _buttons = null;
            _flash = null;
            _portrait = null;
            _chipRow = null;
            _chipFrames = null;
            _chipLabels = null;
            _confirmRow = null;
            _takeButton = null;
            _takeLabel = null;
            _name = null;
            _blurb = null;
            _hint = null;

            // The cut sprites were created for this screen; the textures they reference are
            // the shared library's and stay.
            foreach (var sprite in _frontSprites.Values)
                if (sprite != null) Destroy(sprite);
            _frontSprites.Clear();

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

            // Speaker-less here too. This is the same moment drawn worse, not a different one,
            // and the professor is no more present in it.
            _view.ShowChoices(string.Empty, PokeLab.Core.Loc.Get(_promptKey),
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
