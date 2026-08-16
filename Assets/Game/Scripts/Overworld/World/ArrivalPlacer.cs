using UnityEngine;

namespace PokeLab.Overworld.World
{
    /// <summary>
    /// Puts the player down on the marker the door they walked through asked for.
    ///
    /// This is the half of the transition that was missing. <see cref="LevelTransition"/> has
    /// published <c>PendingArrivalSpawn</c> since it was written and its own comment says a
    /// frame is left "so the arrival marker has repositioned the player" — but a search of
    /// Assets/Game/Scripts for that property found the door that sets it and nothing that
    /// reads it. Every <c>Spawn_From*</c> marker the level builder places was therefore
    /// scenery: walking from the town to the field put the player wherever that scene's rig
    /// happened to be serialized, which is its own spawn, and the arrival was only ever right
    /// because both scenes are emitted into one world space and the gate is streamed rather
    /// than loaded. A building interior has no such luck — it is its own coordinate space, and
    /// without this the player arrives at the town's plaza coordinates, outside the room.
    ///
    /// The marker carries the facing as well as the position. Coming back out of a door
    /// looking at the door you just left is the moment that reads as a bug, so the marker
    /// outside a building faces away from it and the player is turned with it.
    ///
    /// The screen is covered for the frame the move happens in and then revealed. The cover
    /// the door raised belongs to the scene that has just been destroyed, so on this side
    /// there is nothing to fade out of — the new scene's overlay starts clear, which is a hard
    /// cut into a room. Re-covering here and revealing is what makes the two halves one
    /// transition.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArrivalPlacer : MonoBehaviour
    {
        [Tooltip("Seconds the arrival fades in over. Matches LevelTransition's own fade in, " +
                 "so a door feels the same length whichever side of it you are on.")]
        [SerializeField] private float _revealSeconds = 0.55f;

        private void Start()
        {
            // Consumed whether or not it can be honoured. A pending spawn left standing is one
            // that fires on the next load as well, and a stale arrival is worse than none: the
            // player would be moved somewhere for a reason that no longer exists.
            var wanted = LevelTransition.PendingArrivalSpawn;
            LevelTransition.PendingArrivalSpawn = null;
            if (string.IsNullOrEmpty(wanted)) return;

            var marker = GameObject.Find(wanted);
            if (marker == null)
            {
                Debug.LogWarning(
                    $"[Arrival] '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}' " +
                    $"has no marker called '{wanted}', so the player is left at this scene's own " +
                    "spawn. The scene that sent them here names the marker; build it there.", this);
                return;
            }

            var player = FindFirstObjectByType<PlayerLocomotion>();
            if (player == null)
            {
                Debug.LogWarning("[Arrival] There is no player in this scene to place.", this);
                return;
            }

            // Warp rather than assigning the transform: the CharacterController caches its own
            // position and writing straight to the transform is undone on its next move, which
            // walks the player back to where they were standing before the load.
            player.Warp(marker.transform.position, marker.transform.rotation);

            if (!PokeLab.Core.ServiceHub.TryGet<PokeLab.Core.IScreenCover>(out var cover)
                || cover == null)
            {
                return;
            }

            // Covered over a hundredth of a second rather than instantly, because the host
            // clamps a zero duration to that anyway, and revealed from the callback so the
            // reveal cannot start before the cover has finished and leave a half-lit frame.
            cover.Cover(0.01f, () => cover.Reveal(_revealSeconds, null));
        }
    }
}
