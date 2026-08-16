using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PokeLab.Core;
using UnityEngine;

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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EpisodeRunner : MonoBehaviour
    {
        [Tooltip("Authored episodes, relative to the project root. Read at Awake so the " +
                 "opening can be rewritten without touching code or the scene.")]
        [SerializeField] private string _bookPath = "Assets/Game/Data/Story/episodes.json";

        [Tooltip("Episode to play when a new game starts and its completion flag is unset.")]
        [SerializeField] private string _openingEpisodeId = "opening";

        [Tooltip("Run the opening automatically on a fresh profile.")]
        [SerializeField] private bool _autoPlayOpening = true;

        [SerializeField] private StarterSelection _starterSelection;
        [SerializeField] private OverworldCameraRig _cameraRig;

        private readonly Dictionary<string, Episode> _episodes = new Dictionary<string, Episode>();
        private readonly HashSet<string> _flags = new HashSet<string>();
        private Coroutine _running;

        public bool IsPlaying => _running != null;
        public event Action<string> EpisodeFinished;

        private void Awake() => LoadBook();

        private void Start()
        {
            if (!_autoPlayOpening) return;
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile == null) return;
            // A profile that already has a party has played the opening, whatever the
            // flags say. That check is cheaper than a save migration and cannot be wrong.
            if (profile.Party != null && profile.Party.Count > 0) return;
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
            if (!string.IsNullOrEmpty(episode.CompletionFlag) && _flags.Contains(episode.CompletionFlag))
                return false;

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
                if (!string.IsNullOrEmpty(episode.CompletionFlag)) _flags.Add(episode.CompletionFlag);
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
                    if (beat.Value) _flags.Add(beat.Id);
                    else _flags.Remove(beat.Id);
                    break;

                case EpisodeBeatKind.GiveItem:
                    if (ServiceHub.TryGet<IPlayerProfile>(out var bag) && bag != null)
                        bag.AddItem(beat.Id, Mathf.Max(1, beat.Amount));
                    break;

                case EpisodeBeatKind.CameraTo:
                    var marker = FindMarker(beat.Id);
                    if (marker != null && _cameraRig != null) _cameraRig.LookToward(marker.position);
                    break;

                case EpisodeBeatKind.ChooseStarter:
                    yield return RunStarterChoice();
                    break;

                case EpisodeBeatKind.FadeOut:
                case EpisodeBeatKind.FadeIn:
                case EpisodeBeatKind.Dialogue:
                case EpisodeBeatKind.MoveActor:
                case EpisodeBeatKind.Battle:
                    // Each of these needs a system this scene does not own yet: the
                    // transition director for the fades, DialogueRunner for lines, a
                    // navmesh walk for actors, and IGameFlow for the battle handover.
                    // They are listed rather than silently skipped so an unfinished
                    // opening is obvious in the log instead of playing as a shorter one.
                    Debug.LogWarning($"[Episode] Beat {beat.Kind} ('{beat.Id}') is not wired " +
                                     "up yet and was skipped.", this);
                    break;
            }
        }

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

            // Presentation subscribes to StarterSelection and calls Choose; the runner
            // only waits. That keeps the moment independent of how it is drawn.
            while (!done) yield return null;

            _starterSelection.Chosen -= OnChosen;
            _starterSelection.Commit(ResolveTrainerName());
        }

        private static string ResolveTrainerName() =>
            ServiceHub.TryGet<IPlayerProfile>(out var p) && !string.IsNullOrEmpty(p?.TrainerName)
                ? p.TrainerName
                : "Player";

        private void SetControl(bool enabled)
        {
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
    }
}
