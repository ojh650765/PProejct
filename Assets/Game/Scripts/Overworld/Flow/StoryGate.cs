using PokeLab.Core;
using UnityEngine;

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
        }

        private void Awake()
        {
            _wall = GetComponent<BoxCollider>();
            _wall.isTrigger = false;
        }

        // The wall blocks the PLAYER and nobody else — the user's design, stated exactly:
        // "플레이어만 못 지나가게 하고 에이전트는 괜찮도록". The solid collider stops the
        // CharacterController; NavMesh agents ignore physics colliders and their mesh is
        // continuous through the gate (BuildStoryGate keeps this box out of the bake with a
        // NavMeshModifier — it used to be baked in, which severed the ramp permanently and
        // sent every departing agent into the fence beside it). There is deliberately NO
        // NavMeshObstacle and no carving: a scripted walk must never be re-routed by a story
        // rule aimed at the player. The accepted trade is visual — an agent crossing while
        // the gate is still closed would clip through the visible wall — and it is accepted
        // because no authored NPC route crosses the ramp before story.gate_open: Bram stands
        // beside it, the villagers' waypoints are all in-town, and Kes' exit runs only after
        // the same episode has set the flag.

        private void Update()
        {
            if (IsOpen())
            {
                // Left in the scene rather than destroyed, because the flag is per-save and
                // this object is per-scene load: a player who re-enters town on a save that has
                // not opened the gate needs the wall back.
                _wall.enabled = false;
                return;
            }

            _wall.enabled = true;
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
