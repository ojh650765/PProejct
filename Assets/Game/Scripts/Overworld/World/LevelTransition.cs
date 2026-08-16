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

        [Tooltip("Tag the trigger reacts to. Only the player travels between levels.")]
        [SerializeField] private string _travellerTag = "Player";

        private bool _travelling;

        /// <summary>Where the next scene should put the player down. Cleared by whoever
        /// consumes it on arrival.</summary>
        public static string PendingArrivalSpawn { get; set; }

        /// <summary>Set by the level builder.</summary>
        public void Configure(string sceneName, string arrivalSpawn)
        {
            _sceneName = sceneName;
            if (!string.IsNullOrEmpty(arrivalSpawn)) _arrivalSpawn = arrivalSpawn;
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
            if (string.IsNullOrEmpty(_sceneName))
            {
                Debug.LogWarning($"[LevelTransition] '{name}' has no destination scene set.", this);
                return;
            }

            // Already here. Three ways that happens: the destination is streamed in beside
            // us, it is loaded outright, or it *is* this scene — Overworld is built from the
            // town layout, so its To_Town link points at content the player is standing in.
            // Loading any of those tears down the world they are in to rebuild the one they
            // are already in, which is the teleport that was reported at the town gate.
            if (World.WorldStreamer.Streamed.Contains(_sceneName)
                || SceneManager.GetSceneByName(_sceneName).isLoaded
                || string.Equals(_sceneName, SceneManager.GetActiveScene().name,
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

            // A static rather than a field on the profile: IPlayerProfile is part of the
            // frozen Core contract and this does not warrant widening it. The value has
            // to outlive the scene load, which rules out anything held by a component.
            PendingArrivalSpawn = _arrivalSpawn;

            if (_fadeOutSeconds > 0f) yield return new WaitForSeconds(_fadeOutSeconds);

            if (!Application.CanStreamedLevelBeLoaded(_sceneName))
            {
                Debug.LogError(
                    $"[LevelTransition] Scene '{_sceneName}' is not in the build settings, so " +
                    "walking into this door does nothing. Add it under File > Build Settings.", this);
                _travelling = false;
                yield break;
            }

            var load = SceneManager.LoadSceneAsync(_sceneName, LoadSceneMode.Single);
            while (load != null && !load.isDone) yield return null;
        }
    }
}
