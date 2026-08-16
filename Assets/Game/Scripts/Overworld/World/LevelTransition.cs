using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A doorway out of this level: walk into it and the next scene loads.
    ///
    /// Caves are their own scenes rather than hollows carved into the overworld's height
    /// field. That is not a shortcut — the height grid describes the *outside* of the
    /// massif, so anything placed at a cave's floor coordinates ends up on the summit
    /// above it, which is exactly where the grotto's grass and props were landing before
    /// they were moved out.
    ///
    /// The load is deliberately guarded rather than optimistic. A scene that is not in
    /// the build settings fails silently in a player and throws in the editor, and the
    /// failure surfaces as "the cave door does nothing", which is a long way from the
    /// cause. So it is checked up front and says so.
    ///
    /// A building's front door is the same component with one thing added: it remembers the
    /// doorstep it stands on, because the interior it leads to may be shared by seven houses
    /// and cannot know on its own which one to put the player back outside.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public sealed class LevelTransition : MonoBehaviour
    {
        [Tooltip("Scene to load. Must be in File > Build Settings.")]
        [SerializeField] private string _sceneName;

        [Tooltip("Where the player is put down in the destination scene. Resolved by name " +
                 "there, so the two scenes agree without holding a reference across a load.")]
        [SerializeField] private string _arrivalSpawn = "Spawn_FromOverworld";

        [Tooltip("Seconds of black before the load starts. A cut straight to a loading " +
                 "screen is the abrupt transition the brief rules out.")]
        [SerializeField] private float _fadeOutSeconds = 0.35f;

        [Tooltip("Slower coming back than going out. The player knows why the screen went " +
                 "dark; they are looking at somewhere new when it lifts.")]
        [SerializeField] private float _fadeInSeconds = 0.55f;

        [Tooltip("Tag the trigger reacts to. Only the player travels between levels.")]
        [SerializeField] private string _travellerTag = "Player";

        [Tooltip("Scene this door stands in, published for the door on the far side to come " +
                 "back to. Empty on a door that is not a way in.")]
        [SerializeField] private string _returnScene;

        [Tooltip("Marker beside this door, on this side of it. Where the far side sends the " +
                 "player back to.")]
        [SerializeField] private string _returnSpawn;

        [Tooltip("This door is the way back out. It prefers the doorstep the player came in " +
                 "through over its own destination fields.")]
        [SerializeField] private bool _returnsToDoor;

        private bool _travelling;

        /// <summary>Where the next scene should put the player down. Cleared by whoever
        /// consumes it on arrival.</summary>
        public static string PendingArrivalSpawn { get; set; }

        /// <summary>
        /// The doorstep the player last stepped in from, remembered across the load.
        ///
        /// Interiors are shared — seven front doors in the town all lead to Interior_House —
        /// so the way out cannot be a fixed destination baked into the interior: it is
        /// whichever door was walked through. That is only knowable on the outside, so the
        /// outside door records it here on its way through and the interior's own door reads
        /// it back. A static for the same reason <see cref="PendingArrivalSpawn"/> is one:
        /// the value has to outlive every component in the scene being torn down.
        /// </summary>
        public static string ReturnScene { get; set; }

        /// <summary>Marker in <see cref="ReturnScene"/> the way out aims at. See it.</summary>
        public static string ReturnSpawn { get; set; }

        /// <summary>Set by the level builder.</summary>
        public void Configure(string sceneName, string arrivalSpawn)
        {
            _sceneName = sceneName;
            if (!string.IsNullOrEmpty(arrivalSpawn)) _arrivalSpawn = arrivalSpawn;
        }

        /// <summary>A door into a building: remembers where it stood so the way out can find it.</summary>
        public void ConfigureDoor(string sceneName, string arrivalSpawn,
            string returnScene, string returnSpawn)
        {
            Configure(sceneName, arrivalSpawn);
            _returnScene = returnScene;
            _returnSpawn = returnSpawn;
        }

        /// <summary>
        /// The way out of an interior. The scene and marker given here are only the fallback,
        /// used when the interior was opened directly rather than walked into — which is how
        /// it is worked on in the editor, and a way out that does nothing in that case is a
        /// room with no exit for whoever is building it.
        /// </summary>
        public void ConfigureReturn(string fallbackScene, string fallbackSpawn)
        {
            Configure(fallbackScene, fallbackSpawn);
            _returnsToDoor = true;
        }

        /// <summary>
        /// Where this door actually leads this time.
        ///
        /// Resolved in one place because <see cref="OnTriggerEnter"/>'s "already here" guards
        /// and <see cref="Travel"/>'s load have to agree: a return door that checked its
        /// fallback scene and then loaded the remembered one would stand down for the wrong
        /// reasons and travel to the wrong place.
        /// </summary>
        private void Destination(out string scene, out string spawn)
        {
            scene = _sceneName;
            spawn = _arrivalSpawn;
            if (!_returnsToDoor) return;
            if (string.IsNullOrEmpty(ReturnScene) || string.IsNullOrEmpty(ReturnSpawn)) return;

            scene = ReturnScene;
            spawn = ReturnSpawn;
        }

        private void Reset()
        {
            var collider = GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
        }

        private void Awake()
        {
            var collider = GetComponent<Collider>();
            if (collider != null && !collider.isTrigger)
            {
                Debug.LogWarning($"[LevelTransition] Collider on '{name}' was not a trigger; " +
                                 "forced isTrigger. A solid one would be a wall across the door.", this);
                collider.isTrigger = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_travelling) return;
            if (!string.IsNullOrEmpty(_travellerTag) && !other.CompareTag(_travellerTag)) return;

            Destination(out var destination, out _);
            if (string.IsNullOrEmpty(destination))
            {
                Debug.LogWarning($"[LevelTransition] '{name}' has no destination scene set.", this);
                return;
            }

            // Already here. Three ways that happens: the destination is streamed in beside
            // us, it is loaded outright, or it *is* this scene — Overworld is built from the
            // town layout, so its To_Town link points at content the player is standing in.
            // Loading any of those tears down the world they are in to rebuild the one they
            // are already in, which is the teleport that was reported at the town gate.
            if (World.WorldStreamer.Claimed.Contains(destination)
                || World.WorldStreamer.Streamed.Contains(destination)
                || SceneManager.GetSceneByName(destination).isLoaded
                || string.Equals(destination, SceneManager.GetActiveScene().name,
                                 System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Never mid-conversation. Walking into a doorway while someone is talking to you
            // used to cut them off and load the next scene, so a story beat could be lost by
            // taking one step — and the beat that fires at the town gate is the one the whole
            // opening builds to.
            var dialogue = DialogueRunner.Instance;
            if (dialogue != null && dialogue.IsPlaying) return;

            StartCoroutine(Travel());
        }

        private IEnumerator Travel()
        {
            _travelling = true;

            Destination(out var destination, out var arrivalSpawn);

            // A static rather than a field on the profile: IPlayerProfile is part of the
            // frozen Core contract and this does not warrant widening it. The value has
            // to outlive the scene load, which rules out anything held by a component.
            PendingArrivalSpawn = arrivalSpawn;

            // Published before the load, consumed by the door on the far side. Cleared on the
            // way back out rather than left standing: a spent doorstep that survived would be
            // the destination of the next interior the player walked into, and they would
            // come out of a house they were never in.
            if (!string.IsNullOrEmpty(_returnSpawn))
            {
                ReturnScene = _returnScene;
                ReturnSpawn = _returnSpawn;
            }
            else if (_returnsToDoor)
            {
                ReturnScene = null;
                ReturnSpawn = null;
            }

            // Cover the screen before the world changes, rather than waiting out a fade
            // duration and then cutting anyway — which is what this did, and is a teleport
            // with a pause in front of it. ScreenTransitionOverlay has been able to do this
            // since it was written; nothing outside its own assembly could reach it until
            // IScreenCover existed.
            var covered = false;
            if (PokeLab.Core.ServiceHub.TryGet<PokeLab.Core.IScreenCover>(out var cover)
                && cover != null)
            {
                var done = false;
                cover.Cover(_fadeOutSeconds, () => done = true);

                var waited = 0f;
                while (!done && waited < _fadeOutSeconds * 4f + 1f)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                covered = true;
            }
            else if (_fadeOutSeconds > 0f)
            {
                Debug.LogWarning("[LevelTransition] Nothing can cover the screen, so this door " +
                                 "is a hard cut. Add a ScreenCoverHost to the scene.", this);
                yield return new WaitForSeconds(_fadeOutSeconds);
            }

            if (!Application.CanStreamedLevelBeLoaded(destination))
            {
                Debug.LogError(
                    $"[LevelTransition] Scene '{destination}' is not in the build settings, so " +
                    "walking into this door does nothing. Add it under File > Build Settings.", this);
                _travelling = false;
                yield break;
            }

            // Nothing is streamed on the far side of a Single load — the sets are static and
            // survive the teardown that empties them of meaning. Left standing, the town's
            // claim on Field followed the player into a building, and the door back out
            // stood down because it believed its destination was already beside it. That is
            // the "the door does nothing" failure, arrived at from the opposite direction.
            World.WorldStreamer.Claimed.Clear();
            World.WorldStreamer.Streamed.Clear();

            var load = SceneManager.LoadSceneAsync(destination, LoadSceneMode.Single);
            while (load != null && !load.isDone) yield return null;

            // Uncovered on the far side. The cover host is registered fresh by the scene that
            // just loaded, so this is resolved again rather than reusing the one from the
            // scene that has since been torn down.
            if (!covered) yield break;

            yield return null;   // one frame, so the arrival marker has repositioned the player
            if (PokeLab.Core.ServiceHub.TryGet<PokeLab.Core.IScreenCover>(out var arrival)
                && arrival != null)
            {
                arrival.Reveal(_fadeInSeconds, null);
            }
        }
    }
}
