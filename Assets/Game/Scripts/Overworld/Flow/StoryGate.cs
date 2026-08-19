using PokeLab.Core;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The wall across the way out, and the person standing beside it.
    ///
    /// Every flag the story sets had a consumer except one. <c>story.gate_open</c> is written
    /// by the last beat of <c>leaving_town</c> and nothing anywhere read it, so the ramp out of
    /// Aster Town was walkable from the first frame of a new game: a player who ignored the
    /// plaza entirely could be on the route, in tall grass, with no party, before the professor
    /// had finished speaking. Bram was authored to stop exactly that — he has three dialogue
    /// states in dialogue.json about it — and he had nothing to stop them with.
    ///
    /// A solid collider rather than a refusal on <see cref="LevelTransition"/>, because the
    /// route is streamed in beside the town rather than loaded through a door: the transition
    /// volume returns early when its destination is already resident, so gating it would gate
    /// nothing. The boundary the player can see is the ramp, and the ramp is what this closes.
    ///
    /// Not a story beat and deliberately not a <see cref="StoryEncounter"/>. An encounter fires
    /// once and retires; a gate has to keep refusing, every time, until the flag turns.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public sealed class StoryGate : MonoBehaviour
    {
        [Tooltip("Flag that opens the way. Until it is set the collider is solid.")]
        [SerializeField] private string _opensOnFlag = "story.gate_open";

        [Tooltip("Who explains the refusal. Optional — without one the wall is silent, which " +
                 "reads as level geometry rather than as somebody stopping you.")]
        [SerializeField] private NpcController _keeper;

        [Tooltip("Metres. How close the player gets before the keeper speaks up unprompted.")]
        [SerializeField] private float _speakRadius = 3.2f;

        [Tooltip("Metres beyond the speak radius the player must retreat before the keeper " +
                 "will say it again. Without the gap he re-triggers on every step taken on " +
                 "the boundary itself.")]
        [SerializeField] private float _rearmMargin = 1.8f;

        private BoxCollider _wall;
        private NavMeshObstacle _navBlock;
        private Transform _player;
        private bool _spoken;

        /// <summary>Set by the level builder. The town is generated; nothing here is hand-placed.</summary>
        public void Configure(string opensOnFlag, NpcController keeper, Vector3 size)
        {
            _opensOnFlag = opensOnFlag ?? "";
            _keeper = keeper;

            _wall = GetComponent<BoxCollider>();
            _wall.size = size;
            // Sunk a metre. The object is seated on the ground under its own centre, and the
            // ramp is a slope: a box that starts exactly at that height floats clear of the
            // ground at its downhill end, and a gate with daylight under it is a gate the
            // player walks through.
            _wall.center = new Vector3(0f, size.y * 0.5f - 1f, 0f);
            _wall.isTrigger = false;
            EnsureNavBlock();
        }

        private void Awake()
        {
            _wall = GetComponent<BoxCollider>();
            _wall.isTrigger = false;
            EnsureNavBlock();
        }

        /// <summary>
        /// Stands a carving NavMeshObstacle in the wall's own box, added at runtime so no
        /// scene rewrite is needed.
        ///
        /// The collider blocks the player's body; this is what blocks an agent's PATH, and it
        /// is the half that was missing. The gate used to be baked straight into the navmesh —
        /// its collider sits on a layer BuildNavigation collects — so the ramp under it was a
        /// permanent hole in the town mesh: disabling the collider let the player through and
        /// let no agent through, ever, and Kes' walk out of town turned right at the fence
        /// because the ramp was the one place his path could not go. The builder now keeps the
        /// wall out of the bake (NavMeshModifier, see BuildStoryGate) and this obstacle carves
        /// the same box only while the gate is genuinely closed.
        /// </summary>
        private void EnsureNavBlock()
        {
            if (_navBlock == null) _navBlock = GetComponent<NavMeshObstacle>();
            if (_navBlock == null) _navBlock = gameObject.AddComponent<NavMeshObstacle>();
            _navBlock.shape = NavMeshObstacleShape.Box;
            _navBlock.carving = true;
            _navBlock.size = _wall.size;
            _navBlock.center = _wall.center;
        }

        private void Update()
        {
            if (IsOpen())
            {
                // Left in the scene rather than destroyed, because the flag is per-save and
                // this object is per-scene load: a player who re-enters town on a save that has
                // not opened the gate needs the wall back.
                _wall.enabled = false;
                if (_navBlock != null) _navBlock.enabled = false;
                return;
            }

            _wall.enabled = true;
            if (_navBlock != null) _navBlock.enabled = true;
            Speak();
        }

        private bool IsOpen()
        {
            if (string.IsNullOrEmpty(_opensOnFlag)) return true;
            var profile = ServiceHub.TryGet<IPlayerProfile>(out var found) ? found as PlayerProfile : null;
            // No profile yet means the game is still booting, not that the gate is open.
            return profile != null && profile.GetFlagBool(_opensOnFlag);
        }

        /// <summary>
        /// Has the keeper say why, once per approach.
        ///
        /// Which of his three lines he says is decided by NpcController against the same flags,
        /// so walking into the wall and talking to him say the same thing — there is no second
        /// copy of the gating logic here to drift out of step with dialogue.json.
        /// </summary>
        private void Speak()
        {
            if (_keeper == null) return;

            if (_player == null)
            {
                var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
                if (found == null) return;
                _player = found.transform;
            }

            var flat = _player.position - transform.position;
            flat.y = 0f;
            var distance = flat.magnitude;

            if (distance > _speakRadius + _rearmMargin) _spoken = false;
            if (_spoken || distance > _speakRadius) return;

            // Never over a scene or a conversation already in progress. The opening walks the
            // player to a mark a few metres from here and Bram speaks his own lines there as
            // part of it; a second, unscripted copy of him talking over that is the failure
            // this check exists for.
            var runner = FindFirstObjectByType<EpisodeRunner>();
            if (runner != null && runner.IsPlaying) return;
            var dialogue = DialogueRunner.Instance;
            if (dialogue != null && dialogue.IsPlaying) return;

            _spoken = true;
            _keeper.Interact(_player.gameObject);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.9f, 0.3f, 0.3f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, _speakRadius);
        }
    }
}
