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

        private Collider _collider;
        private float _nextSpeakTime;

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
            var canSurf = SurfCapability.CanSurf();
            if (_collider.enabled == canSurf) _collider.enabled = !canSurf;
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
