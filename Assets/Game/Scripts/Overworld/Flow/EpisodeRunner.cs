using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PokeLab.Core;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Overworld
{
    /// <summary>
    /// What a single step of a scripted scene does.
    ///
    /// Deliberately small. Every beat here exists because the opening needs it; a beat
    /// set that anticipates scenes nobody has written is a schema nobody can satisfy,
    /// and the data would end up describing the runner rather than the scene.
    /// </summary>
    public enum EpisodeBeatKind
    {
        None = 0,
        /// <summary>Cover the screen. `seconds` is the fade length.</summary>
        FadeOut = 1,
        /// <summary>Reveal. `seconds` is the fade length.</summary>
        FadeIn = 2,
        /// <summary>Hold. `seconds`.</summary>
        Wait = 3,
        /// <summary>Play a dialogue sequence by id and block until it ends.</summary>
        Dialogue = 4,
        /// <summary>Take control away from the player and put the flow in Cutscene.</summary>
        TakeControl = 5,
        /// <summary>Give control back and return the flow to Exploring.</summary>
        GiveControl = 6,
        /// <summary>Move a named actor to a named marker, walking, and block until it arrives.</summary>
        MoveActor = 7,
        /// <summary>Frame the camera on a named marker.</summary>
        CameraTo = 8,
        /// <summary>Open the starter choice and block until the player commits.</summary>
        ChooseStarter = 9,
        /// <summary>Put an item in the bag. `id`, `amount`.</summary>
        GiveItem = 10,
        /// <summary>Set a progression flag, which is what gates later beats and doors.</summary>
        SetFlag = 11,
        /// <summary>Start a trainer battle by trainer id and block until it resolves.</summary>
        Battle = 12,
        /// <summary>
        /// Ask the player their name and write it to the profile. `id` is the default used when
        /// the player confirms an empty field — or, today, when nothing in the project can ask.
        /// </summary>
        AskName = 13,
    }

    [Serializable]
    public sealed class EpisodeBeat
    {
        public EpisodeBeatKind Kind;
        /// <summary>Dialogue id, actor name, marker name, item id, flag name, trainer id.</summary>
        public string Id;
        public string Target;
        public float Seconds = 0.4f;
        public int Amount = 1;
        public bool Value = true;
    }

    [Serializable]
    public sealed class Episode
    {
        public string Id;
        public string DisplayName;
        /// <summary>Progression flag that means this episode has already run.</summary>
        public string CompletionFlag;
        public List<EpisodeBeat> Beats = new List<EpisodeBeat>();
    }

    [Serializable]
    public sealed class EpisodeBook
    {
        public List<Episode> Episodes = new List<Episode>();
    }

    /// <summary>
    /// Runs an authored scene, beat by beat.
    ///
    /// The opening is data rather than code because it is the part of the game most
    /// likely to be rewritten: the order of "wake up, get dragged out, choose, fight"
    /// is a design decision, not a program. What the runner owns is the part that is
    /// genuinely mechanical — taking control away and giving it back exactly once each,
    /// never leaving the flow in Cutscene if a beat fails, and never running an episode
    /// twice on a save that has already seen it.
    ///
    /// Beats block. A scripted scene whose steps overlap is not a scene, it is a race,
    /// and the failure looks like a camera move that lands mid-sentence.
    ///
    /// Every beat that waits on something outside this file — a wipe, a conversation, a
    /// walk, a battle — waits with a ceiling on it and continues past a dependency that
    /// never answers. The dependencies are all optional and several of them are absent
    /// today, so the choice is not between a perfect scene and a degraded one; it is
    /// between a degraded scene and a player standing frozen in the plaza with nothing
    /// in the log to say why.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EpisodeRunner : MonoBehaviour
    {
        [Tooltip("Authored episodes, relative to the project root. Read at Awake so the " +
                 "opening can be rewritten without touching code or the scene.")]
        [SerializeField] private string _bookPath = "Assets/Game/Data/Story/episodes.json";

        [Tooltip("Authored dialogue, relative to the project root. Beat ids key into it.")]
        [SerializeField] private string _dialoguePath = "Assets/Game/Data/Story/dialogue.json";

        [Tooltip("Episode to play when a new game starts and its completion flag is unset.")]
        [SerializeField] private string _openingEpisodeId = "opening";

        [Tooltip("Run the opening automatically on a fresh profile.")]
        [SerializeField] private bool _autoPlayOpening = true;

        [SerializeField] private StarterSelection _starterSelection;
        [SerializeField] private OverworldCameraRig _cameraRig;
        [Tooltip("Speaks the Dialogue beats. Found in the scene when empty.")]
        [SerializeField] private DialogueRunner _dialogueRunner;

        [Header("Actors")]
        [Tooltip("Metres per second an actor with no NavMeshAgent is walked at.")]
        [SerializeField] private float _actorWalkSpeed = 2.6f;
        [Tooltip("How close to a marker counts as arrived.")]
        [SerializeField] private float _arriveDistance = 0.35f;
        [Tooltip("Fallback walk timeout for a MoveActor beat that authored no seconds.")]
        [SerializeField] private float _defaultMoveTimeoutSeconds = 8f;

        [Header("Ceilings")]
        [Tooltip("Seconds a wipe may take before the beat gives up on it and forces the screen " +
                 "to the state the beat asked for.")]
        [SerializeField] private float _fadeTimeoutSeconds = 8f;
        [Tooltip("Seconds a single line may sit on screen unchanged before the runner advances " +
                 "the conversation itself. Only ever reached when nothing is presenting it, so " +
                 "keep it well above the longest authored AutoAdvanceSeconds — set below one and " +
                 "this stops being a safety net and starts being the pacing.")]
        [SerializeField] private float _dialogueStallSeconds = 15f;
        [Tooltip("Hard ceiling on a whole Dialogue beat.")]
        [SerializeField] private float _dialogueBeatTimeoutSeconds = 180f;
        [Tooltip("Seconds the starter choice waits for the player before it picks for them.")]
        [SerializeField] private float _starterChoiceTimeoutSeconds = 90f;
        [Tooltip("Seconds a scripted battle may run before the episode abandons it. Generous: " +
                 "this is a last resort, not a pacing control.")]
        [SerializeField] private float _battleTimeoutSeconds = 600f;

        private readonly Dictionary<string, Episode> _episodes = new Dictionary<string, Episode>();
        private readonly HashSet<string> _flags = new HashSet<string>();
        private DialogueBook _dialogue;
        private Coroutine _running;

        /// <summary>Mirrors the last <see cref="SetControl"/> call, so a beat that hands control
        /// away to another system can put the cutscene freeze back afterwards.</summary>
        private bool _controlHeld;

        // The cinematics layer's wipe, resolved once by reflection. See ResolveOverlay.
        private Component _overlay;
        private bool _overlaySearched;

        /// <summary>The profile field AskName knows how to write.</summary>
        private const string TrainerNameField = "profile.trainer_name";

        /// <summary>Replaced with the player's name in every line presented from the book.</summary>
        private const string PlayerToken = "{PLAYER}";

        public bool IsPlaying => _running != null;
        public event Action<string> EpisodeFinished;

        private void Awake()
        {
            LoadBook();
            _dialogue = DialogueBook.Load(_dialoguePath);
        }

        private void Start()
        {
            if (!_autoPlayOpening) return;
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile == null) return;

            // The completion flag, read through the profile, is the only test that survives a
            // save. It replaces a check on the party being empty, which cannot work in this
            // scene: PlayerProfileHost.NewGame hands every fresh profile a debug Charmander
            // before this component's Start runs, so "has a party" was true on the very first
            // frame of a new game and the opening never played once.
            if (_episodes.TryGetValue(_openingEpisodeId, out var opening)
                && IsFlagSet(opening.CompletionFlag)) return;

            // A session restored from a save has had its opening whatever the flags say — that
            // covers saves written before the flag was persisted, without a save migration.
            if (SessionBeganFromSave() && profile.Party != null && profile.Party.Count > 0) return;

            Play(_openingEpisodeId);
        }

        private void LoadBook()
        {
            _episodes.Clear();
            var path = Path.Combine(Directory.GetCurrentDirectory(), _bookPath);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Episode] No episode book at {_bookPath}. The opening will " +
                                 "not play, and a new game will start with the player standing " +
                                 "in the plaza with no party and nothing to do.", this);
                return;
            }

            var book = JsonUtility.FromJson<EpisodeBook>(File.ReadAllText(path));
            foreach (var episode in book?.Episodes ?? new List<Episode>())
            {
                if (string.IsNullOrEmpty(episode.Id)) continue;
                _episodes[episode.Id] = episode;
            }
        }

        public bool Play(string episodeId)
        {
            if (_running != null)
            {
                Debug.LogWarning($"[Episode] '{episodeId}' was asked for while '{_playingId}' " +
                                 "is still running; ignored. Two scripted scenes at once is a " +
                                 "race, not a scene.", this);
                return false;
            }
            if (!_episodes.TryGetValue(episodeId, out var episode))
            {
                Debug.LogWarning($"[Episode] No episode '{episodeId}' in the book.", this);
                return false;
            }
            if (IsFlagSet(episode.CompletionFlag)) return false;

            _playingId = episodeId;
            _running = StartCoroutine(Run(episode));
            return true;
        }

        private string _playingId;

        private IEnumerator Run(Episode episode)
        {
            var tookControl = false;
            try
            {
                foreach (var beat in episode.Beats)
                {
                    if (beat.Kind == EpisodeBeatKind.TakeControl) tookControl = true;
                    if (beat.Kind == EpisodeBeatKind.GiveControl) tookControl = false;

                    var step = Perform(beat);
                    while (step.MoveNext()) yield return step.Current;
                }
            }
            finally
            {
                // Control is returned even if a beat threw or the episode was cut short.
                // The alternative is a player who cannot move and no message saying why,
                // which is the worst failure this system can have.
                if (tookControl) SetControl(true);
                if (!string.IsNullOrEmpty(episode.CompletionFlag)) SetFlag(episode.CompletionFlag, true);
                _running = null;
                _playingId = null;
                EpisodeFinished?.Invoke(episode.Id);
            }
        }

        private IEnumerator Perform(EpisodeBeat beat)
        {
            switch (beat.Kind)
            {
                case EpisodeBeatKind.Wait:
                    yield return new WaitForSeconds(Mathf.Max(0f, beat.Seconds));
                    break;

                case EpisodeBeatKind.TakeControl:
                    SetControl(false);
                    break;

                case EpisodeBeatKind.GiveControl:
                    SetControl(true);
                    break;

                case EpisodeBeatKind.SetFlag:
                    SetFlag(beat.Id, beat.Value);
                    break;

                case EpisodeBeatKind.GiveItem:
                    if (ServiceHub.TryGet<IPlayerProfile>(out var bag) && bag != null)
                        bag.AddItem(beat.Id, Mathf.Max(1, beat.Amount));
                    break;

                case EpisodeBeatKind.CameraTo:
                    var marker = FindMarker(beat.Id);
                    if (marker == null)
                        Debug.LogWarning($"[Episode] CameraTo '{beat.Id}': no such object in the " +
                                         "scene. Its world position is in cast.json; until the " +
                                         "marker exists the shot is whatever the camera was " +
                                         "already looking at.", this);
                    else if (_cameraRig != null) _cameraRig.LookToward(marker.position);
                    break;

                case EpisodeBeatKind.ChooseStarter:
                    yield return RunStarterChoice();
                    break;

                case EpisodeBeatKind.FadeOut:
                    yield return RunFade(cover: true, beat.Seconds);
                    break;

                case EpisodeBeatKind.FadeIn:
                    yield return RunFade(cover: false, beat.Seconds);
                    break;

                case EpisodeBeatKind.Dialogue:
                    yield return RunDialogue(beat);
                    break;

                case EpisodeBeatKind.MoveActor:
                    yield return RunMoveActor(beat);
                    break;

                case EpisodeBeatKind.AskName:
                    RunAskName(beat);
                    break;

                case EpisodeBeatKind.Battle:
                    yield return RunBattle(beat);
                    break;

                default:
                    // A kind the data knows and this runner does not. Named by number as well as
                    // by name because the number is what the JSON actually carries.
                    Debug.LogWarning($"[Episode] Beat kind {(int)beat.Kind} ('{beat.Id}') is not a " +
                                     "kind this runner knows, and was skipped.", this);
                    break;
            }
        }

        // --- Fades ---------------------------------------------------------------------------

        /// <summary>
        /// Covers or reveals the screen with the cinematics layer's own wipe.
        ///
        /// Reached through <see cref="ITransitionDirector"/> and then by reflection, for the
        /// reason <see cref="ServiceBridge"/> exists: the wipe lives in PokeLab.Cinematics,
        /// which this assembly deliberately cannot see. The encounter entry points are not used
        /// — those swing the camera around the player and stage a battle behind the cover, and
        /// a FadeOut beat wants the wipe and nothing else.
        /// </summary>
        private IEnumerator RunFade(bool cover, float seconds)
        {
            var overlay = ResolveOverlay();
            if (overlay == null)
            {
                Debug.LogWarning($"[Episode] {(cover ? "FadeOut" : "FadeIn")}: no " +
                                 "ScreenTransitionOverlay in the scene, so there is nothing to " +
                                 "fade. Add the cinematics layer's TransitionDirector to get the " +
                                 "wipe; the scene plays on without it, just harder.", this);
                yield break;
            }

            var routine = BuildWipe(overlay, cover, Mathf.Max(0f, seconds));
            if (routine == null) yield break;

            // On abandonment the screen is forced to the state the beat asked for. A fade that
            // stalls half-covered is indistinguishable from a crash, and a fade-in that stalls
            // leaves the player playing behind a black pane.
            yield return RunBounded(routine, _fadeTimeoutSeconds + Mathf.Max(0f, seconds),
                (cover ? "FadeOut" : "FadeIn") + " wipe",
                () => SetCoverageDirect(overlay, cover ? 1f : 0f));
        }

        /// <summary>
        /// Binds the overlay's own cover/reveal sequence. Returns null and warns if the component
        /// found does not have the shape this expects, rather than throwing inside a beat.
        /// </summary>
        private IEnumerator BuildWipe(Component overlay, bool cover, float seconds)
        {
            var name = cover ? "CoverIn" : "CoverOut";
            var method = overlay.GetType().GetMethod(name,
                BindingFlags.Public | BindingFlags.Instance);
            var parameters = method?.GetParameters();

            if (method == null || parameters.Length < 1)
            {
                Debug.LogWarning($"[Episode] {overlay.GetType().Name} has no {name}(float, style); " +
                                 "the fade beat cannot drive it.", this);
                return null;
            }

            var args = new object[parameters.Length];
            args[0] = seconds;
            // The style enum is declared in an assembly this one cannot reference, so the value
            // is built from the parameter's own type. Zero is WipeStyle.Fade: a plain fade is
            // what a FadeOut beat means, and the shutter is the encounter's signature, not the
            // opening's.
            for (var i = 1; i < parameters.Length; i++)
            {
                args[i] = parameters[i].ParameterType.IsEnum
                    ? Enum.ToObject(parameters[i].ParameterType, 0)
                    : parameters[i].DefaultValue;
            }

            return method.Invoke(overlay, args) as IEnumerator;
        }

        private void SetCoverageDirect(Component overlay, float coverage)
        {
            var method = overlay.GetType().GetMethod("SetCoverage",
                BindingFlags.Public | BindingFlags.Instance);
            method?.Invoke(overlay, new object[] { coverage, Color.black });
        }

        /// <summary>
        /// Finds the wipe. It lives on the transition director's own GameObject — the director
        /// adds one there when the scene has none — so that is looked at first, and the scene is
        /// only swept when no director is registered at all. Searched once either way: a scene
        /// with no cinematics layer must not pay for a full sweep on every fade beat.
        /// </summary>
        private Component ResolveOverlay()
        {
            if (_overlay != null) return _overlay;
            if (_overlaySearched) return null;
            _overlaySearched = true;

            if (ServiceHub.TryGet<ITransitionDirector>(out var director) && director is Component host)
            {
                foreach (var component in host.GetComponents<Component>())
                {
                    if (component == null || component.GetType().Name != "ScreenTransitionOverlay") continue;
                    _overlay = component;
                    return _overlay;
                }
            }

            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour == null || behaviour.GetType().Name != "ScreenTransitionOverlay") continue;
                _overlay = behaviour;
                return _overlay;
            }

            return null;
        }

        // --- Dialogue ------------------------------------------------------------------------

        /// <summary>
        /// Plays one authored conversation and blocks until it ends.
        ///
        /// A missing DialogueRunner skips the beat rather than holding it. The runner presents a
        /// line and waits to be told to advance; with nothing drawing the line there is nobody to
        /// press anything, so holding would stop the episode here permanently and leave the
        /// player frozen in the middle of the opening.
        /// </summary>
        private IEnumerator RunDialogue(EpisodeBeat beat)
        {
            var sequenceId = ResolveDialogueId(beat);
            var runner = ResolveDialogueRunner();
            if (runner == null)
            {
                Debug.LogWarning($"[Episode] Dialogue '{sequenceId}' has nowhere to play: no " +
                                 "DialogueRunner in the scene. The beat is skipped, so the scene " +
                                 "plays silent and short until one is wired up.", this);
                yield break;
            }

            var sequence = _dialogue != null ? _dialogue.Build(sequenceId, Substitute) : null;
            if (sequence == null)
            {
                Debug.LogWarning($"[Episode] No dialogue sequence '{sequenceId}' in {_dialoguePath}. " +
                                 "The beat that needs it was skipped.", this);
                yield break;
            }

            if (!runner.Play(sequence, gameObject))
            {
                Debug.LogWarning($"[Episode] DialogueRunner refused '{sequenceId}' — it is already " +
                                 "playing something. The beat was skipped rather than queued, " +
                                 "because two conversations at once is the race this runner exists " +
                                 "to prevent.", this);
                Destroy(sequence);
                yield break;
            }

            yield return WaitForDialogue(runner, sequenceId);
            Destroy(sequence);
        }

        /// <summary>
        /// Waits out a conversation, nudging it along if it stops making progress.
        ///
        /// Two clocks on purpose. The stall watchdog runs on scaled time so a deliberately frozen
        /// world — a transition holding time at zero — is not mistaken for a line nobody
        /// answered. The ceiling runs on unscaled time, because a world frozen forever is exactly
        /// the case the ceiling is for.
        /// </summary>
        private IEnumerator WaitForDialogue(DialogueRunner runner, string sequenceId)
        {
            var total = 0f;
            var onThisLine = 0f;
            var shown = runner.CurrentLine.Text;
            var warned = false;

            while (runner.IsPlaying && total < _dialogueBeatTimeoutSeconds)
            {
                total += Time.unscaledDeltaTime;

                if (!string.Equals(runner.CurrentLine.Text, shown, StringComparison.Ordinal))
                {
                    shown = runner.CurrentLine.Text;
                    onThisLine = 0f;
                    yield return null;
                    continue;
                }

                onThisLine += Time.deltaTime;
                if (onThisLine >= _dialogueStallSeconds)
                {
                    if (!warned)
                    {
                        Debug.LogWarning($"[Episode] '{sequenceId}' has sat on the same line for " +
                                         $"{_dialogueStallSeconds:0}s. Nothing is presenting it, so " +
                                         "the runner is advancing it itself — the scene keeps its " +
                                         "shape and the player keeps their control.", this);
                        warned = true;
                    }
                    // A choice cannot be advanced past, only answered. The first branch is taken
                    // because the opening's only choice is cosmetic and both options land on the
                    // same line; an unanswered one would burn the whole ceiling instead.
                    if (runner.CurrentLine.HasChoices) runner.Choose(0);
                    else runner.Advance();
                    onThisLine = 0f;
                }

                yield return null;
            }

            if (!runner.IsPlaying) yield break;

            Debug.LogError($"[Episode] '{sequenceId}' was still running after " +
                           $"{_dialogueBeatTimeoutSeconds:0}s and has been ended so the episode can " +
                           "finish and hand control back.", this);
            runner.End();
        }

        /// <summary>
        /// The sequence id a Dialogue beat resolves to. `Target: "starter"` suffixes the id with
        /// the chosen species, which is how one beat reaches one of three authored reactions.
        /// </summary>
        private string ResolveDialogueId(EpisodeBeat beat)
        {
            if (beat.Target != "starter") return beat.Id;

            var choice = _starterSelection != null ? _starterSelection.Choice : null;
            if (choice == null)
            {
                Debug.LogWarning($"[Episode] Dialogue '{beat.Id}' is keyed on the chosen starter, " +
                                 "but nothing has been chosen. Falling back to the unsuffixed id.", this);
                return beat.Id;
            }
            return beat.Id + "_" + choice.SpeciesId;
        }

        private DialogueRunner ResolveDialogueRunner()
        {
            if (_dialogueRunner != null) return _dialogueRunner;
            _dialogueRunner = DialogueRunner.Instance != null
                ? DialogueRunner.Instance
                : FindAnyObjectByType<DialogueRunner>(FindObjectsInactive.Exclude);
            return _dialogueRunner;
        }

        // --- Name entry ----------------------------------------------------------------------

        /// <summary>
        /// Writes the player's name to the profile.
        ///
        /// There is no text entry anywhere in the project, so this takes the beat's authored
        /// default and says so. It does not wait for a UI that does not exist: a modal prompt
        /// nothing can dismiss is a game over on the first screen.
        /// </summary>
        private void RunAskName(EpisodeBeat beat)
        {
            var chosen = string.IsNullOrWhiteSpace(beat.Id) ? "Ren" : beat.Id.Trim();

            if (!string.IsNullOrEmpty(beat.Target) && beat.Target != TrainerNameField)
                Debug.LogWarning($"[Episode] AskName was pointed at '{beat.Target}'. The only " +
                                 $"profile field this beat can write is '{TrainerNameField}'; the " +
                                 "name went there.", this);

            var profile = ResolveProfile();
            if (profile == null)
            {
                Debug.LogWarning("[Episode] AskName with no PlayerProfile registered. The name was " +
                                 "not recorded, and every {PLAYER} in the script will read as the " +
                                 "fallback.", this);
                return;
            }

            // SEAM: real name entry goes here. Replace this method's body with a coroutine that
            // opens the UI worker's entry widget, waits for it under a ceiling of its own, and
            // falls back to `chosen` on timeout or on an empty confirmation — the rest of the
            // scene already reads the name back out of the profile, so nothing else changes.
            profile.SetTrainerName(chosen);
            Debug.LogWarning($"[Episode] AskName fell through to its authored default '{chosen}': " +
                             "nothing in the project offers text entry yet. The scene continues " +
                             "with that name rather than waiting for a prompt that cannot appear.", this);
        }

        // --- Actors --------------------------------------------------------------------------

        /// <summary>
        /// Walks an actor to a marker and blocks until it arrives.
        ///
        /// `Seconds` on the beat is a timeout rather than a duration, as the data documents: a
        /// walk that has not finished by then is snapped and the scene carries on, so blocked
        /// geometry or a missing NavMesh can never strand a cutscene.
        /// </summary>
        private IEnumerator RunMoveActor(EpisodeBeat beat)
        {
            var actor = FindActor(beat.Id);
            if (actor == null)
            {
                Debug.LogWarning($"[Episode] MoveActor: no actor '{beat.Id}' in the scene. The beat " +
                                 "was skipped; whoever was meant to walk simply is not there, and " +
                                 "the lines they speak later will play from wherever the camera is.", this);
                yield break;
            }

            var marker = FindMarker(beat.Target);
            if (marker == null)
            {
                Debug.LogWarning($"[Episode] MoveActor: no marker '{beat.Target}' in the scene, so " +
                                 $"'{beat.Id}' stayed put. The marker's world position is in " +
                                 "cast.json; the beat degrades rather than blocking on a walk to " +
                                 "nowhere.", this);
                yield break;
            }

            var timeout = beat.Seconds > 0.01f ? beat.Seconds : _defaultMoveTimeoutSeconds;
            var body = actor.transform;
            // The marker's Y came off a path control point rather than the terrain, so it is
            // trusted for where to stand and not for how high to stand there.
            var destination = new Vector3(marker.position.x, body.position.y, marker.position.z);

            var agent = actor.GetComponent<NavMeshAgent>();
            var locomotion = actor.GetComponent<PlayerLocomotion>();
            var arrived = false;
            var elapsed = 0f;

            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.stoppingDistance = _arriveDistance;
                agent.SetDestination(marker.position);

                while (elapsed < timeout)
                {
                    elapsed += Time.deltaTime;
                    if (!agent.pathPending && agent.remainingDistance <= _arriveDistance)
                    {
                        arrived = true;
                        break;
                    }
                    yield return null;
                }

                agent.isStopped = true;
                agent.ResetPath();
            }
            else
            {
                while (elapsed < timeout)
                {
                    elapsed += Time.deltaTime;
                    var next = Vector3.MoveTowards(body.position, destination,
                        _actorWalkSpeed * Time.deltaTime);
                    var facing = Flatten(destination - body.position);
                    PlaceActor(body, locomotion, next,
                        facing.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(facing) : body.rotation);

                    if (Flatten(destination - body.position).magnitude <= _arriveDistance)
                    {
                        arrived = true;
                        break;
                    }
                    yield return null;
                }
            }

            if (!arrived)
            {
                Debug.LogWarning($"[Episode] '{beat.Id}' did not reach '{beat.Target}' within " +
                                 $"{timeout:0.0}s and was snapped there. The scene needs them on " +
                                 "that mark for the next beat to frame.", this);
                PlaceActor(body, locomotion, destination, body.rotation);
            }

            yield return FacePlayer(actor);
        }

        /// <summary>
        /// Moves a body. The player goes through <see cref="PlayerLocomotion.Warp"/> because a
        /// CharacterController keeps its own copy of its position and writing the transform
        /// underneath it snaps back on the next frame it moves.
        /// </summary>
        private static void PlaceActor(Transform body, PlayerLocomotion locomotion,
            Vector3 position, Quaternion rotation)
        {
            if (locomotion != null) locomotion.Warp(position, rotation);
            else body.SetPositionAndRotation(position, rotation);
        }

        /// <summary>Turns an actor to face the player on arrival. Talking to a shoulder reads as broken.</summary>
        private IEnumerator FacePlayer(GameObject actor)
        {
            var player = FindActor("Player");
            if (player == null || player == actor) yield break;

            var body = actor.transform;
            var direction = Flatten(player.transform.position - body.position);
            if (direction.sqrMagnitude < 1e-4f) yield break;

            var target = Quaternion.LookRotation(direction);
            var elapsed = 0f;
            while (elapsed < 0.35f)
            {
                elapsed += Time.deltaTime;
                body.rotation = Quaternion.Slerp(body.rotation, target, elapsed / 0.35f);
                yield return null;
            }
            body.rotation = target;
        }

        private static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private static GameObject FindActor(string actorName) =>
            string.IsNullOrEmpty(actorName) ? null : GameObject.Find(actorName);

        // --- Battle --------------------------------------------------------------------------

        /// <summary>
        /// Hands a scripted fight to the game flow and blocks until it comes back.
        ///
        /// The flow owns the whole round trip — cover, stage, resolve, restore, reveal — and it
        /// already degrades when no battle stage is registered, so this waits on its callback
        /// rather than on a battle. The wait is still bounded: a battle layer that accepts the
        /// request and never answers would otherwise hold the episode, and the episode holds the
        /// player's control.
        /// </summary>
        private IEnumerator RunBattle(EpisodeBeat beat)
        {
            if (!ServiceHub.TryGet<IGameFlow>(out var flow) || flow == null)
            {
                Debug.LogWarning($"[Episode] Battle '{beat.Id}': no IGameFlow is registered, so " +
                                 "there is nothing to hand the fight to. The beat is skipped and " +
                                 "the episode continues to the lines that follow it.", this);
                yield break;
            }

            var player = FindActor("Player");
            var body = player != null ? player.transform : transform;
            var rng = new DeterministicRandom(DeterministicRandom.HashString(beat.Id ?? "battle"));

            var request = new EncounterRequest
            {
                Kind = BattleKind.Trainer,
                TrainerId = beat.Id,
                WorldPosition = body.position,
                PlayerRotation = body.rotation,
                Seed = rng.NextSeed(),
            };

            if (beat.Target == "starter_counter")
            {
                var choice = _starterSelection != null ? _starterSelection.Choice : null;
                if (choice == null)
                    Debug.LogWarning($"[Episode] Battle '{beat.Id}' wanted the counter to the " +
                                     "player's starter, but nothing has been chosen. The trainer " +
                                     "will field whatever trainers.json gives them.", this);
                // The only field in the request that names a creature. A battle layer that reads
                // it fields the counter; one that does not falls back to the trainer's fixed list,
                // which is a worse fight rather than a broken one.
                else request.WildSpeciesId = choice.RivalCounterSpeciesId;
            }

            var resolved = false;
            flow.RequestEncounter(request, _ => resolved = true);

            var elapsed = 0f;
            while (!resolved && elapsed < _battleTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!resolved)
                Debug.LogError($"[Episode] Battle '{beat.Id}' never reported a result after " +
                               $"{_battleTimeoutSeconds:0}s. The episode is continuing without it " +
                               "rather than holding the player in a cutscene forever.", this);

            // The flow hands control back to the player when it returns, which is right for a
            // wild encounter and wrong in the middle of a scripted scene. Put the freeze back.
            if (_controlHeld) SetControl(false);
        }

        // --- Starter -------------------------------------------------------------------------

        /// <summary>
        /// True while the runner is waiting for the player to pick a starter.
        ///
        /// The presentation layer needs to know it is being asked, and StarterSelection only
        /// raises the answer. Without this the choice screen has no cue to appear on, and
        /// the moment the whole opening builds to resolves on a timeout.
        /// </summary>
        public bool IsAwaitingStarter { get; private set; }

        private IEnumerator RunStarterChoice()
        {
            if (_starterSelection == null)
            {
                Debug.LogError("[Episode] ChooseStarter with no StarterSelection assigned. " +
                               "The player would be given nothing and the game would " +
                               "continue as if they had chosen.", this);
                yield break;
            }

            var done = false;
            void OnChosen(StarterOption _) => done = true;
            _starterSelection.Chosen += OnChosen;
            IsAwaitingStarter = true;

            // Presentation subscribes to StarterSelection and calls Choose; the runner
            // only waits. That keeps the moment independent of how it is drawn.
            var elapsed = 0f;
            while (!done && elapsed < _starterChoiceTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            _starterSelection.Chosen -= OnChosen;
            IsAwaitingStarter = false;

            if (!done)
            {
                // Nothing is drawing the desk, so nothing can call Choose. Picking for the player
                // is bad; leaving them at the gate with no party, unable to battle, unable to
                // pass Bram and with no message saying why is worse, and unrecoverable.
                Debug.LogError($"[Episode] Nothing offered the starter choice within " +
                               $"{_starterChoiceTimeoutSeconds:0}s. Taking the first ball on the " +
                               "player's behalf so the game can continue — wire a presenter to " +
                               "StarterSelection.Chosen to make this the player's decision.", this);
                if (!_starterSelection.Choose(0)) yield break;
            }

            _starterSelection.Commit(ResolveTrainerName());
        }

        // --- Flags, profile, control ---------------------------------------------------------

        /// <summary>
        /// Records a progression flag in memory and on the profile.
        ///
        /// Both, because they answer different questions: the in-memory set is what this run of
        /// the scene reads, and the profile is what a save reads. Held only in memory, an
        /// episode's completion flag dies with the scene and the opening replays every time the
        /// town is re-entered.
        /// </summary>
        private void SetFlag(string key, bool on)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (on) _flags.Add(key);
            else _flags.Remove(key);
            ResolveProfile()?.SetFlagBool(key, on);
        }

        private bool IsFlagSet(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (_flags.Contains(key)) return true;
            var profile = ResolveProfile();
            return profile != null && profile.GetFlagBool(key);
        }

        private static PlayerProfile ResolveProfile() =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete
                ? concrete
                : null;

        /// <summary>
        /// Whether this session was restored from a save rather than started fresh. With no
        /// profile host there is no way to tell the two apart, so the conservative reading wins
        /// and the caller falls back to treating an existing party as proof.
        /// </summary>
        private static bool SessionBeganFromSave()
        {
            var host = FindAnyObjectByType<PlayerProfileHost>(FindObjectsInactive.Include);
            return host == null || host.LoadedFromSave;
        }

        private static string ResolveTrainerName() =>
            ServiceHub.TryGet<IPlayerProfile>(out var p) && !string.IsNullOrEmpty(p?.TrainerName)
                ? p.TrainerName
                : "Player";

        private static string Substitute(string text) =>
            string.IsNullOrEmpty(text) || text.IndexOf(PlayerToken, StringComparison.Ordinal) < 0
                ? text
                : text.Replace(PlayerToken, ResolveTrainerName());

        private void SetControl(bool enabled)
        {
            _controlHeld = !enabled;
            if (_cameraRig != null) _cameraRig.ControlEnabled = enabled;
            foreach (var reader in FindObjectsByType<OverworldInputReader>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                reader.InputEnabled = enabled;
            }
        }

        private static Transform FindMarker(string markerName)
        {
            if (string.IsNullOrEmpty(markerName)) return null;
            var go = GameObject.Find(markerName);
            return go != null ? go.transform : null;
        }

        // --- Shared plumbing -----------------------------------------------------------------

        /// <summary>
        /// Runs a routine with a ceiling on it, and abandons it if it overruns.
        ///
        /// Every wait in this file that hands off to another system goes through something like
        /// this. The one thing an episode must never do is stop halfway with the player's input
        /// switched off, so a dependency that never finishes is logged loudly, cut loose, and
        /// left behind.
        /// </summary>
        private IEnumerator RunBounded(IEnumerator routine, float timeoutSeconds, string what,
            Action onTimeout = null)
        {
            if (routine == null) yield break;

            var finished = false;
            IEnumerator Wrapped()
            {
                yield return routine;
                finished = true;
            }

            var handle = StartCoroutine(Wrapped());
            var elapsed = 0f;
            while (!finished && elapsed < timeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            if (finished) yield break;

            StopCoroutine(handle);
            Debug.LogError($"[Episode] {what} did not finish within {timeoutSeconds:0.0}s and was " +
                           "abandoned so the episode can continue.", this);
            onTimeout?.Invoke();
        }
    }
}
