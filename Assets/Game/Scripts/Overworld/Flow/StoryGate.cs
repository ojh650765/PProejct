using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>A story boundary across the road; terrain banks provide its visible edges.</summary>
    [DisallowMultipleComponent]
    public sealed class StoryGate : MonoBehaviour
    {
        [Tooltip("Flag that opens the way. Until it is set the line refuses the player.")]
        [SerializeField] private string _opensOnFlag = "story.gate_open";

        [Tooltip("Who explains the refusal. Optional — without one the line is silent, which " +
                 "reads as the player being stuck rather than as somebody stopping them.")]
        [SerializeField] private NpcController _keeper;

        [Tooltip("Width of the local passage, in metres.")]
        [SerializeField] private float _width = 12f;
        [SerializeField] private float _standOff = 0.6f;
        [SerializeField] private float _heightBelow = 3f;
        [SerializeField] private float _heightAbove = 4f;

        [Tooltip("Metres. How close the player gets before the keeper speaks up unprompted.")]
        [SerializeField] private float _speakRadius = 3.2f;

        [Tooltip("Metres beyond the speak radius the player must retreat before the keeper " +
                 "will say it again. Without the gap he re-triggers on every step taken on " +
                 "the boundary itself.")]
        [SerializeField] private float _rearmMargin = 1.8f;

        private Transform _player;
        private bool _spoken;

        private BoxCollider _barrier;

        public void Configure(string opensOnFlag, NpcController keeper, float width, float standOff)
        {
            _opensOnFlag = opensOnFlag ?? "";
            _keeper = keeper;
            _width = width > 0f ? width : 12f;
            _standOff = standOff;
        }

        private void Awake()
        {
            // Runtime only: the passage remains connected in the baked navigation mesh.
            // The terrain banks close the sides. A distant player is never repositioned.
            foreach (var old in GetComponents<UnityEngine.AI.NavMeshObstacle>()) Destroy(old);
            _barrier = GetComponent<BoxCollider>();
            if (_barrier == null) _barrier = gameObject.AddComponent<BoxCollider>();
            _barrier.isTrigger = false;
            _barrier.center = new Vector3(0f, (_heightAbove - _heightBelow) * .5f, -_standOff + .25f);
            _barrier.size = new Vector3(_width > 0f ? _width : 12f, _heightAbove + _heightBelow, .5f);
            _barrier.enabled = !IsOpen();
        }

        private void Update()
        {
            var open = IsOpen();
            if (_barrier != null) _barrier.enabled = !open;
            if (open) { _spoken = false; return; }
            Speak();
        }

        private bool ResolvePlayer()
        {
            if (_player != null) return true;

            var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
            if (found == null) return false;

            _player = found.transform;
            return true;
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
        /// so walking into the line and talking to him say the same thing — there is no second
        /// copy of the gating logic here to drift out of step with dialogue.json.
        /// </summary>
        private void Speak()
        {
            if (_keeper == null) return;
            if (StoryInterlude.Active) return;
            if (!ResolvePlayer()) return;

            var flat = _player.position - transform.position;
            flat.y = 0f;
            var distance = flat.magnitude;

            if (distance > _speakRadius + _rearmMargin) _spoken = false;
            if (_spoken || distance > _speakRadius) return;

            // Never over a scene or a conversation already in progress. The opening walks the
            // player to a mark a few metres from here and Bram speaks his own lines there as
            // part of it; a second, unscripted copy of him talking over that is the failure
            // this check exists for.
            var runner = EpisodeRunner.Live;
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

            // The refusal itself: the line the player is held at, drawn where it actually is.
            Gizmos.color = new Color(0.95f, 0.5f, 0.2f, 0.9f);
            var centre = transform.position - transform.forward * _standOff;
            var half = transform.right * (_width * 0.5f);
            var top = transform.up * _heightAbove;
            var bottom = -transform.up * _heightBelow;
            Gizmos.DrawLine(centre - half + bottom, centre + half + bottom);
            Gizmos.DrawLine(centre - half + top, centre + half + top);
            Gizmos.DrawLine(centre - half + bottom, centre - half + top);
            Gizmos.DrawLine(centre + half + bottom, centre + half + top);
        }
    }
}
