using PokeLab.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A way out that always works, held down by the player rather than detected.
    ///
    /// <see cref="PlayerLocomotion"/> already recovers from two traps: a fall out of the world,
    /// and a pit the player is standing in and cannot climb. Both anchor on the last place the
    /// player moved freely, and that is exactly what a ravine defeats — the floor of a gully is
    /// somewhere you can walk, at length, in both directions, so the anchor follows you down
    /// and the depth test compares the bottom against the bottom. You can pace a trap forever
    /// without ever satisfying a test for being trapped.
    ///
    /// Rather than keep widening a detector against an unbounded set of shapes a generated
    /// world can produce, this hands the player the answer. Held, not tapped, and with the
    /// progress shown, because an instant teleport on a key press is something you trigger by
    /// accident and then cannot undo.
    ///
    /// The anchor is a spawn marker, not the last safe position: the whole premise is that the
    /// player's recent history is inside the trap, so anything derived from it is suspect.
    /// Markers are authored, are on the walkable network by construction, and exist in every
    /// scene the player can be standing in.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerLocomotion))]
    public sealed class StuckEscape : MonoBehaviour
    {
        [Tooltip("Seconds the key is held before the player is moved. Long enough that it " +
                 "cannot be pressed by accident, short enough to try when in doubt.")]
        [SerializeField] private float _holdSeconds = 1.2f;

        [Tooltip("Names searched for an anchor, best first.")]
        [SerializeField]
        private string[] _anchorNames = { "PlayerSpawn", "Spawn_FromField", "Spawn_FromTown" };

        private PlayerLocomotion _player;
        private OverworldInputReader _input;
        private float _held;

        private void Awake()
        {
            _player = GetComponent<PlayerLocomotion>();
            _input = FindFirstObjectByType<OverworldInputReader>();
        }

        private void Update()
        {
            // Only while the player actually has control. During a cutscene the character is
            // being moved by a script, and a rescue in the middle of that would strand the
            // scene rather than the player.
            if (_input == null || !_input.InputEnabled) { _held = 0f; return; }

            var keyboard = Keyboard.current;
            var pad = Gamepad.current;
            var down = (keyboard != null && keyboard.rKey.isPressed)
                       || (pad != null && pad.selectButton.isPressed);

            if (!down) { _held = 0f; return; }

            _held += Time.deltaTime;
            if (_held < _holdSeconds) return;

            _held = 0f;
            Escape();
        }

        private void Escape()
        {
            var anchor = FindAnchor();
            if (anchor == null)
            {
                Debug.LogWarning("[Player] Asked to be rescued and there is no spawn marker in " +
                                 "any loaded scene to move them to. The level was built without " +
                                 "one, which also means a fresh game has nowhere to start.", this);
                return;
            }

            Debug.Log($"[Player] Rescued from {transform.position} to '{anchor.name}' at " +
                      $"{anchor.position}. If this happens twice in the same place, the level " +
                      "has a trap there rather than the player having wandered into one.");

            _player.Warp(anchor.position, anchor.rotation);
        }

        /// <summary>
        /// The nearest authored spawn marker, preferring the named ones.
        ///
        /// Nearest rather than first, because both bands are loaded at once: the town's spawn
        /// and the field's both exist while the player is standing in either, and rescuing
        /// someone at the lake back to the town plaza would undo an act of walking.
        /// </summary>
        private Transform FindAnchor()
        {
            Transform best = null;
            var bestDistance = float.MaxValue;

            foreach (var name in _anchorNames)
            {
                foreach (var found in GameObject.FindObjectsByType<Transform>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (found.name != name) continue;

                    var distance = (found.position - transform.position).sqrMagnitude;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = found;
                }

                if (best != null) return best;
            }

            return best;
        }
    }
}
