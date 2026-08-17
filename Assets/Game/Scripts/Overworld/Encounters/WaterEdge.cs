using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The wall along a waterline, and the line the player is told when they walk into it.
    ///
    /// Turning the player back *after* they had entered was the first attempt and it is the
    /// wrong shape: whatever the correction, the player has already stepped into the lake
    /// and been moved by something they did not do, which reads as the game glitching rather
    /// than as a boundary. The series never lets the step happen at all. So this is a solid
    /// collider standing on the shoreline — the player simply stops, exactly as they stop at
    /// a fence — and a message explains why.
    ///
    /// The wall is removed, not opened, once the player has a Pokémon that can carry them.
    /// A blocker that stays and is selectively ignored has to be right about who is allowed
    /// through on every frame; one that is gone cannot be wrong.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public sealed class WaterEdge : MonoBehaviour
    {
        [Tooltip("Seconds before the same shoreline will speak again, so walking along it " +
                 "does not repeat the line every step.")]
        [SerializeField] private float _repeatDelay = 6f;

        [SerializeField] private string _sequenceId = "water_needs_surf";

        [Tooltip("String table key. The text itself lives in strings.json so it exists in " +
                 "every language the game ships, not just the one it was typed in.")]
        [SerializeField] private string _lineKey = "water.needs_surf";

        [Tooltip("Said instead once the player has a Pokémon that could swim but the wall is " +
                 "still up — which should not happen, and is worth hearing if it does.")]
        [SerializeField] private string _unexpectedKey = "water.blocked_unexpected";

        private readonly Collider[] _overlaps = new Collider[16];
        private Collider _collider;
        private CharacterController _player;
        private float _nextSpeakTime;
        private bool _reportedDeferral;

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            _collider.isTrigger = false;
        }

        private void Update()
        {
            // Polled rather than driven by an event because the party can change from a
            // catch, a faint, a swap or a heal, and there is no single signal for "the party
            // changed" that all four raise.
            var wanted = !SurfCapability.CanSurf();
            if (_collider.enabled == wanted) return;

            // Raising the wall through the player is the one thing this must never do. A
            // CharacterController cannot leave a solid it is already inside — there is no
            // depenetration pass, only a sweep that refuses — so the party fainting its only
            // swimmer while the player is out over the water would close a wall around them
            // and leave them unable to move in any direction at all. Waiting costs nothing:
            // the wall goes up on the first frame they are clear of it, and until then they
            // are on the water, which is the state the wall is there to end rather than to
            // punish.
            if (wanted && StandingInTheWall()) return;

            _collider.enabled = wanted;
            _reportedDeferral = false;
        }

        /// <summary>
        /// Whether the player's capsule is inside the curtain as it stands.
        ///
        /// The collider has to be enabled to be asked, because a disabled one is not in the
        /// physics scene and no query will report it, so it is switched on for the length of
        /// the query and switched back. The alternative is its bounding box, and the bounding
        /// box of a wall that follows a lake shore is the whole lake — which would hold the
        /// wall down every time the player walked near the water, which is every time it
        /// matters.
        /// </summary>
        private bool StandingInTheWall()
        {
            if (_player == null)
            {
                var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
                _player = found != null ? found.GetComponentInParent<CharacterController>() : null;
                if (_player == null) return false;
            }

            // Off the controller's own centre, which on this rig is most of a metre above the
            // transform: the transform sits at the player's feet and the capsule does not.
            var spine = Mathf.Max(0f, _player.height * 0.5f - _player.radius);
            var centre = _player.transform.position + _player.transform.rotation * _player.center;
            var bottom = centre - Vector3.up * spine;
            var top = centre + Vector3.up * spine;

            _collider.enabled = true;
            var count = Physics.OverlapCapsuleNonAlloc(bottom, top, _player.radius, _overlaps,
                ~0, QueryTriggerInteraction.Ignore);
            var inside = false;
            for (var i = 0; i < count && !inside; i++) inside = _overlaps[i] == _collider;
            _collider.enabled = false;

            if (inside && !_reportedDeferral)
            {
                _reportedDeferral = true;
                Debug.Log($"[WaterEdge] Held '{name}' down: the player is standing in it, and " +
                          "closing it around them would wedge them there. It goes up as soon " +
                          "as they are clear.", this);
            }
            return inside;
        }

        /// <summary>Called by the player when they bump into this wall.</summary>
        public void Prompt()
        {
            if (Time.time < _nextSpeakTime) return;
            _nextSpeakTime = Time.time + Mathf.Max(0.5f, _repeatDelay);

            var runner = DialogueRunner.Instance;
            if (runner == null || runner.IsPlaying) return;

            var text = PokeLab.Core.Loc.Get(SurfCapability.CanSurf() ? _unexpectedKey : _lineKey);
            var sequence = DialogueSequence.FromLines(_sequenceId, new DialogueLine
            {
                SpeakerName = string.Empty,
                Text = text,
                AutoAdvanceSeconds = 0f,
            });
            runner.Play(sequence, gameObject);
        }
    }
}
