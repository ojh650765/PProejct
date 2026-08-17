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
        /// <summary>
        /// Put a wild creature on a mark and leave it there, visible, doing nothing.
        /// `id` is the marker, `target` is the game species id, `amount` is the level.
        /// </summary>
        StageCreature = 14,
        /// <summary>
        /// Walk the staged creature up to the player and block until it arrives.
        /// `seconds` is a timeout, as with <see cref="MoveActor"/>.
        /// </summary>
        CreatureApproach = 15,
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

        /// <summary>
        /// Played the instant this one ends. For scenes that are really one scene, split so
        /// each half can be judged on its own.
        ///
        /// The field is what makes the split safe. episodes.json has carried
        /// <c>"NextEpisodeId": "rival_first_battle"</c> on <c>opening_departure</c> since the
        /// act was written, and this class did not declare it — JsonUtility drops members it
        /// has no field for without a word, so the chain was authored, documented in the
        /// schema, and silently discarded at load. The rival battle had no other way to start.
        /// </summary>
        public string NextEpisodeId;

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
        [SerializeField] private string _bookPath = "Assets/Game/Data/Story/Resources/episodes.json";

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

            // A debug jump asked to start somewhere else in the game. Checked before the
            // coroutine so the opening never begins at all, rather than beginning and being
            // interrupted — an episode stopped between taking control and giving it back
            // leaves the player frozen.
            if (DebugFlow.SuppressOpening) return;

            StartCoroutine(AutoPlayOpening());
        }

        /// <summary>
        /// Plays the opening once the profile exists.
        ///
        /// This used to be a plain Start that gave up the moment ServiceHub had no profile in
        /// it — and whether it did came down to which component's Start ran first. The opening
        /// therefore played on some runs and not others, from the same save state, with
        /// nothing logged either way: the player simply began a new game standing in the plaza
        /// with control and no professor. Waiting a few frames for the host to register makes
        /// the outcome depend on the save rather than on script execution order.
        /// </summary>
        private System.Collections.IEnumerator AutoPlayOpening()
        {
            IPlayerProfile profile = null;
            var deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (ServiceHub.TryGet<IPlayerProfile>(out profile) && profile != null) break;
                yield return null;
            }

            if (profile == null)
            {
                Debug.LogWarning("[Episode] No player profile was ever registered, so the opening " +
                                 "cannot decide whether it is due. A new game will start with the " +
                                 "player loose in the plaza.", this);
                yield break;
            }

            // The completion flag, read through the profile, is the only test that survives a
            // save. It replaces a check on the party being empty, which cannot work in this
            // scene: PlayerProfileHost.NewGame hands every fresh profile a debug Charmander
            // before this component's Start runs, so "has a party" was true on the very first
            // frame of a new game and the opening never played once.
            if (_episodes.TryGetValue(_openingEpisodeId, out var opening)
                && IsFlagSet(opening.CompletionFlag)) yield break;

            // A session restored from a save has had its opening whatever the flags say — that
            // covers saves written before the flag was persisted, without a save migration.
            if (SessionBeganFromSave() && profile.Party != null && profile.Party.Count > 0) yield break;

            if (!Play(_openingEpisodeId))
                Debug.LogWarning($"[Episode] '{_openingEpisodeId}' would not start. The book holds " +
                                 $"{_episodes.Count} episode(s); check _bookPath on this component.", this);
        }

        /// <summary>Name the book is looked up under once it is inside a Resources folder.</summary>
        private const string BookResourceName = "episodes";

        private void LoadBook()
        {
            _episodes.Clear();

            // Resources first, disk second.
            //
            // This read the book off disk and nothing else, which works in the editor and
            // cannot work in a player: a build has no project folder, so File.Exists is false,
            // the warning fires into a console nobody is looking at, and a new game begins
            // with the player standing in the plaza with no opening and nothing to do. Every
            // other book in the game — dialogue, trainers, the string table — already reads
            // this way, and the note in Loc.cs records fixing exactly this. This one was
            // missed, and only a build would ever have shown it.
            //
            // The disk path is kept below because the editor edits that file directly and
            // reloading it without a reimport is what makes a story rewrite quick.
            var json = Resources.Load<TextAsset>(BookResourceName)?.text;

            if (string.IsNullOrEmpty(json))
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), _bookPath);
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[Episode] No episode book in Resources as " +
                                     $"'{BookResourceName}' and none at {_bookPath}. The opening " +
                                     "will not play, and a new game will start with the player " +
                                     "standing in the plaza with no party and nothing to do.", this);
                    return;
                }
                json = File.ReadAllText(path);
            }

            var book = JsonUtility.FromJson<EpisodeBook>(json);
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

        /// <summary>
        /// Set by a beat that could not happen and could happen next time.
        ///
        /// Only for the transient kind. A sequence id that is not in the book, or a scene with
        /// no DialogueRunner in it, will fail identically on every retry — holding the
        /// completion flag open for those turns one lost beat into an episode that re-triggers
        /// forever, which is a worse failure than the one it is guarding against.
        /// </summary>
        private bool _beatLost;

        private IEnumerator Run(Episode episode)
        {
            var tookControl = false;
            var reachedTheEnd = false;
            _beatLost = false;

            try
            {
                foreach (var beat in episode.Beats)
                {
                    if (beat.Kind == EpisodeBeatKind.TakeControl) tookControl = true;
                    if (beat.Kind == EpisodeBeatKind.GiveControl) tookControl = false;

                    var step = Perform(beat);
                    while (step.MoveNext()) yield return step.Current;
                }
                reachedTheEnd = true;
            }
            finally
            {
                // Control is returned even if a beat threw or the episode was cut short.
                // The alternative is a player who cannot move and no message saying why,
                // which is the worst failure this system can have.
                if (tookControl) SetControl(true);
                ClearStagedCreature();

                // Marked done only if it was actually done, the way StoryEncounter's _spoke
                // guard does it.
                //
                // This finally runs when a beat throws and when the coroutine is disposed —
                // which is what a scene load does to the host mid-episode — and it used to
                // set the flag in both cases. On the opening that is a bricked save and not a
                // missed scene: cut off before ChooseStarter, the flag persists through the
                // next autosave, so the opening never replays, the player has no starter,
                // story.pokedex is never granted, and StoryPhase then holds wild encounters,
                // roamers and every trainer down for the rest of that file. Nothing on screen
                // says why, because from the game's point of view the act happened.
                if (reachedTheEnd && !_beatLost && !string.IsNullOrEmpty(episode.CompletionFlag))
                    SetFlag(episode.CompletionFlag, true);
                else if (!string.IsNullOrEmpty(episode.CompletionFlag))
                    Debug.LogWarning($"[Episode] '{episode.Id}' did not finish, so its completion " +
                                     $"flag '{episode.CompletionFlag}' has been left unset and the " +
                                     "scene can be offered again.", this);

                _running = null;
                _playingId = null;
                EpisodeFinished?.Invoke(episode.Id);

                // Chained here, inside the finally, and not after it: this runs in the same
                // frame the previous episode released, so IsPlaying never reads false in
                // between. One such frame is enough for the StoryEncounter waiting on it to
                // decide the scene is over and hand control back to the player halfway
                // through what the script treats as one continuous act.
                if (!string.IsNullOrEmpty(episode.NextEpisodeId)
                    && !Play(episode.NextEpisodeId)
                    && !AlreadyPlayed(episode.NextEpisodeId))
                {
                    Debug.LogWarning($"[Episode] '{episode.Id}' names '{episode.NextEpisodeId}' as " +
                                     "what follows it and it would not start, so the act ends here " +
                                     "with the player free and nothing left to walk towards.", this);
                }
            }
        }

        /// <summary>
        /// Whether an episode's completion flag is already set — the benign reason
        /// <see cref="Play"/> refuses, and the one that must not be warned about.
        /// </summary>
        private bool AlreadyPlayed(string episodeId) =>
            _episodes.TryGetValue(episodeId, out var episode) && IsFlagSet(episode.CompletionFlag);

        /// <summary>The episode currently playing, or null. Read by the capture stamp.</summary>
        public string PlayingEpisodeId => _playingId;

        /// <summary>
        /// The beat being performed right now, as "Kind:id".
        ///
        /// Exists so a screenshot can say which beat it is a screenshot of. Reading a capture
        /// and having to guess whether the episode stalled, skipped a beat or never started is
        /// how several rounds of this have been wasted.
        /// </summary>
        public string CurrentBeat { get; private set; }

        private IEnumerator Perform(EpisodeBeat beat)
        {
            CurrentBeat = beat.Kind + (string.IsNullOrEmpty(beat.Id) ? "" : ":" + beat.Id);

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
                    yield return RunAskName(beat);
                    break;

                case EpisodeBeatKind.Battle:
                    yield return RunBattle(beat);
                    break;

                case EpisodeBeatKind.StageCreature:
                    yield return RunStageCreature(beat);
                    break;

                case EpisodeBeatKind.CreatureApproach:
                    yield return RunCreatureApproach(beat);
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
                // Play refuses for two reasons and only one of them is worth waiting on. A
                // sequence with no lines in it will be just as empty in three minutes' time.
                if (!runner.IsPlaying)
                {
                    Debug.LogWarning($"[Episode] '{sequenceId}' resolved to a sequence with no lines, " +
                                     "so the beat had nothing to say and was skipped.", this);
                    Destroy(sequence);
                    yield break;
                }

                // Queued, not dropped.
                //
                // The refusal is correct — two conversations at once is the race the runner
                // exists to prevent — but skipping is the wrong answer to it. A beat is the
                // only copy of a scene: 'gate_wait_for_kes' is the one place the player is
                // told where to go next, and losing it because an NPC happened to be talking
                // on the frame the trigger fired is a hole in the act with no way back into
                // it. Waiting keeps the invariant (still one conversation at a time) and
                // costs a second.
                yield return WaitForRunnerFree(runner, sequenceId);

                // The conversation we waited on restores the control state it captured when
                // it began, which was before this episode took it away. Put the cutscene
                // freeze back before the next line goes up, or the player has their legs for
                // the length of a beat that is holding the camera on somebody else.
                if (_controlHeld) SetControl(false);

                if (!runner.Play(sequence, gameObject))
                {
                    Debug.LogError($"[Episode] '{sequenceId}' still could not start after waiting " +
                                   $"{_dialogueBeatTimeoutSeconds:0}s for the DialogueRunner. The " +
                                   "episode's completion flag is being left unset so the beat is " +
                                   "offered again rather than lost.", this);
                    _beatLost = true;
                    Destroy(sequence);
                    yield break;
                }
            }

            yield return WaitForDialogue(runner, sequenceId);
            Destroy(sequence);
        }

        /// <summary>
        /// Holds until the runner has nothing on screen, or the ceiling runs out.
        ///
        /// Unscaled, because the conversation being waited on may itself be running behind a
        /// transition that has held time at zero. The whole conversation ahead of this one is
        /// allowed to finish at the player's own pace — it is somebody else's scene and
        /// cutting it off to get at ours would be the same bug pointed the other way.
        /// </summary>
        private IEnumerator WaitForRunnerFree(DialogueRunner runner, string sequenceId)
        {
            var waited = 0f;
            var warned = false;

            while (runner.IsPlaying && waited < _dialogueBeatTimeoutSeconds)
            {
                if (!warned && waited > 1f)
                {
                    warned = true;
                    Debug.Log($"[Episode] '{sequenceId}' is waiting for the conversation already on " +
                              $"screen ('{runner.CurrentSequenceId}') to finish.", this);
                }
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
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
        /// Asks the player their name and writes it to the profile.
        ///
        /// Bounded, and it falls back to the beat's authored default on a timeout or an empty
        /// confirmation. A modal prompt nothing can dismiss is a game over on the first
        /// screen, which is what this method used to guard against by never opening one at
        /// all — the note it carried is now satisfied rather than deferred.
        /// </summary>
        /// <summary>
        /// Starts the opening theme as the question is asked.
        ///
        /// Reached by reflection for the same reason the screen wipe is: MusicDirector lives
        /// in the audio assembly, which PokeLab.Overworld does not reference, and adding that
        /// reference to play one cue would tie every scripted scene in the game to the audio
        /// layer. Silence is an acceptable outcome here — the scene still works without it —
        /// so a missing director is not worth a hard dependency or an error.
        /// </summary>
        private void PlayOpeningTheme()
        {
            var director = FindAnyObjectByType(
                System.Type.GetType("PokeLab.Audio.MusicDirector, PokeLab.Audio"), FindObjectsInactive.Exclude);
            if (director == null) return;

            var play = director.GetType().GetMethod("PlayTrack",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(float) }, null);
            play?.Invoke(director, new object[] { OpeningTrack, 1.2f });
        }

        /// <summary>The cue that plays from the name prompt onward.</summary>
        private const string OpeningTrack = "Music_Opening_Introduction";

        private IEnumerator RunAskName(EpisodeBeat beat)
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
                yield break;
            }

            DefaultName = chosen;
            SubmittedName = null;
            IsAwaitingName = true;

            PlayOpeningTheme();

            var elapsed = 0f;
            while (SubmittedName == null && elapsed < _nameEntryTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            IsAwaitingName = false;

            if (SubmittedName == null)
            {
                Debug.LogWarning($"[Episode] Nothing answered the name prompt within " +
                                 $"{_nameEntryTimeoutSeconds:F0}s, so '{chosen}' was used. Check " +
                                 "that a name entry presenter is in the scene.", this);
            }

            var final = string.IsNullOrWhiteSpace(SubmittedName) ? chosen : SubmittedName.Trim();
            profile.SetTrainerName(final);
        }

        [Tooltip("Ceiling on the name prompt. Long, because the player is typing — but not " +
                 "unbounded, because a missing presenter must not end the game on its first " +
                 "screen.")]
        [SerializeField] private float _nameEntryTimeoutSeconds = 180f;

        /// <summary>True while the runner is waiting for a name. The presenter's cue to open.</summary>
        public bool IsAwaitingName { get; private set; }

        /// <summary>What to prefill the field with, and what is used if the player confirms it empty.</summary>
        public string DefaultName { get; private set; } = "Ren";

        private string SubmittedName { get; set; }

        /// <summary>Called by the name entry UI. An empty string means "keep the default".</summary>
        public void SubmitName(string name) => SubmittedName = name ?? string.Empty;

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
            // The marker's own height, not the walker's.
            //
            // This used to keep the actor's starting Y for the whole walk, so anyone crossing
            // a slope arrived at the height they set off from — Kes crossing the bank sank
            // into it on the way down and floated over it on the way up. The marker is seated
            // on the ground by the level builder, so its height is the right one to arrive at.
            var destination = marker.position;

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
                    // Re-seated each step, because a straight line between two points that
                    // are both on the ground still passes under a rise and over a dip.
                    var next = Vector3.MoveTowards(body.position, destination,
                        _actorWalkSpeed * Time.deltaTime);
                    var facing = Flatten(destination - body.position);
                    PlaceActor(body, locomotion, next,
                        facing.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(facing) : body.rotation);

                    if (Physics.Raycast(body.position + Vector3.up * 3f, Vector3.down,
                                        out var underfoot, 30f, ~0, QueryTriggerInteraction.Ignore))
                        body.position = new Vector3(body.position.x, underfoot.point.y, body.position.z);

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
            if (locomotion != null) { locomotion.Warp(position, rotation); return; }

            // An agent has to be Warped, not moved.
            //
            // A live NavMeshAgent keeps its own nextPosition and writes it back over the
            // transform on the frame after any direct assignment — so setting the position of
            // an NPC that has one looks like it worked and is silently undone. That is what
            // makes a MoveActor across the town/field seam fail: the two bands bake separate
            // navmeshes with nothing linking them, the agent paths only as far as its own mesh
            // ends, and the snap that is supposed to finish the walk is reverted. Kes stopped
            // at the town gate and the battle that waits for him at the lake fired there.
            var agent = body.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.Warp(position);
                body.rotation = rotation;
                return;
            }

            body.SetPositionAndRotation(position, rotation);
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
            // A wild fight against the creature a StageCreature beat put on the ground, rather
            // than a trainer named by id. Split off here so the two never share a request: this
            // one has no trainer and must carry the staged creature's species, and the trainer
            // path has no creature to read one from.
            if (beat.Target == "staged")
            {
                yield return RunStagedBattle();
                yield break;
            }

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

        // --- Staged creatures ------------------------------------------------------------------

        /// <summary>
        /// The creature the running episode put on the ground, if any. At most one: a scene that
        /// needs two staged creatures is a scene, not a beat, and nothing authored wants it.
        /// </summary>
        private RoamingCreature _staged;

        /// <summary>Name the staged actor is given, so CameraTo can frame it by name.</summary>
        private const string StagedCreatureName = "Actor_StagedCreature";

        /// <summary>
        /// Stands a wild creature on a mark, visible, and leaves it there.
        ///
        /// This is a <see cref="RoamingCreature"/> and not something new, and that is the whole
        /// design decision in the ambush. What the ambush needs is a creature the player can see
        /// standing in the grass before anything happens, that walks toward them, and that the
        /// battle is then fought against — its species, its level, and gone from the world
        /// afterwards. RoamingCreature already is all of that: it resolves the Gen 5 sprite
        /// through <c>IOverworldCreatureArtFactory</c>, walks on the same NavMesh everyone else
        /// does, and <see cref="EncounterDirector.ApplyResult"/> already knows to consume or
        /// retreat it when the fight ends. Writing a bespoke ambusher would mean a second piece
        /// of art resolution, a second walk, and a battle whose result nothing cleans up.
        ///
        /// <see cref="RoamingCreatureSpawner"/> is deliberately <em>not</em> used. Its job is the
        /// opposite of this one: it keeps an ambient population alive in a ring around the player
        /// and refuses to place anything closer than twenty-two metres, precisely so nothing ever
        /// appears in view. The ambush has to appear in view, on an authored mark, at a species
        /// and level the script chose. Asking the spawner for that would mean disabling every
        /// rule it exists to enforce. It is also switched off entirely for the length of the
        /// prologue — see <see cref="StoryPhase"/> — so there is nothing to ask.
        ///
        /// The creature is held (<see cref="RoamingCreature.ScriptedHold"/>) from the moment it
        /// lands, so it cannot wander off its mark between being placed and being needed.
        /// </summary>
        private IEnumerator RunStageCreature(EpisodeBeat beat)
        {
            ClearStagedCreature();

            if (!int.TryParse(beat.Target, out var speciesId) || speciesId <= 0)
            {
                Debug.LogWarning($"[Episode] StageCreature: '{beat.Target}' is not a game species " +
                                 "id. Nothing was placed, so the scene plays out with an empty " +
                                 "patch of grass in it.", this);
                yield break;
            }

            var stand = ResolveStagePosition(beat.Id);
            var host = new GameObject(StagedCreatureName);

            var creatureLayer = LayerMask.NameToLayer(OverworldNames.LayerCreature);
            if (creatureLayer >= 0) host.layer = creatureLayer;

            // Same agent settings the spawner builds its roamers with. Copied rather than shared
            // because the method that builds them is private to the spawner and this scene must
            // not depend on a spawner existing — the prologue switches it off.
            // Placed on the navmesh before the agent exists.
            //
            // A NavMeshAgent validates its position the instant it is added, and complains —
            // "Failed to create agent because it is not close enough to the NavMesh" — before
            // any line after AddComponent can move it. An agent that fails to create is not
            // merely misplaced; every call on it throws for the rest of its life. So the
            // position is settled first, and where there is no navmesh within reach the agent
            // is not added at all: a creature that walks nowhere is better than one that
            // throws on every step.
            if (!UnityEngine.AI.NavMesh.SamplePosition(host.transform.position, out var onMesh, 6f,
                                                       UnityEngine.AI.NavMesh.AllAreas))
            {
                Debug.LogWarning($"[Episode] No navmesh within 6 m of {host.transform.position}, so no agent " +
                                 "was created, so the ambush has nothing to stage. The beat " +
                                 "ends here rather than holding the player's control for a " +
                                 "creature that will never arrive.");
                yield break;
            }
            host.transform.position = onMesh.position;

            var agent = host.AddComponent<NavMeshAgent>();
            agent.autoBraking = true;
            agent.angularSpeed = 360f;
            agent.acceleration = 12f;
            agent.stoppingDistance = 0.35f;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

            _staged = host.AddComponent<RoamingCreature>();
            // Placid: the temperament is inert while the hold is on, and it is the one that does
            // nothing if the hold is ever lifted by accident. An Aggressive ambusher that got
            // loose would charge the player during a line and start the battle early.
            _staged.Configure(speciesId, Mathf.Max(1, beat.Amount), Temperament.Placid, stand);
            _staged.ScriptedHold = true;

            // Turned to face the player from the start. A creature standing side-on in the grass
            // reads as scenery; one looking at you reads as the thing the next line is about.
            var player = FindActor("Player");
            if (player != null)
            {
                var facing = Flatten(player.transform.position - host.transform.position);
                if (facing.sqrMagnitude > 1e-4f) host.transform.rotation = Quaternion.LookRotation(facing);
            }

            yield break;
        }

        /// <summary>
        /// Where the staged creature stands. The authored marker, or a point in front of the
        /// player when there is none.
        ///
        /// It falls back rather than skipping, which is the opposite of what
        /// <see cref="RunMoveActor"/> does with a missing marker, and the difference is what is
        /// lost. A walk that does not happen costs a piece of staging; an ambusher that is never
        /// placed costs the battle, the starter choice that depends on it and every episode
        /// chained behind it. Six metres in front of the player is not the shot the scene wants,
        /// but it is a scene.
        /// </summary>
        private Vector3 ResolveStagePosition(string markerName)
        {
            var marker = FindMarker(markerName);
            if (marker != null) return marker.position;

            var player = FindActor("Player");
            var origin = player != null ? player.transform : transform;
            var guess = origin.position + Flatten(origin.forward).normalized * 6f;

            Debug.LogWarning($"[Episode] StageCreature: no marker '{markerName}' in the scene, so " +
                             "the creature was put six metres in front of the player instead. It " +
                             "will not be standing in the grass the dialogue says it is standing " +
                             "in; the marker's world position belongs in cast.json.", this);

            return NavMesh.SamplePosition(guess, out var hit, 8f, NavMesh.AllAreas) ? hit.position : guess;
        }

        /// <summary>
        /// Walks the staged creature at the player and blocks until it is close enough to be
        /// threatening.
        ///
        /// Driven from here rather than by letting the creature's own Aggressive reaction do it,
        /// because that reaction is a negotiation — alertness builds, it commits, it may give up
        /// and retreat if the player is beyond its notice radius — and this beat has to finish.
        /// The scene is holding the player's control while it runs. So it is a plain walk with a
        /// ceiling on it, like every other wait in this file, and the creature is snapped onto
        /// its final distance if the walk does not land: a scripted approach that never arrives
        /// would hold the player frozen watching a Rattata stuck on a rock.
        /// </summary>
        private IEnumerator RunCreatureApproach(EpisodeBeat beat)
        {
            if (_staged == null)
            {
                Debug.LogWarning("[Episode] CreatureApproach with nothing staged. The beat before " +
                                 "this one was meant to place it; the scene continues without an " +
                                 "approach.", this);
                yield break;
            }

            var player = FindActor("Player");
            if (player == null) yield break;

            var body = _staged.transform;
            var agent = _staged.GetComponent<NavMeshAgent>();
            var timeout = beat.Seconds > 0.01f ? beat.Seconds : _defaultMoveTimeoutSeconds;
            var elapsed = 0f;
            var arrived = false;

            while (elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                var target = player.transform.position;
                var flat = Flatten(target - body.position);

                // Stopped short of the player's own capsule. Walking into it leaves the two
                // interpenetrating for the whole of the choice that follows.
                if (flat.magnitude <= ApproachStopDistance)
                {
                    arrived = true;
                    break;
                }

                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    // The agent turns itself, so nothing here touches the rotation — writing it
                    // as well leaves the two fighting over the same transform every frame.
                    agent.isStopped = false;
                    agent.SetDestination(target);
                }
                else
                {
                    // No navmesh under it. Walked straight at the player rather than skipped:
                    // the beat after this is a battle, and a creature that never moved is a
                    // battle that begins with the ambusher still out in the grass.
                    body.position = Vector3.MoveTowards(body.position, body.position + flat,
                        _actorWalkSpeed * Time.deltaTime);
                    body.rotation = Quaternion.LookRotation(flat.normalized);
                }

                yield return null;
            }

            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.ResetPath();
            }

            if (arrived) yield break;

            Debug.LogWarning($"[Episode] The staged creature did not reach the player within " +
                             $"{timeout:0.0}s and was placed in front of them. The next beat is a " +
                             "battle against it and needs it on screen.", this);

            var toPlayer = Flatten(player.transform.position - body.position);
            if (toPlayer.sqrMagnitude < 1e-4f) yield break;

            var mark = player.transform.position - toPlayer.normalized * ApproachStopDistance;
            body.rotation = Quaternion.LookRotation(toPlayer.normalized);

            // Warped rather than assigned. A NavMeshAgent keeps its own copy of where it is and
            // writing the transform underneath it snaps back on the next frame it moves — the
            // same reason PlaceActor puts the player through PlayerLocomotion.Warp.
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(mark);
            else body.position = mark;
        }

        /// <summary>Metres the ambusher stops at. Just outside the player's capsule.</summary>
        private const float ApproachStopDistance = 1.6f;

        /// <summary>
        /// Fights the staged creature, and blocks until the result has been applied.
        ///
        /// Routed through <see cref="EncounterDirector"/> rather than straight at
        /// <see cref="IGameFlow"/> like <see cref="RunBattle"/> does, because the director is what
        /// makes the fight be against <em>this</em> creature: it builds the request from the
        /// creature's own species and level, plays its telegraph, and removes it from the world
        /// afterwards. Waiting on <c>SequenceRunning</c> rather than on a callback because that is
        /// the only completion signal the director publishes, and it does not go false until the
        /// result has been written to the profile.
        /// </summary>
        private IEnumerator RunStagedBattle()
        {
            var director = EncounterDirector.Instance;
            if (director == null || _staged == null)
            {
                Debug.LogWarning("[Episode] The staged battle has no creature or no " +
                                 "EncounterDirector to run it. The beat is skipped and the scene " +
                                 "continues to the lines after the fight, which will read as " +
                                 "though the player won.", this);
                yield break;
            }

            if (!director.TriggerStagedEncounter(_staged))
            {
                Debug.LogWarning("[Episode] The EncounterDirector refused the staged battle — " +
                                 "something else is already running one. Skipped rather than " +
                                 "waited on, because nothing is going to finish.", this);
                yield break;
            }

            var elapsed = 0f;
            while (director.SequenceRunning && elapsed < _battleTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (director.SequenceRunning)
                Debug.LogError($"[Episode] The staged battle never resolved after " +
                               $"{_battleTimeoutSeconds:0}s. The episode is continuing without it " +
                               "rather than holding the player in a cutscene forever.", this);

            // The director unfreezes the player on its way out, which is right for a wild
            // encounter met while walking and wrong in the middle of a scripted scene.
            if (_controlHeld) SetControl(false);
        }

        /// <summary>
        /// Removes the staged creature, on every path out of the episode.
        ///
        /// Unconditional. Beaten or caught it is already inactive and this only tidies up the
        /// object; fled, or left behind by a battle that never resolved, it is an aggressive wild
        /// creature standing on the lake bank that no spawner owns, will never despawn, and that
        /// the player meets again on their way back to town.
        /// </summary>
        private void ClearStagedCreature()
        {
            if (_staged == null) return;
            Destroy(_staged.gameObject);
            _staged = null;
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
