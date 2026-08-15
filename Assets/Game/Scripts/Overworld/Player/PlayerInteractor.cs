using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Finds the thing in front of the player and offers it to the UI as a prompt.
    ///
    /// A single forward ray misses small targets and anything the player is standing beside, so
    /// this combines a forward sphere cast (what you are looking at) with an overlap sweep (what
    /// is within arm's reach), then scores candidates by a blend of distance and angle. That
    /// scoring is why walking up to an NPC at an angle still highlights them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerInteractor : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private OverworldInputReader _input;
        [SerializeField] private PlayerAnimationDriver _animation;
        [Tooltip("Origin of the interaction ray. Use a chest-height child, not the feet.")]
        [SerializeField] private Transform _rayOrigin;

        [Header("Detection")]
        [SerializeField] private float _range = 2.6f;
        [SerializeField] private float _sphereRadius = 0.35f;
        [Tooltip("Widest angle off the facing that still counts as 'in front of me'.")]
        [SerializeField] private float _maxAngle = 100f;
        [SerializeField] private LayerMask _interactableMask = ~0;
        [Tooltip("Geometry that blocks an interaction — walls between you and a sign.")]
        [SerializeField] private LayerMask _occluderMask;
        [SerializeField] private int _maxCandidates = 12;

        [Header("Prompt output")]
        [Tooltip("Wire the UI worker's prompt widget here; nothing in this assembly draws.")]
        [SerializeField] private InteractionPromptEvent _promptChanged = new InteractionPromptEvent();

        private readonly Collider[] _overlap = new Collider[16];
        private IInteractable _current;
        private Transform _currentTransform;
        private bool _promptVisible;

        /// <summary>The interactable currently offered, or null.</summary>
        public IInteractable Current => _current;

        private void Awake()
        {
            if (_input == null) _input = GetComponentInParent<OverworldInputReader>();
            if (_animation == null) _animation = GetComponentInChildren<PlayerAnimationDriver>();
            if (_rayOrigin == null) _rayOrigin = transform;
        }

        private void Update()
        {
            ScanForTarget();

            if (_input != null && _input.InputEnabled && _input.InteractPressed && _current != null)
            {
                _input.ConsumeInteract();
                if (_animation != null) _animation.PlayInteract();
                _current.Interact(gameObject);
            }
        }

        private void ScanForTarget()
        {
            IInteractable best = null;
            Transform bestTransform = null;
            var bestScore = float.MaxValue;

            var origin = _rayOrigin.position;
            var facing = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

            var count = Physics.OverlapSphereNonAlloc(origin, _range, _overlap, _interactableMask, QueryTriggerInteraction.Collide);
            var considered = Mathf.Min(count, _maxCandidates);

            for (var i = 0; i < considered; i++)
            {
                var collider = _overlap[i];
                if (collider == null) continue;

                // GetComponentInParent so a collider on a child of an NPC still resolves the NPC.
                var interactable = collider.GetComponentInParent<IInteractable>();
                if (interactable == null || !interactable.CanInteract(gameObject)) continue;

                var point = collider.bounds.center;
                var toTarget = point - origin;
                var distance = toTarget.magnitude;
                if (distance > _range) continue;

                var planar = Vector3.ProjectOnPlane(toTarget, Vector3.up);
                var angle = planar.sqrMagnitude < 0.0001f ? 0f : Vector3.Angle(facing, planar);
                if (angle > _maxAngle * 0.5f) continue;

                if (IsOccluded(origin, point, distance)) continue;

                // Angle dominates distance: the thing you are facing wins over the thing that
                // happens to be a few centimetres closer off to the side.
                var score = distance + angle * 0.03f;
                if (score >= bestScore) continue;

                bestScore = score;
                best = interactable;
                bestTransform = collider.transform;
            }

            if (!ReferenceEquals(best, _current) || bestTransform != _currentTransform)
            {
                _current = best;
                _currentTransform = bestTransform;
                PublishPrompt();
            }
            else if (_promptVisible && _currentTransform != null)
            {
                // The world-space anchor moves with a walking NPC, so republish each frame while
                // a prompt is up. Cheap, and it keeps the UI marker glued to the target.
                PublishPrompt();
            }
        }

        private bool IsOccluded(Vector3 origin, Vector3 point, float distance)
        {
            if (_occluderMask.value == 0) return false;
            var direction = (point - origin) / Mathf.Max(0.0001f, distance);
            return Physics.Raycast(origin, direction, distance - 0.1f, _occluderMask, QueryTriggerInteraction.Ignore);
        }

        private void PublishPrompt()
        {
            var data = new InteractionPromptData
            {
                Visible = _current != null,
                Prompt = _current != null ? _current.InteractionPrompt : string.Empty,
                WorldPosition = _currentTransform != null ? _currentTransform.position : transform.position,
            };
            _promptVisible = data.Visible;
            _promptChanged.Invoke(data);
            OverworldEvents.RaiseInteractionPromptChanged(data);
        }

        /// <summary>Clears the prompt, e.g. when dialogue opens and the world stops being clickable.</summary>
        public void ClearPrompt()
        {
            _current = null;
            _currentTransform = null;
            PublishPrompt();
        }

        private void OnDrawGizmosSelected()
        {
            var origin = _rayOrigin != null ? _rayOrigin.position : transform.position;
            Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.25f);
            Gizmos.DrawWireSphere(origin, _range);
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(origin, transform.forward * _range);
        }
    }
}
