using UnityEngine;
using UnityEngine.InputSystem;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A way out that always works, held down by the player rather than detected.
    ///
    /// <see cref="PlayerLocomotion"/> recovers from three traps on its own: a fall out of the
    /// world, a capsule embedded in solid geometry, and a hole with no direction that leads out
    /// of it. All three are detected, and a ravine defeats detection by not looking like a
    /// trap: the floor of a gully is somewhere you can walk, at length, in both directions, so
    /// the player is moving, every direction is open, and nothing is ever inside anything. You
    /// can pace a trap forever without satisfying any test for being in one.
    ///
    /// Rather than keep widening a detector against an unbounded set of shapes a generated
    /// world can produce, this hands the player the answer. Held, not tapped, and with the
    /// progress shown, because an instant teleport on a key press is something you trigger by
    /// accident and then cannot undo.
    ///
    /// The anchor is a spawn marker, not the last safe position: the whole premise is that the
    /// player's recent history is inside the trap, so anything derived from it is suspect.
    /// Markers are authored, are on the walkable network by construction, and exist in every
    /// scene the player can be standing in. The marker is still checked before it is used —
    /// authored is not the same as clear, and the one thing a last resort must not do is put
    /// the player somewhere they need rescuing from again.
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
        private float _held;

        private void Awake()
        {
            _player = GetComponent<PlayerLocomotion>();
        }

        private void Update()
        {
            // Only while the player actually owns the character. During a cutscene it is being
            // moved by a script, and a rescue in the middle of that would strand the scene
            // rather than the player.
            //
            // This used to ask the input reader whether input was on, and that made the escape
            // unreachable at precisely the moment it was needed. Walking into the shoreline
            // opens a prompt box; the box turns input off; the last resort turned itself off
            // with it. So the player was against the water, unable to move, holding the key
            // that exists for exactly that situation, and nothing happened. A box on the screen
            // is not a scene taking the character away, and only the second of those is a
            // reason to refuse.
            if (!PlayerLocomotion.PlayerOwnsCharacter) { _held = 0f; return; }

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

            // A marker is on the walkable network by construction, but construction was a while
            // ago: the scatter, a prop or a shoreline wall may have grown over it since, and a
            // rescue that puts the player inside a collider swaps a trap they understand for
            // one they do not. Asking the locomotion means the last resort is held to the same
            // standard as everything else that places the player.
            if (!_player.TryResolveStandingPosition(anchor.position, out var destination))
            {
                Debug.LogWarning($"[Player] Asked to be rescued to '{anchor.name}' at " +
                                 $"{anchor.position}, and there is nothing clear to stand on " +
                                 "anywhere near it. The marker needs moving, and until it is " +
                                 "moved a fresh game starts inside something too.", this);
                return;
            }

            Debug.Log($"[Player] Rescued from {transform.position} to '{anchor.name}' at " +
                      $"{destination}. If this happens twice in the same place, the level " +
                      "has a trap there rather than the player having wandered into one.");

            _player.Warp(destination, anchor.rotation);
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
